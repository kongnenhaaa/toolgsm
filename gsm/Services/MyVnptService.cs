using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace gsm.Services;

public sealed record MyVnptOtpSession(
    string Phone,
    bool AccountExists,
    string DeviceInfo,
    string UserAgent);

public sealed record MyVnptPasswordResult(bool Success, string Message);

public static class MyVnptService
{
    private const string ApiRoot = "https://api-myvnpt.vnpt.vn/mapi_v2/services/";
    private const string AuthorizationToken = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly object PasswordLogLock = new();

    public static async Task<MyVnptOtpSession> PreparePasswordRequestAsync(
        string phone,
        CancellationToken cancellationToken = default)
    {
        string normalizedPhone = NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalizedPhone))
            throw new InvalidOperationException("Số điện thoại không hợp lệ");

        string deviceInfo = GetRandomDeviceInfo();
        string userAgent = GetRandomUserAgent();

        string checkContent = await PostAsync(
            "authen_check_account",
            new { msisdn = normalizedPhone },
            deviceInfo,
            userAgent,
            cancellationToken);

        string? checkCode = GetResponseValue(checkContent, "error_code", "errorCode");
        bool accountExists = checkCode switch
        {
            "3" => true,
            "0" => false,
            _ => throw new InvalidOperationException(GetResponseMessage(checkContent, "Không xác định được trạng thái tài khoản MyVNPT"))
        };

        return new MyVnptOtpSession(normalizedPhone, accountExists, deviceInfo, userAgent);
    }

    public static async Task SendOtpAsync(
        MyVnptOtpSession session,
        CancellationToken cancellationToken = default)
    {
        string otpContent = await PostAsync(
            "otp_send",
            new
            {
                msisdn = session.Phone,
                otp_service = session.AccountExists ? "authen_miss_password" : "authen_register"
            },
            session.DeviceInfo,
            session.UserAgent,
            cancellationToken);

        if (GetResponseValue(otpContent, "error_code", "errorCode") != "0")
            throw new InvalidOperationException(GetResponseMessage(otpContent, "Lỗi gửi OTP MyVNPT"));

    }

    public static async Task<MyVnptPasswordResult> SetPasswordAsync(
        string portName,
        MyVnptOtpSession session,
        string otp,
        string password,
        Action<string, string>? addLogCallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(otp))
                return new MyVnptPasswordResult(false, "OTP không hợp lệ");
            if (string.IsNullOrWhiteSpace(password))
                return new MyVnptPasswordResult(false, "Mật khẩu không hợp lệ");

            string hashedPassword = CreateMd5(password).ToUpperInvariant();
            string targetService = session.AccountExists ? "authen_miss_password" : "authen_register";
            object payload = session.AccountExists
                ? new { msisdn = session.Phone, otp, password = hashedPassword }
                : new { msisdn = session.Phone, password = hashedPassword, pin = otp };

            string responseContent = await PostAsync(
                targetService,
                payload,
                session.DeviceInfo,
                session.UserAgent,
                cancellationToken);

            string mode = session.AccountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
            if (GetResponseValue(responseContent, "error_code", "errorCode") == "0")
            {
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thành công ({mode}).", "SUCCESS");
                AppendPasswordBackup(session.Phone, password);
                return new MyVnptPasswordResult(true,
                    session.AccountExists ? "Đặt lại pass thành công" : "Đăng ký thành công");
            }

            string error = GetResponseMessage(responseContent, "Lỗi đặt pass");
            addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thất bại ({mode}): {error}", "ERROR");
            return new MyVnptPasswordResult(false, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string error = GetFriendlyExceptionMessage(ex);
            addLogCallback?.Invoke($"[{portName}] Lỗi đặt mật khẩu MyVNPT: {ex.Message}", "ERROR");
            return new MyVnptPasswordResult(false, error);
        }
    }

    public static bool IsMyVnptOtpMessage(string? content) =>
        !string.IsNullOrWhiteSpace(content)
        && (content.Contains("MyVNPT", StringComparison.OrdinalIgnoreCase)
            || content.Contains("My VNPT", StringComparison.OrdinalIgnoreCase));

    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        string digits = new(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0", StringComparison.Ordinal) && digits.Length is 10 or 11)
            digits = "84" + digits[1..];
        return digits.StartsWith("84", StringComparison.Ordinal) && digits.Length is 11 or 12
            ? digits
            : string.Empty;
    }

    public static string GetFriendlyExceptionMessage(Exception ex)
    {
        string msg = ex.Message;
        if (msg.Contains("api-myvnpt.vnpt.vn", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("respond", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("host", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("socket", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("closed", StringComparison.OrdinalIgnoreCase))
            return "Lỗi kết nối VNPT";
        return msg.Length > 80 ? "Lỗi hệ thống" : $"Lỗi: {msg}";
    }

    private static async Task<string> PostAsync(
        string service,
        object payload,
        string deviceInfo,
        string userAgent,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot + service)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", AuthorizationToken);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Device-Info", deviceInfo);
        request.Headers.TryAddWithoutValidation("Language", "vi_VN");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"VNPT HTTP {(int)response.StatusCode}: {GetResponseMessage(content, response.ReasonPhrase ?? "Request failed")}");
        return content;
    }

    private static string? GetResponseValue(string json, params string[] names)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (string name in names)
            {
                if (!document.RootElement.TryGetProperty(name, out JsonElement value)) continue;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string GetResponseMessage(string json, string fallback) =>
        GetResponseValue(json, "message", "error_message", "errorMessage") ?? fallback;

    private static string GetRandomDeviceInfo()
    {
        string deviceId = Guid.NewGuid().ToString();
        string[] models = [
            $"SM-G{Random.Shared.Next(900, 999)}F",
            $"SM-A{Random.Shared.Next(10, 99)}5F",
            $"Pixel {Random.Shared.Next(4, 8)}",
            $"CPH{Random.Shared.Next(2000, 2500)}",
            $"Redmi Note {Random.Shared.Next(7, 12)}"
        ];
        string model = models[Random.Shared.Next(models.Length)];
        return $"{deviceId}|{deviceId}|unknown|Android||3.3.97.Prd|{model}|{Random.Shared.Next(9, 14)}|";
    }

    private static string GetRandomUserAgent() =>
        $"okhttp/4.{Random.Shared.Next(7, 12)}.{Random.Shared.Next(0, 5)}";

    private static string CreateMd5(string input) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static void AppendPasswordBackup(string phone, string password)
    {
        try
        {
            string logPath = AppPaths.ForRuntimeFile("myvnpt_passwords.txt");
            lock (PasswordLogLock)
                File.AppendAllText(logPath, $"{phone}|{password}{Environment.NewLine}");
        }
        catch { }
    }
}
