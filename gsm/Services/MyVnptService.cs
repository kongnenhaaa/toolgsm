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

public sealed record MyVnptPasswordResult(bool Success, string Message);

public static class MyVnptService
{
    private const string ApiRoot = "https://api-myvnpt.vnpt.vn/mapi_v2/services/";
    private const string AuthorizationToken = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b";
    // Keep the same client fingerprint as the stable cuibap/pass_myvnpt flow.
    // Rotating it per COM made VNPT see a burst of unrelated clients.
    private const string StableDeviceInfo =
        "a6d10733-aaed-47a5-aa83-2446121b3e4e|a6d10733-aaed-47a5-aa83-2446121b3e4e|unknown|Android||3.3.97.Prd|motog(7)|10|";
    private const string StableUserAgent = "okhttp/4.7.2";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly object PasswordLogLock = new();
    // Mọi COM dùng chung một IP/API token nhưng mỗi workflow được phát độc lập,
    // đồng thời. Lỗi 429/503 vẫn được xử lý bằng retry riêng của request đó.
    private const int MaxTransientAttempts = 3;
    // VNPT có thể trả lỗi trạng thái nếu authen_check_account, otp_send và
    // request kế tiếp đến quá sát nhau. Các COM vẫn chạy độc lập, nhưng các
    // request dùng chung API phải được giãn cách ở một điểm trung tâm.
    private static readonly object ApiPacingLock = new();
    private static readonly TimeSpan MinimumApiRequestSpacing =
        TimeSpan.FromMilliseconds(1200);
    private static DateTimeOffset NextApiRequestUtc = DateTimeOffset.MinValue;

    public static async Task<MyVnptOtpSession> PreparePasswordRequestAsync(
        string phone,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
    {
        string normalizedPhone = NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalizedPhone))
            throw new InvalidOperationException("Số điện thoại không hợp lệ");

        string deviceInfo = StableDeviceInfo;
        string userAgent = StableUserAgent;

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
        // Giống pass_myvnpt: chỉ error_code=3 là tài khoản đã tồn tại.
        // Mọi mã khác đi theo nhánh đăng ký mới; không đoán ngược sang quên mật khẩu.
        bool accountExists = string.Equals(checkCode, "3", StringComparison.Ordinal);
        addLogCallback?.Invoke(
            $"[VNPT_HTTP] authen_check_account: code={checkCode}; message={checkMessage}; mode={(accountExists ? "authen_miss_password" : "authen_register")}", "INFO");

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

            // Re-check the account immediately before consuming the OTP. This
            // is the stable cuibap flow and avoids using a stale branch after
            // the SMS has been delayed. If the re-check fails, keep the branch
            // selected before otp_send instead of guessing from set-pass errors.
            bool accountExists = session.AccountExists;
            try
            {
                string checkContent = await PostAsync(
                    "authen_check_account",
                    new { msisdn = session.Phone },
                    session.DeviceInfo,
                    session.UserAgent,
                    cancellationToken,
                    addLogCallback);
                string? checkCode = GetResponseValue(checkContent, "error_code", "errorCode");
                string checkMessage = GetResponseMessage(checkContent, "");
                bool refreshedAccountExists = string.Equals(checkCode, "3", StringComparison.Ordinal);
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] Kiểm tra lại tài khoản trước khi đặt pass: code={checkCode}; message={checkMessage}; mode={(refreshedAccountExists ? "authen_miss_password" : "authen_register")}",
                    "INFO");
                accountExists = refreshedAccountExists;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] Không kiểm tra lại được tài khoản; dùng nhánh ban đầu: {ex.Message}",
                    "WARN");
            }

            string hashedPassword = CreateMd5(password).ToUpperInvariant();

            string targetService = accountExists ? "authen_miss_password" : "authen_register";
            object payload = accountExists
                ? new { msisdn = session.Phone, otp, password = hashedPassword }
                : new { msisdn = session.Phone, password = hashedPassword, pin = otp };

            string responseContent = await PostAsync(
                targetService,
                payload,
                session.DeviceInfo,
                session.UserAgent,
                cancellationToken,
                addLogCallback);

            string mode = accountExists ? "Quên mật khẩu" : "Tạo mới tài khoản";
            string respCode = GetResponseValue(responseContent, "error_code", "errorCode") ?? "null";
            string respMsg  = GetResponseMessage(responseContent, "Lỗi đặt pass");

            if (respCode == "0")
            {
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thành công ({mode}).", "SUCCESS");
                AppendPasswordBackup(session.Phone, password);
                return new MyVnptPasswordResult(true,
                    accountExists ? "Đặt lại pass thành công" : "Đăng ký thành công");
            }

            // Log chi tiết error_code để debug
            addLogCallback?.Invoke(
                $"[{portName}] [VNPT_DEBUG] {targetService} error_code={respCode} msg={respMsg}", "WARN");

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
            await WaitForApiRequestTurnAsync(cancellationToken);
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

        throw new HttpRequestException("VNPT không phản hồi sau các lần thử lại");
    }

    private static async Task WaitForApiRequestTurnAsync(
        CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (ApiPacingLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset start = NextApiRequestUtc > now
                ? NextApiRequestUtc
                : now;
            delay = start - now;
            NextApiRequestUtc = start + MinimumApiRequestSpacing;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
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
