using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using gsm.Models;
using gsm.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace gsm.Services;

/// <summary>
/// Small HTTP bridge for trusted tools running on the same computer. By
/// default Kestrel only binds 127.0.0.1 and never enables CORS. A non-loopback
/// bind is allowed only when TOOLGSM_API_TOKEN is configured.
/// </summary>
public sealed class ToolGsmApiService
{
    public const string DefaultUrl = "http://127.0.0.1:17890";
    public const string SendSmsPath = "/api/v1/sms/send";
    public const string HealthPath = "/api/v1/health";
    private const string RequiredClientHeader = "ZaloTool";
    private const string RequiredPurpose = "zalo-manual-mo";
    private static readonly Regex RequestIdPattern = new(
        "^[A-Za-z0-9_-]{8,80}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ZaloMoPattern = new(
        @"^\[Zalo\]\s+[A-Za-z0-9_-]{8,160}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IToolGsmApiHost _host;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<ToolGsmSmsResponse>>> _requests =
            new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken _serviceCancellation = CancellationToken.None;

    public ToolGsmApiService(MainViewModel viewModel)
        : this(new MainViewModelToolGsmApiHost(viewModel))
    {
    }

    internal ToolGsmApiService(IToolGsmApiHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string[] urls = ResolveUrls();
        string apiToken = (Environment.GetEnvironmentVariable("TOOLGSM_API_TOKEN")
            ?? string.Empty).Trim();
        bool loopbackOnly = urls.All(IsLoopbackUrl);
        if (!loopbackOnly && apiToken.Length < 24)
        {
            throw new InvalidOperationException(
                "TOOLGSM_API_TOKEN phải dài ít nhất 24 ký tự khi API bind ngoài localhost.");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(ToolGsmApiService).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(urls);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
        });

        WebApplication app = builder.Build();
        app.MapGet(HealthPath, (HttpContext context) =>
        {
            IResult? denied = Authorize(context, apiToken);
            if (denied != null) return denied;
            return Results.Json(new
            {
                ok = true,
                service = "ToolGSM",
                transport = loopbackOnly ? "localhost" : "web",
                activePorts = _host.GetPorts().Count(port => port.Ready)
            });
        });
        app.MapPost(SendSmsPath, async (HttpContext context) =>
        {
            IResult? denied = Authorize(context, apiToken);
            if (denied != null) return denied;
            if (!context.Request.HasJsonContentType())
            {
                return Results.Json(
                    ToolGsmSmsResponse.Failure(
                        "invalid_content_type",
                        "Content-Type phải là application/json.",
                        StatusCodes.Status415UnsupportedMediaType),
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            ToolGsmSmsRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ToolGsmSmsRequest>(
                    cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request == null)
            {
                return Results.Json(
                    ToolGsmSmsResponse.Failure(
                        "invalid_json",
                        "Request JSON không hợp lệ.",
                        StatusCodes.Status400BadRequest),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            ToolGsmSmsResponse response = await SubmitSmsAsync(
                request, context.RequestAborted);
            return Results.Json(response, statusCode: response.HttpStatusCode);
        });

        _serviceCancellation = cancellationToken;
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _host.Log(
                $"[LOCAL_API_READY] {string.Join(", ", urls)}; SMS MO bridge sẵn sàng.",
                "SUCCESS");
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await app.StopAsync(stopCts.Token).ConfigureAwait(false); }
            catch { }
            await app.DisposeAsync().ConfigureAwait(false);
            _serviceCancellation = CancellationToken.None;
        }
    }

    internal async Task<ToolGsmSmsResponse> SubmitSmsAsync(
        ToolGsmSmsRequest request,
        CancellationToken waitCancellation = default)
    {
        ToolGsmSmsResponse? invalid = Validate(request);
        if (invalid != null) return invalid;

        string requestId = request.RequestId.Trim();
        CancellationToken operationCancellation = _serviceCancellation.CanBeCanceled
            ? _serviceCancellation
            : CancellationToken.None;
        var candidate = new Lazy<Task<ToolGsmSmsResponse>>(
            () => ExecuteSmsAsync(request, operationCancellation),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<ToolGsmSmsResponse>> selected = _requests.GetOrAdd(
            requestId, candidate);
        bool duplicate = !ReferenceEquals(candidate, selected);
        ToolGsmSmsResponse response = await selected.Value
            .WaitAsync(waitCancellation)
            .ConfigureAwait(false);
        // A definitely failed operation is safe to retry with the same id.
        // Keep successful and uncertain submissions cached because retrying
        // either of those could charge/send the same SMS twice.
        if (string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase)
            && _requests.TryGetValue(requestId, out Lazy<Task<ToolGsmSmsResponse>>? current)
            && ReferenceEquals(current, selected))
        {
            _requests.TryRemove(requestId, out _);
        }
        TrimCompletedRequests();
        return duplicate ? response with { Duplicate = true } : response;
    }

    private async Task<ToolGsmSmsResponse> ExecuteSmsAsync(
        ToolGsmSmsRequest request,
        CancellationToken cancellationToken)
    {
        string sourcePhone = MyVnptService.NormalizePhone(request.SourcePhone);
        string destination = NormalizeVietnamPhone(request.Destination);
        IReadOnlyList<ToolGsmApiPort> matching = _host.GetPorts()
            .Where(port => string.Equals(
                MyVnptService.NormalizePhone(port.PhoneNumber),
                sourcePhone,
                StringComparison.Ordinal))
            .Where(port => string.IsNullOrWhiteSpace(request.PortName)
                || string.Equals(
                    port.PortName,
                    request.PortName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(port => port.PhysicalIndex)
            .ThenBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matching.Count == 0)
        {
            return ToolGsmSmsResponse.Failure(
                "source_phone_not_found",
                "ToolGSM không tìm thấy SIM có số điện thoại đang đăng ký.",
                StatusCodes.Status404NotFound,
                request.RequestId);
        }

        ToolGsmApiPort[] ready = matching.Where(port => port.Ready).ToArray();
        if (ready.Length == 0)
        {
            return ToolGsmSmsResponse.Failure(
                "source_phone_not_ready",
                "Đúng SIM đã được tìm thấy nhưng COM chưa Active/sẵn sàng.",
                StatusCodes.Status409Conflict,
                request.RequestId);
        }
        if (ready.Length > 1)
        {
            return ToolGsmSmsResponse.Failure(
                "source_phone_ambiguous",
                "Có nhiều COM Active cùng số điện thoại; hãy chỉ định portName để tránh gửi trùng.",
                StatusCodes.Status409Conflict,
                request.RequestId);
        }

        ToolGsmApiPort selected = ready[0];
        _host.UpsertQueue(
            request.RequestId,
            selected.PortName,
            destination,
            request.Message,
            "Đang gửi",
            null,
            null);
        _host.Log(
            $"[{selected.PortName}] [ZALO_MO_API] request={request.RequestId}; bắt đầu gửi tới {destination}.",
            "INFO");

        string result;
        try
        {
            result = await _host.SendSmsAsync(
                selected.PortName,
                destination,
                request.Message,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = "ERROR: SMS operation cancelled";
        }
        catch (Exception ex)
        {
            result = $"ERROR: {ex.Message}";
        }

        SmsSubmitDisposition disposition = GsmSmsService.ClassifySubmitResult(result);
        string status = disposition switch
        {
            SmsSubmitDisposition.Confirmed => "sent",
            SmsSubmitDisposition.PayloadSubmittedUncertain => "maybe_sent",
            _ => "failed"
        };
        bool ok = disposition == SmsSubmitDisposition.Confirmed;
        string? error = ok ? null : result;
        _host.UpsertQueue(
            request.RequestId,
            selected.PortName,
            destination,
            request.Message,
            status,
            result,
            error);
        _host.Log(
            $"[{selected.PortName}] [ZALO_MO_API_{status.ToUpperInvariant()}] request={request.RequestId}.",
            ok ? "SUCCESS" : status == "maybe_sent" ? "WARN" : "ERROR");

        return new ToolGsmSmsResponse
        {
            Ok = ok,
            RequestId = request.RequestId,
            Status = status,
            PortName = selected.PortName,
            SourcePhone = sourcePhone,
            Destination = destination,
            Result = result,
            ErrorCode = ok ? string.Empty : status,
            Error = ok
                ? string.Empty
                : status == "maybe_sent"
                    ? "Modem đã nhận payload nhưng chưa xác nhận chắc chắn; không tự gửi lại."
                    : result,
            HttpStatusCode = ok
                ? StatusCodes.Status200OK
                : status == "maybe_sent"
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status502BadGateway
        };
    }

    private static ToolGsmSmsResponse? Validate(ToolGsmSmsRequest request)
    {
        if (request.SchemaVersion != 1)
            return Invalid("schemaVersion phải bằng 1.");
        if (!string.Equals(
                request.Purpose?.Trim(),
                RequiredPurpose,
                StringComparison.Ordinal))
            return Invalid("purpose phải là zalo-manual-mo.");
        if (!RequestIdPattern.IsMatch(request.RequestId?.Trim() ?? string.Empty))
            return Invalid("requestId không hợp lệ.");
        if (string.IsNullOrEmpty(MyVnptService.NormalizePhone(request.SourcePhone)))
            return Invalid("sourcePhone không phải số điện thoại Việt Nam hợp lệ.");
        if (string.IsNullOrEmpty(NormalizeVietnamPhone(request.Destination)))
            return Invalid("destination không phải số điện thoại Việt Nam hợp lệ.");
        if (!ZaloMoPattern.IsMatch(request.Message ?? string.Empty))
            return Invalid("message không đúng định dạng SMS MO [Zalo] token.");
        if (!string.IsNullOrWhiteSpace(request.PortName)
            && !Regex.IsMatch(
                request.PortName.Trim(),
                "^COM[0-9]{1,4}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return Invalid("portName không hợp lệ.");
        return null;

        ToolGsmSmsResponse Invalid(string message) => ToolGsmSmsResponse.Failure(
            "invalid_request",
            message,
            StatusCodes.Status400BadRequest,
            request.RequestId);
    }

    private static string NormalizeVietnamPhone(string? value) =>
        MyVnptService.NormalizePhone(value);

    private static string[] ResolveUrls()
    {
        string configured = Environment.GetEnvironmentVariable("TOOLGSM_API_URLS")
            ?? DefaultUrl;
        string[] urls = configured.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0) return [DefaultUrl];
        foreach (string url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"TOOLGSM_API_URLS không hợp lệ: {url}");
        }
        return urls;
    }

    private static bool IsLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1"
            || uri.Host == "::1"
            || (IPAddress.TryParse(uri.Host, out IPAddress? address)
                && IPAddress.IsLoopback(address));
    }

    private static IResult? Authorize(HttpContext context, string apiToken)
    {
        if (context.Request.Headers.ContainsKey("Origin"))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!string.Equals(
                context.Request.Headers["X-ToolGSM-Client"].ToString(),
                RequiredClientHeader,
                StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrEmpty(apiToken)) return null;

        string authorization = context.Request.Headers.Authorization.ToString();
        string candidate = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(apiToken);
        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate);
        bool valid = expectedBytes.Length == candidateBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
        return valid ? null : Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    private void TrimCompletedRequests()
    {
        if (_requests.Count <= 4096) return;
        foreach (var item in _requests
            .Where(pair => pair.Value.IsValueCreated && pair.Value.Value.IsCompleted)
            .Take(1024))
        {
            _requests.TryRemove(item.Key, out _);
        }
    }
}

public sealed record ToolGsmSmsRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string RequestId { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string SourcePhone { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
}

public sealed record ToolGsmSmsResponse
{
    public bool Ok { get; init; }
    public bool Duplicate { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public string SourcePhone { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public int HttpStatusCode { get; init; } = StatusCodes.Status200OK;

    public static ToolGsmSmsResponse Failure(
        string code,
        string message,
        int statusCode,
        string? requestId = null) => new()
        {
            Ok = false,
            RequestId = requestId?.Trim() ?? string.Empty,
            Status = "failed",
            ErrorCode = code,
            Error = message,
            HttpStatusCode = statusCode
        };
}

internal sealed record ToolGsmApiPort(
    string PortName,
    int PhysicalIndex,
    string PhoneNumber,
    bool Ready);

internal interface IToolGsmApiHost
{
    IReadOnlyList<ToolGsmApiPort> GetPorts();
    Task<string> SendSmsAsync(
        string portName,
        string destination,
        string message,
        CancellationToken cancellationToken);
    void UpsertQueue(
        string requestId,
        string portName,
        string destination,
        string message,
        string status,
        string? result,
        string? error);
    void Log(string message, string level);
}

internal sealed class MainViewModelToolGsmApiHost : IToolGsmApiHost
{
    private readonly MainViewModel _viewModel;

    public MainViewModelToolGsmApiHost(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public IReadOnlyList<ToolGsmApiPort> GetPorts() => _viewModel
        .GetPortsSnapshot()
        .Select(port => new ToolGsmApiPort(
            port.PortName,
            port.PhysicalIndex,
            port.PhoneNumber,
            port.Status == SimStatus.Active
                && _viewModel.IsPortReadyForOperation(port.PortName)))
        .ToArray();

    public Task<string> SendSmsAsync(
        string portName,
        string destination,
        string message,
        CancellationToken cancellationToken) =>
        _viewModel.QueueSmsFromWebAsync(
            portName, destination, message, cancellationToken);

    public void UpsertQueue(
        string requestId,
        string portName,
        string destination,
        string message,
        string status,
        string? result,
        string? error) => _viewModel.UpsertCommandQueue(
            requestId,
            portName,
            "sms",
            destination,
            message,
            status,
            result,
            error,
            "ZaloTool API");

    public void Log(string message, string level) =>
        _viewModel.AddLog(message, level);
}
