using System.Text.RegularExpressions;
using gsm.Models;

namespace gsm.Services;

public sealed record DataUsageRunResult(
    int RequestedKilobytes,
    int? ReportedBytes,
    int HttpStatusCode,
    string Message);

/// <summary>
/// Creates a small packet-data session on supported Quectel modems and asks
/// the HTTP server for an exact byte count. The modem downloads the response
/// into its own HTTP buffer, so no temporary file is written to the PC.
/// </summary>
public sealed class DataUsageService
{
    private const string DownloadUrlBase = "http://speed.cloudflare.com/__down?bytes=";
    public const int MinimumKilobytes = 1;
    public const int MaximumKilobytes = 1024;

    private static readonly Regex HttpResultRegex = new(
        @"\+QHTTPGET:\s*(?<error>\d+)(?:\s*,\s*(?<status>\d+))?(?:\s*,\s*(?<length>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IGsmModemService _modem;

    public DataUsageService(IGsmModemService modem)
    {
        _modem = modem;
    }

    public async Task<DataUsageRunResult> ConsumeAsync(
        string portName,
        int kilobytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("Cổng COM không hợp lệ.", nameof(portName));
        if (kilobytes is < MinimumKilobytes or > MaximumKilobytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kilobytes),
                $"Dung lượng phải từ {MinimumKilobytes} đến {MaximumKilobytes} KB.");
        }
        if (!_modem.IsPortOpen(portName))
            throw new InvalidOperationException($"{portName} chưa được mở.");
        if (_modem.GetModemProfile(portName)?.Supports(ModemCapability.HttpData) != true)
        {
            throw new NotSupportedException(
                $"Modem trên {portName} không hỗ trợ bộ lệnh HTTP Data của Quectel.");
        }

        using IDisposable backgroundLease =
            _modem.SuspendPortBackgroundOperations(portName);

        cancellationToken.ThrowIfCancellationRequested();
        int requestedBytes = checked(kilobytes * 1024);
        string downloadUrl = DownloadUrlBase + requestedBytes;

        string contextState = await _modem.SendCommandAsync(
            portName,
            "AT+QIACT?",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(contextState, "kiểm tra kết nối dữ liệu");

        if (!Regex.IsMatch(
                contextState,
                @"\+QIACT:\s*1\s*,\s*1(?:\s*,|\r|\n|$)",
                RegexOptions.IgnoreCase))
        {
            await EnsurePacketNetworkRegisteredAsync(portName, cancellationToken)
                .ConfigureAwait(false);
            await ConfigureApnIfMissingAsync(portName, cancellationToken)
                .ConfigureAwait(false);
            string activateResponse = await _modem.SendCommandAsync(
                portName,
                "AT+QIACT=1",
                timeoutMs: 150_000,
                silent: true,
                ct: cancellationToken).ConfigureAwait(false);
            EnsureCommandSucceeded(activateResponse, "kích hoạt dữ liệu di động");
        }

        await SendRequiredCommandAsync(
            portName,
            "AT+QHTTPCFG=\"contextid\",1",
            5000,
            "chọn PDP context",
            cancellationToken).ConfigureAwait(false);
        await SendRequiredCommandAsync(
            portName,
            "AT+QHTTPCFG=\"requestheader\",0",
            5000,
            "cấu hình HTTP request",
            cancellationToken).ConfigureAwait(false);
        await SendRequiredCommandAsync(
            portName,
            "AT+QHTTPCFG=\"responseheader\",0",
            5000,
            "cấu hình HTTP response",
            cancellationToken).ConfigureAwait(false);

        string urlPrompt = await _modem.SendCommandAsync(
            portName,
            $"AT+QHTTPURL={downloadUrl.Length},20",
            timeoutMs: 25_000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        if (!urlPrompt.Contains("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Không mở được phiên nhập URL: {Compact(urlPrompt)}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string urlResponse = await _modem.SendRawAsync(
            portName,
            downloadUrl,
            timeoutMs: 15_000,
            silent: true).ConfigureAwait(false);
        EnsureCommandSucceeded(urlResponse, "gửi URL tải dữ liệu");

        HttpDownloadResult httpResult = await SendDownloadRequestAsync(
            portName,
            cancellationToken).ConfigureAwait(false);

        if (httpResult.ErrorCode != 0)
        {
            string reason = httpResult.ErrorCode switch
            {
                702 => "Hết thời gian chờ máy chủ tải dữ liệu",
                714 => "Modem không phân giải được tên máy chủ tải dữ liệu (DNS)",
                _ => "Modem báo lỗi HTTP"
            };
            throw new InvalidOperationException($"{reason}: {httpResult.ErrorCode}.");
        }
        if (httpResult.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"Máy chủ tải dữ liệu trả HTTP {httpResult.StatusCode}.");
        }

        if (httpResult.ContentLength != requestedBytes)
        {
            throw new InvalidOperationException(
                $"Modem báo tải {httpResult.ContentLength?.ToString() ?? "không rõ"} byte, "
                + $"khác {requestedBytes} byte yêu cầu; không xác nhận phát sinh cước.");
        }

        string message = $"Đã tải {FormatKilobytes(requestedBytes)} KB qua dữ liệu di động.";

        return new DataUsageRunResult(
            kilobytes,
            httpResult.ContentLength,
            httpResult.StatusCode,
            message);
    }

    private async Task SendRequiredCommandAsync(
        string portName,
        string command,
        int timeoutMs,
        string action,
        CancellationToken cancellationToken)
    {
        string response = await _modem.SendCommandAsync(
            portName,
            command,
            timeoutMs,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(response, action);
    }

    private async Task EnsurePacketNetworkRegisteredAsync(
        string portName,
        CancellationToken cancellationToken)
    {
        string eps = await _modem.SendCommandAsync(
            portName,
            "AT+CEREG?",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        int? epsStatus = ParseRegistrationStatus(eps, "CEREG");
        if (epsStatus is 1 or 5) return;

        string packet = await _modem.SendCommandAsync(
            portName,
            "AT+CGREG?",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        int? packetStatus = ParseRegistrationStatus(packet, "CGREG");
        if (packetStatus is 1 or 5) return;

        if (epsStatus.HasValue && packetStatus.HasValue)
        {
            throw new InvalidOperationException(
                $"{portName} chưa đăng ký mạng dữ liệu "
                + $"(CEREG={epsStatus}, CGREG={packetStatus}); kiểm tra SIM và nhà mạng.");
        }
    }

    private static int? ParseRegistrationStatus(string response, string command)
    {
        Match match = Regex.Match(
            response,
            $@"\+{command}:\s*\d+\s*,\s*(?<status>[0-5])\b",
            RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups["status"].Value) : null;
    }

    private async Task ConfigureApnIfMissingAsync(
        string portName,
        CancellationToken cancellationToken)
    {
        string settings = await _modem.SendCommandAsync(
            portName,
            "AT+QICSGP=1",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(settings, "đọc cấu hình APN");
        Match configuredApn = Regex.Match(
            settings,
            "\\+QICSGP:\\s*\\d+\\s*,\\s*\"(?<apn>[^\"]*)\"",
            RegexOptions.IgnoreCase);
        if (!configuredApn.Success)
            throw new InvalidOperationException($"Không đọc được APN trên {portName}: {Compact(settings)}");
        if (!string.IsNullOrWhiteSpace(configuredApn.Groups["apn"].Value))
            return;

        string imsiResponse = await _modem.SendCommandAsync(
            portName,
            "AT+CIMI",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(imsiResponse, "xác định nhà mạng cho APN");
        Match imsi = Regex.Match(imsiResponse, @"(?m)^\s*(452\d{10,12})\s*$");
        string network = imsi.Success ? imsi.Groups[1].Value[..5] : string.Empty;
        (string apn, string username, string password, int authentication) = network switch
        {
            "45201" => ("m-wap", "", "", 0),
            "45202" => ("m3-world", "mms", "mms", 1),
            "45204" => ("v-internet", "", "", 0),
            "45205" => ("internet", "", "", 0),
            _ => throw new InvalidOperationException(
                $"{portName} chưa có APN và không xác định được nhà mạng từ IMSI.")
        };
        await SendRequiredCommandAsync(
            portName,
            $"AT+QICSGP=1,1,\"{apn}\",\"{username}\",\"{password}\",{authentication}",
            5000,
            "cấu hình APN",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpDownloadResult> SendDownloadRequestAsync(
        string portName,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<HttpDownloadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLogMessage(object? sender, GsmDataEventArgs args)
        {
            if (!args.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase))
                return;

            Match match = HttpResultRegex.Match(args.Data ?? string.Empty);
            if (!match.Success) return;

            int error = int.Parse(match.Groups["error"].Value);
            int status = match.Groups["status"].Success
                ? int.Parse(match.Groups["status"].Value)
                : 0;
            int? length = match.Groups["length"].Success
                ? int.Parse(match.Groups["length"].Value)
                : null;
            completion.TrySetResult(new HttpDownloadResult(error, status, length));
        }

        _modem.LogMessage += OnLogMessage;
        try
        {
            string response = await _modem.SendCommandAsync(
                portName,
                "AT+QHTTPGET=80",
                timeoutMs: 15_000,
                silent: true,
                ct: cancellationToken).ConfigureAwait(false);
            EnsureCommandSucceeded(response, "bắt đầu tải dữ liệu");

            try
            {
                return await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(90),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"{portName} không trả kết quả tải dữ liệu sau 90 giây.");
            }
        }
        finally
        {
            _modem.LogMessage -= OnLogMessage;
        }
    }

    private static void EnsureCommandSucceeded(string response, string action)
    {
        if (!string.IsNullOrWhiteSpace(response)
            && response.Contains("OK", StringComparison.OrdinalIgnoreCase)
            && !IsFailure(response))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Không thể {action}: {Compact(response)}");
    }

    private static bool IsFailure(string response) =>
        response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || response.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
        || response.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase);

    private static string Compact(string? response)
    {
        string value = Regex.Replace(response ?? string.Empty, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(value) ? "không có phản hồi" : value;
    }

    private static string FormatKilobytes(int bytes) =>
        (bytes / 1024d).ToString("N1");

    private sealed record HttpDownloadResult(
        int ErrorCode,
        int StatusCode,
        int? ContentLength);
}
