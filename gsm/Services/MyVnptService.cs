using System.IO;
using System.Diagnostics;
using System.Net;
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

public sealed record MyVnptPasswordResult(bool Success, string Message, bool NeedsRetryWithMissPassword = false);

public static class MyVnptService
{
    private const string ApiRoot = "https://api-myvnpt.vnpt.vn/mapi_v2/services/";
    private const string AuthorizationToken = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly object PasswordLogLock = new();
    // Mọi COM dùng chung một IP/API token: phát request VNPT tuần tự để tránh
    // burst 5-10 request làm phía VNPT trả 429/503 hoặc giới hạn IP.
    private static readonly SemaphoreSlim RequestConcurrencyGate = new(1, 1);
    private static readonly SemaphoreSlim RequestStartGate = new(1, 1);
    private static readonly TimeSpan MinimumRequestSpacing = TimeSpan.FromMilliseconds(1200);
    private static DateTime _nextRequestStartUtc = DateTime.MinValue;
    private const int MaxTransientAttempts = 3;

    public static async Task<MyVnptOtpSession> PreparePasswordRequestAsync(
        string phone,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
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
            cancellationToken,
            addLogCallback);

        string? checkCode = GetResponseValue(checkContent, "error_code", "errorCode");
        string checkMessage = GetResponseMessage(checkContent, "");
        // error_code=3: đã có tài khoản → quên mật khẩu
        // error_code=0: chưa có tài khoản → đăng ký
        // error_code=1: "Chưa có tài khoản VNPortal" → đăng ký (một số version API VNPT trả 1 thay vì 0)
        // Các code khác: log ra rồi thử đăng ký (an toàn hơn throw)
        bool accountExists = checkCode switch
        {
            "3" => true,
            "0" or "1" => false,
            _ => string.IsNullOrWhiteSpace(checkMessage)
                 || checkMessage.Contains("tài khoản", StringComparison.OrdinalIgnoreCase)
                    ? false  // có vẻ là chưa có TK
                    : throw new InvalidOperationException(GetResponseMessage(checkContent, "Không xác định được trạng thái tài khoản MyVNPT"))
        };
        addLogCallback?.Invoke(
            $"[VNPT_HTTP] authen_check_account: code={checkCode} → accountExists={accountExists}", "INFO");

        return new MyVnptOtpSession(normalizedPhone, accountExists, deviceInfo, userAgent);
    }

    public static async Task<MyVnptOtpSession> SendOtpAsync(
        MyVnptOtpSession session,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
    {
        string otpService = session.AccountExists ? "authen_miss_password" : "authen_register";
        string otpContent = await PostAsync(
            "otp_send",
            new { msisdn = session.Phone, otp_service = otpService },
            session.DeviceInfo,
            session.UserAgent,
            cancellationToken,
            addLogCallback);

        string? otpCode = GetResponseValue(otpContent, "error_code", "errorCode");
        string otpMessage = GetResponseMessage(otpContent, "Lỗi gửi OTP MyVNPT");

        // VNPT đôi khi trả trạng thái tài khoản không nhất quán ở authen_check_account.
        // Nếu loại OTP hiện tại không khớp, thử đúng một lần với loại còn lại.
        if (otpCode != "0" && IsAccountStateConflictMessage(otpMessage))
        {
            bool fallbackAccountExists = !session.AccountExists;
            string fallbackService = fallbackAccountExists ? "authen_miss_password" : "authen_register";
            addLogCallback?.Invoke(
                $"[VNPT_HTTP] otp_send {otpService} thất bại ({otpMessage.Trim()}); thử lại với {fallbackService}.",
                "INFO");

            otpContent = await PostAsync(
                "otp_send",
                new { msisdn = session.Phone, otp_service = fallbackService },
                session.DeviceInfo,
                session.UserAgent,
                cancellationToken,
                addLogCallback);
            otpCode = GetResponseValue(otpContent, "error_code", "errorCode");
            otpMessage = GetResponseMessage(otpContent, "Lỗi gửi OTP MyVNPT");
            if (otpCode == "0" || IsOtpAlreadyPendingMessage(otpMessage))
                session = session with { AccountExists = fallbackAccountExists };
        }

        if (otpCode != "0")
        {
            if (IsOtpAlreadyPendingMessage(otpMessage))
            {
                addLogCallback?.Invoke("[VNPT_HTTP] otp_send báo OTP đang được xử lý; tiếp tục chờ SMS.", "INFO");
                return session;
            }
            throw new InvalidOperationException(otpMessage);
        }

        return session;
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
                cancellationToken,
                addLogCallback);

            string mode = session.AccountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
            string respCode = GetResponseValue(responseContent, "error_code", "errorCode") ?? "null";
            string respMsg  = GetResponseMessage(responseContent, "Lỗi đặt pass");

            if (respCode == "0")
            {
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thành công ({mode}).", "SUCCESS");
                AppendPasswordBackup(session.Phone, password);
                return new MyVnptPasswordResult(true,
                    session.AccountExists ? "Đặt lại pass thành công" : "Đăng ký thành công");
            }

            // Log chi tiết error_code để debug
            addLogCallback?.Invoke(
                $"[{portName}] [VNPT_DEBUG] {targetService} error_code={respCode} msg={respMsg}", "WARN");

            // Bước check có thể báo chưa có TK nhưng register mới phát hiện thuê bao đã có TK.
            // Caller phải phát một OTP authen_miss_password mới vì OTP register vừa dùng không
            // được tái sử dụng cho luồng quên mật khẩu.
            if (!session.AccountExists && IsAccountAlreadyExistsResponse(respCode, respMsg))
            {
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] {respCode} ({respMsg}); chuyển sang quên mật khẩu và gửi OTP mới.",
                    "INFO");
                return new MyVnptPasswordResult(false, respMsg, NeedsRetryWithMissPassword: true);
            }

            addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thất bại ({mode}): {respMsg}", "ERROR");
            return new MyVnptPasswordResult(false, respMsg);
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

    public static bool IsOtpAlreadyPendingMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("đang gửi OTP", StringComparison.OrdinalIgnoreCase)
            || message.Contains("dang gui OTP", StringComparison.OrdinalIgnoreCase));

    internal static bool IsAccountAlreadyExistsResponse(string? errorCode, string? message) =>
        string.Equals(errorCode, "reg_nok", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("đã có tài khoản", StringComparison.OrdinalIgnoreCase)
                || message.Contains("da co tai khoan", StringComparison.OrdinalIgnoreCase)
                || message.Contains("đã tồn tại", StringComparison.OrdinalIgnoreCase)
                || message.Contains("da ton tai", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || message.Contains("đăng ký không thành công", StringComparison.OrdinalIgnoreCase)
                || message.Contains("dang ky khong thanh cong", StringComparison.OrdinalIgnoreCase)));

    private static bool IsAccountStateConflictMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("tài khoản", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tai khoan", StringComparison.OrdinalIgnoreCase)
            || message.Contains("account", StringComparison.OrdinalIgnoreCase));

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
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable })
            return "VNPT tạm quá tải; tool đã tự thử lại nhưng dịch vụ vẫn chưa sẵn sàng";
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            return "VNPT đang giới hạn yêu cầu; vui lòng chờ ít phút";

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
        CancellationToken cancellationToken,
        Action<string, string>? addLogCallback = null)
    {
        string json = JsonSerializer.Serialize(payload);
        for (int attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RequestConcurrencyGate.WaitAsync(cancellationToken);
            try
            {
                await WaitForRequestStartAsync(cancellationToken);
                var stopwatch = Stopwatch.StartNew();
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
                stopwatch.Stop();
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} attempt={attempt} status={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}",
                    response.IsSuccessStatusCode ? "INFO" : "WARN");
                if (response.IsSuccessStatusCode) return content;

                string responseMessage = GetResponseMessage(content, response.ReasonPhrase ?? "Request failed");
                if (!IsTransientStatusCode(response.StatusCode) || attempt == MaxTransientAttempts)
                {
                    throw new HttpRequestException(
                        $"VNPT HTTP {(int)response.StatusCode}: {responseMessage}",
                        null,
                        response.StatusCode);
                }

                TimeSpan retryDelay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(attempt * 2);
                if (retryDelay > TimeSpan.FromSeconds(15)) retryDelay = TimeSpan.FromSeconds(15);
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} tạm lỗi {(int)response.StatusCode}; thử lại sau {retryDelay.TotalSeconds:0.#} giây.",
                    "WARN");
                await Task.Delay(retryDelay, cancellationToken);
            }
            finally
            {
                RequestConcurrencyGate.Release();
            }
        }

        throw new HttpRequestException("VNPT không phản hồi sau các lần thử lại");
    }

    private static async Task WaitForRequestStartAsync(CancellationToken cancellationToken)
    {
        await RequestStartGate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan delay = _nextRequestStartUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            _nextRequestStartUtc = DateTime.UtcNow + MinimumRequestSpacing;
        }
        finally
        {
            RequestStartGate.Release();
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

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
