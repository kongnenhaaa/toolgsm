using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record TammiOtpSession(
    string Phone,
    string Scope,
    string DeviceId,
    bool IsPasswordRecovery);

public sealed record TammiAccountResult(
    bool Success,
    string Message,
    bool PasswordRecovered = false);

public sealed class TammiApiException : Exception
{
    public TammiApiException(string message) : base(message) { }
    public TammiApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Viettel Tammi account flow reconstructed from the official Android client:
/// check user, request/verify OTP, then register or reset the password. Tokens
/// stay in memory only and are never written to logs or local storage.
/// </summary>
public static partial class TammiService
{
    private const string ApiRoot = "https://api.tammi.vn/";
    internal const int ApiConcurrencyLimit = 20;
    private static readonly SemaphoreSlim ApiGate =
        new(ApiConcurrencyLimit, ApiConcurrencyLimit);

    private static HttpClient Client => WorkflowProxyService.GetHttpClient(
        "tammi",
        TimeSpan.FromSeconds(30),
        SettingsService.Current.UseProxyForTammi);

    [GeneratedRegex(@"(?<!\d)(\d{6})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex OtpRegex();

    public static string NormalizePhone(string? phone)
    {
        string digits = new((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("84", StringComparison.Ordinal) && digits.Length == 11)
            digits = "0" + digits[2..];
        return digits.Length == 10 && digits[0] == '0' ? digits : string.Empty;
    }

    public static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static bool TryExtractOtp(string? content, out string otp)
    {
        Match match = OtpRegex().Match(content ?? string.Empty);
        otp = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    public static bool IsTammiOtpMessage(string? content, string? sender = null)
    {
        if (!TryExtractOtp(content, out _)) return false;
        string text = RemoveDiacritics(content ?? string.Empty).ToLowerInvariant();
        string from = RemoveDiacritics(sender ?? string.Empty).ToLowerInvariant();
        if (text.Contains("tammi", StringComparison.Ordinal)
            || text.Contains("tami", StringComparison.Ordinal)
            || from.Contains("tammi", StringComparison.Ordinal)
            || from.Contains("tami", StringComparison.Ordinal))
        {
            return true;
        }

        bool looksLikeOtp = text.Contains("otp", StringComparison.Ordinal)
            || text.Contains("ma xac thuc", StringComparison.Ordinal)
            || text.Contains("ma bao mat", StringComparison.Ordinal);
        bool looksLikeViettel = text.Contains("viettel", StringComparison.Ordinal)
            || from.Contains("viettel", StringComparison.Ordinal);
        return looksLikeOtp && looksLikeViettel;
    }

    public static async Task<TammiOtpSession> PrepareAsync(
        string phone,
        CancellationToken cancellationToken = default)
    {
        string normalizedPhone = NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            throw new TammiApiException("Số điện thoại Tammi không hợp lệ.");

        string deviceId = CreateDeviceId();
        JsonElement response = await SendAsync(
            HttpMethod.Get,
            $"api/v1/user/check-user-exist?username={Uri.EscapeDataString(normalizedPhone)}",
            deviceId,
            payload: null,
            authorization: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Không kiểm tra được tài khoản Tammi");
        if (!TryGetProperty(response, "data", out JsonElement data)
            || data.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new TammiApiException(
                "Máy chủ Tammi không trả trạng thái tồn tại tài khoản.");
        }

        bool accountExists = data.GetBoolean();
        return new TammiOtpSession(
            normalizedPhone,
            accountExists ? "PASSWORD_RESET" : "REGISTER",
            deviceId,
            IsPasswordRecovery: accountExists);
    }

    public static async Task<string> SendOtpAsync(
        TammiOtpSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        JsonElement response = await SendAsync(
            HttpMethod.Post,
            "api/v1/auth/request-otp",
            session.DeviceId,
            new { username = session.Phone, scope = session.Scope },
            authorization: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Gửi OTP Tammi thất bại");
        return GetMessage(response, "Đã gửi OTP Tammi");
    }

    public static async Task<TammiAccountResult> CompleteAsync(
        TammiOtpSession session,
        string otp,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        string normalizedOtp = (otp ?? string.Empty).Trim();
        if (!Regex.IsMatch(normalizedOtp, @"^\d{6}$", RegexOptions.CultureInvariant))
            return new TammiAccountResult(false, "OTP Tammi không hợp lệ.");
        if (!IsValidPassword(password, out string passwordError))
            return new TammiAccountResult(false, passwordError);

        JsonElement verified = await SendAsync(
            HttpMethod.Post,
            "api/v1/auth/verify-otp",
            session.DeviceId,
            new
            {
                username = session.Phone,
                otpCode = normalizedOtp,
                scope = session.Scope
            },
            authorization: null,
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(verified))
            return new TammiAccountResult(false, GetMessage(verified, "Xác thực OTP Tammi thất bại."));

        string tempToken = GetNestedString(verified, "data", "token");
        if (string.IsNullOrWhiteSpace(tempToken))
            return new TammiAccountResult(false, "Máy chủ không trả tempToken sau khi xác thực OTP.");

        string endpoint = session.IsPasswordRecovery
            ? "api/v1/auth/reset-password"
            : "api/v1/auth/register-user";
        object payload = session.IsPasswordRecovery
            ? new { username = session.Phone, newPassword = password }
            : new { username = session.Phone, password };
        JsonElement completed = await SendAsync(
            HttpMethod.Post,
            endpoint,
            session.DeviceId,
            payload,
            authorization: $"Bearer {tempToken}",
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(completed))
        {
            return new TammiAccountResult(
                false,
                GetMessage(completed, session.IsPasswordRecovery
                    ? "Đặt lại mật khẩu Tammi thất bại."
                    : "Đăng ký Tammi thất bại."),
                session.IsPasswordRecovery);
        }

        return new TammiAccountResult(
            true,
            session.IsPasswordRecovery
                ? "Đã đặt lại mật khẩu Tammi"
                : "Đã đăng ký tài khoản và đặt mật khẩu Tammi",
            session.IsPasswordRecovery);
    }

    public static bool IsValidPassword(string? password, out string error)
    {
        string value = password ?? string.Empty;
        if (value.Length < 8
            || !value.Any(char.IsUpper)
            || !value.Any(char.IsLower)
            || !value.Any(char.IsDigit)
            || !value.Any(character => !char.IsLetterOrDigit(character)))
        {
            error = "Mật khẩu Tammi cần ít nhất 8 ký tự, có chữ hoa, chữ thường, số và ký tự đặc biệt.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public static string GetFriendlyExceptionMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "Kết nối Tammi quá thời gian.",
        OperationCanceledException => "Đã hủy",
        TammiApiException api => api.Message,
        HttpRequestException => "Không kết nối được máy chủ Tammi.",
        _ => string.IsNullOrWhiteSpace(exception.Message)
            ? "Lỗi không xác định khi xử lý Tammi."
            : exception.Message
    };

    private static async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        string deviceId,
        object? payload,
        string? authorization,
        CancellationToken cancellationToken)
    {
        await ApiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(
                method,
                new Uri(new Uri(ApiRoot), path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");
            request.Headers.UserAgent.ParseAdd("Tammi/1.1.46 Android");
            request.Headers.TryAddWithoutValidation("X-Device-ID", deviceId);
            if (!string.IsNullOrWhiteSpace(authorization))
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (payload is not null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");
            }

            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TammiApiException(
                    $"Máy chủ Tammi phản hồi HTTP {(int)response.StatusCode}.");
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new TammiApiException("Phản hồi Tammi không phải JSON hợp lệ.", ex);
            }
        }
        finally
        {
            ApiGate.Release();
        }
    }

    private static void EnsureSuccess(JsonElement response, string fallback)
    {
        if (!IsSuccess(response))
            throw new TammiApiException(GetMessage(response, fallback));
    }

    internal static bool IsSuccess(JsonElement response) =>
        TryGetProperty(response, "success", out JsonElement success)
        && success.ValueKind == JsonValueKind.True;

    internal static string GetMessage(JsonElement response, string fallback) =>
        TryGetProperty(response, "message", out JsonElement message)
        && message.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(message.GetString())
            ? message.GetString()!
            : fallback;

    private static string GetNestedString(
        JsonElement response,
        string objectName,
        string propertyName)
    {
        if (TryGetProperty(response, objectName, out JsonElement parent)
            && parent.ValueKind == JsonValueKind.Object
            && TryGetProperty(parent, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string RemoveDiacritics(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
