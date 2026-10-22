using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

public sealed record WorkflowProxyConfiguration(
    string Host,
    int Port,
    string Username,
    string Password)
{
    public string Endpoint => $"{Host}:{Port}";
}

public sealed record WorkflowProxyTestResult(
    bool Success,
    string Message,
    string OutboundIp = "");

/// <summary>
/// Stores and applies the single authenticated proxy used by the MyVNPT,
/// DKTTTB and Dien May Xanh workflows. Other application traffic is left
/// unchanged.
/// </summary>
public static class WorkflowProxyService
{
    // Plain-text response is smaller than the JSON variant and is sufficient
    // for checking that the configured proxy can reach the Internet.
    private const string IpCheckUrl = "https://api.ipify.org";
    private static readonly ConcurrentDictionary<string, HttpClient> Clients =
        new(StringComparer.Ordinal);

    public static WorkflowProxyConfiguration? Current
    {
        get
        {
            AppSettings settings = SettingsService.Current;
            return TryCreateConfiguration(
                settings.WorkflowProxyHost,
                settings.WorkflowProxyPort,
                settings.WorkflowProxyUsername,
                settings.WorkflowProxyPassword,
                out WorkflowProxyConfiguration? configuration,
                out _)
                ? configuration
                : null;
        }
    }

    public static bool IsConfigured => Current is not null;

    public static bool TryCreateConfiguration(
        string? host,
        int port,
        string? username,
        string? password,
        out WorkflowProxyConfiguration? configuration,
        out string error)
    {
        configuration = null;
        string normalizedHost = (host ?? string.Empty).Trim();
        if (normalizedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalizedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(normalizedHost, UriKind.Absolute, out Uri? uri))
            {
                error = "Địa chỉ proxy không hợp lệ.";
                return false;
            }
            normalizedHost = uri.Host;
            if (port <= 0 && !uri.IsDefaultPort) port = uri.Port;
        }

        if (string.IsNullOrWhiteSpace(normalizedHost)
            || Uri.CheckHostName(normalizedHost) == UriHostNameType.Unknown)
        {
            error = "Host/IP proxy không hợp lệ.";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "Cổng proxy phải từ 1 đến 65535.";
            return false;
        }

        configuration = new WorkflowProxyConfiguration(
            normalizedHost,
            port,
            (username ?? string.Empty).Trim(),
            password ?? string.Empty);
        error = string.Empty;
        return true;
    }

    public static bool TryParse(
        string? value,
        out WorkflowProxyConfiguration? configuration,
        out string error)
    {
        configuration = null;
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Vui lòng nhập proxy.";
            return false;
        }

        string[] parts = text.Split(':', 4);
        if (parts.Length < 2
            || !int.TryParse(parts[1], out int port))
        {
            error = "Proxy phải có dạng IP:Port hoặc IP:Port:User:Password.";
            return false;
        }

        return TryCreateConfiguration(
            parts[0],
            port,
            parts.Length >= 3 ? parts[2] : string.Empty,
            parts.Length >= 4 ? parts[3] : string.Empty,
            out configuration,
            out error);
    }

    public static bool Save(
        WorkflowProxyConfiguration configuration,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!TryCreateConfiguration(
                configuration.Host,
                configuration.Port,
                configuration.Username,
                configuration.Password,
                out WorkflowProxyConfiguration? normalized,
                out error)
            || normalized is null)
        {
            return false;
        }

        AppSettings settings = SettingsService.Current;
        settings.WorkflowProxyHost = normalized.Host;
        settings.WorkflowProxyPort = normalized.Port;
        settings.WorkflowProxyUsername = normalized.Username;
        settings.WorkflowProxyPassword = normalized.Password;
        if (!SettingsService.SaveSettings(settings))
        {
            error = "Không lưu được proxy vào cấu hình ToolGSM.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool Clear()
    {
        AppSettings settings = SettingsService.Current;
        settings.WorkflowProxyHost = string.Empty;
        settings.WorkflowProxyPort = 0;
        settings.WorkflowProxyUsername = string.Empty;
        settings.WorkflowProxyPassword = string.Empty;
        settings.UseProxyForMyVnpt = false;
        settings.UseProxyForDeviceUnlock = false;
        settings.UseProxyForDmx = false;
        return SettingsService.SaveSettings(settings);
    }

    public static HttpClient GetHttpClient(
        string consumer,
        TimeSpan timeout,
        bool useConfiguredProxy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        WorkflowProxyConfiguration? configuration = useConfiguredProxy
            ? Current
            : null;
        string fingerprint = CreateFingerprint(configuration);
        string key = $"{consumer}|{timeout.Ticks}|{fingerprint}";
        return Clients.GetOrAdd(
            key,
            _ => CreateHttpClient(configuration, timeout));
    }

    public static async Task<WorkflowProxyTestResult> TestAsync(
        WorkflowProxyConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!TryCreateConfiguration(
                configuration.Host,
                configuration.Port,
                configuration.Username,
                configuration.Password,
                out WorkflowProxyConfiguration? normalized,
                out string error)
            || normalized is null)
        {
            return new WorkflowProxyTestResult(false, error);
        }

        try
        {
            using HttpClient client = CreateHttpClient(
                normalized,
                TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await client.GetAsync(
                IpCheckUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            string content = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new WorkflowProxyTestResult(
                    false,
                    $"Proxy phản hồi HTTP {(int)response.StatusCode}.");
            }

            string outboundIp = ExtractIp(content);
            return new WorkflowProxyTestResult(
                true,
                string.IsNullOrWhiteSpace(outboundIp)
                    ? "Kết nối proxy thành công."
                    : $"Kết nối thành công · IP ra ngoài: {outboundIp}",
                outboundIp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WorkflowProxyTestResult(false, "Proxy quá thời gian phản hồi.");
        }
        catch (Exception ex)
        {
            return new WorkflowProxyTestResult(
                false,
                "Không kết nối được proxy: " + ex.Message);
        }
    }

    public static void ApplyToEnvironment(
        ProcessStartInfo startInfo,
        bool useConfiguredProxy)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        WorkflowProxyConfiguration? configuration = useConfiguredProxy
            ? Current
            : null;
        if (configuration is null)
        {
            RemoveProxyEnvironment(startInfo);
            return;
        }

        string proxyUri = BuildProxyUri(configuration, includeCredentials: true);
        startInfo.Environment["HTTP_PROXY"] = proxyUri;
        startInfo.Environment["HTTPS_PROXY"] = proxyUri;
        startInfo.Environment["ALL_PROXY"] = proxyUri;
        startInfo.Environment["http_proxy"] = proxyUri;
        startInfo.Environment["https_proxy"] = proxyUri;
        startInfo.Environment["all_proxy"] = proxyUri;
    }

    internal static string BuildProxyUri(
        WorkflowProxyConfiguration configuration,
        bool includeCredentials)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttp, configuration.Host)
        {
            Port = configuration.Port
        };
        if (includeCredentials
            && !string.IsNullOrWhiteSpace(configuration.Username))
        {
            builder.UserName = configuration.Username;
            builder.Password = configuration.Password;
        }
        return builder.Uri.AbsoluteUri;
    }

    private static HttpClient CreateHttpClient(
        WorkflowProxyConfiguration? configuration,
        TimeSpan timeout)
    {
        if (configuration is null)
        {
            return ConfigureClient(
                new HttpClient(CreateHandler(null), disposeHandler: true),
                timeout);
        }

        var webProxy = new WebProxy(
            BuildProxyUri(configuration, includeCredentials: false));
        if (!string.IsNullOrWhiteSpace(configuration.Username))
        {
            webProxy.Credentials = new NetworkCredential(
                configuration.Username,
                configuration.Password);
        }

        return ConfigureClient(
            new HttpClient(CreateHandler(webProxy), disposeHandler: true),
            timeout);
    }

    internal static SocketsHttpHandler CreateHandler(IWebProxy? proxy)
    {
        return new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy is not null,
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli,
            // OTP workflows can pause for several minutes. Keeping an idle
            // tunnel available avoids paying for another proxy CONNECT/TLS
            // handshake when the next API step starts.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10)
        };
    }

    private static HttpClient ConfigureClient(HttpClient client, TimeSpan timeout)
    {
        client.Timeout = timeout;
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    private static string CreateFingerprint(
        WorkflowProxyConfiguration? configuration)
    {
        if (configuration is null) return "direct";
        string value = string.Join(
            '\n',
            configuration.Host,
            configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.Username,
            configuration.Password);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ExtractIp(string content)
    {
        string trimmed = (content ?? string.Empty).Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.TryGetProperty("ip", out JsonElement ip))
                return ip.GetString()?.Trim() ?? string.Empty;
        }
        catch (JsonException)
        {
        }
        return IPAddress.TryParse(trimmed, out IPAddress? address)
            ? address.ToString()
            : string.Empty;
    }

    private static void RemoveProxyEnvironment(ProcessStartInfo startInfo)
    {
        foreach (string name in new[]
                 {
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                     "http_proxy", "https_proxy", "all_proxy"
                 })
        {
            startInfo.Environment.Remove(name);
        }
    }
}
