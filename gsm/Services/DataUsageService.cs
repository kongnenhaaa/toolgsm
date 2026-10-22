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
/// the HTTP server for an exact byte range. The modem downloads the response
/// into its own HTTP buffer, so no temporary file is written to the PC.
/// </summary>
public sealed class DataUsageService
{
    private const string DownloadUrl = "http://speedtest.tele2.net/1MB.zip";
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
            string activateResponse = await _modem.SendCommandAsync(
                portName,
                "AT+QIACT=1",
                timeoutMs: 30_000,
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

        string rangeSupport = await _modem.SendCommandAsync(
            portName,
            "AT+QHTTPGETEX=?",
            timeoutMs: 5000,
            silent: true,
            ct: cancellationToken).ConfigureAwait(false);
        EnsureCommandSucceeded(rangeSupport, "kiểm tra tải theo dung lượng");
        if (!rangeSupport.Contains("QHTTPGET", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Firmware trên {portName} không hỗ trợ tải đúng dung lượng bằng QHTTPGETEX.");
        }

        string urlPrompt = await _modem.SendCommandAsync(
            portName,
            $"AT+QHTTPURL={DownloadUrl.Length},20",
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
            DownloadUrl,
            timeoutMs: 15_000,
            silent: true).ConfigureAwait(false);
        EnsureCommandSucceeded(urlResponse, "gửi URL tải dữ liệu");

        int requestedBytes = checked(kilobytes * 1024);
        HttpDownloadResult httpResult = await SendRangeRequestAsync(
            portName,
            requestedBytes,
            cancellationToken).ConfigureAwait(false);

        if (httpResult.ErrorCode != 0)
        {
            throw new InvalidOperationException(
                $"Modem báo lỗi HTTP {httpResult.ErrorCode}.");
        }
        if (httpResult.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"Máy chủ tải dữ liệu trả HTTP {httpResult.StatusCode}.");
        }

        string message = httpResult.ContentLength is > 0
            ? $"Đã dùng {FormatKilobytes(httpResult.ContentLength.Value)} KB qua dữ liệu di động."
            : $"Đã gửi yêu cầu dùng {kilobytes:N0} KB qua dữ liệu di động.";

        if (httpResult.ContentLength is > 0
            && httpResult.ContentLength.Value != requestedBytes)
        {
            message += $" Yêu cầu ban đầu: {kilobytes:N0} KB.";
        }

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

    private async Task<HttpDownloadResult> SendRangeRequestAsync(
        string portName,
        int requestedBytes,
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
                $"AT+QHTTPGETEX=80,0,{requestedBytes}",
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
