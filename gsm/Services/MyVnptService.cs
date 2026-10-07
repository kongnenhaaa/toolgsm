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

public sealed record MyVnptLoginOtpSession(
    string Phone,
    string DeviceInfo,
    string DeviceModel,
    string AndroidVersion,
    string FcmToken,
    string MacAddress,
    string UserAgent);

public sealed record MyVnptPasswordResult(bool Success, string Message);

public static class MyVnptService
{
    private const string ApiRoot = "https://api-myvnpt.vnpt.vn/mapi_v2/services/";
    private const string AuthorizationToken = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b";
    private const string DeviceFingerprintNamespace =
        "ToolGSM.MyVNPT.Device.v1";
    private const string StableUserAgent = "okhttp/4.7.2";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    // Giới hạn một burst ở mức vừa phải nhưng vẫn cho nhiều COM chạy thật sự
    // song song. Bản cũ giãn *mọi* request 1,2 giây nên thời gian tăng tuyến
    // tính theo số COM, kể cả khi VNPT đang phản hồi bình thường.
    internal const int ApiConcurrencyLimit = 6;
    private static readonly SemaphoreSlim ApiConcurrencyGate =
        new(ApiConcurrencyLimit, ApiConcurrencyLimit);
    internal static readonly TimeSpan MinimumOtpSendSpacing =
        TimeSpan.FromSeconds(3);
    private static readonly AsyncRequestPacer OtpSendPacer =
        new(MinimumOtpSendSpacing);
    private const int MaxTransientAttempts = 3;
    // Chỉ giảm nhịp sau khi VNPT thực sự báo quá tải. Cooldown dùng chung giúp
    // các retry không cùng lúc dội ngược vào API, còn fast path không bị delay.
    private static readonly object ApiCooldownLock = new();
    private static DateTimeOffset ApiCooldownUntilUtc = DateTimeOffset.MinValue;

    public static async Task<MyVnptOtpSession> PreparePasswordRequestAsync(
        string phone,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
        => await PreparePasswordRequestAsync(
            phone,
            phone,
            cancellationToken,
            addLogCallback);

    public static async Task<MyVnptOtpSession> PreparePasswordRequestAsync(
        string phone,
        string deviceKey,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
    {
        string normalizedPhone = NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalizedPhone))
            throw new InvalidOperationException("Số điện thoại không hợp lệ");

        // A COM keeps the same virtual device across SIM swaps and app runs,
        // while different COM ports do not share one Android identifier. Never rotate this
        // value between check-account, otp_send and set-password: VNPT scopes
        // the pending OTP to the client fingerprint.
        string deviceInfo = CreateStableDeviceInfo(deviceKey);
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
        // VNPT có nhiều phiên bản API: tài khoản đã tồn tại có thể trả code 3,
        // reg_nok hoặc chỉ trả message "đã có tài khoản". Tất cả phải đi qua
        // authen_miss_password; nếu chọn nhầm authen_register, VNPT sẽ trả lại
        // "Thuê bao đã có tài khoản trên hệ thống".
        bool accountExists =
            string.Equals(checkCode, "3", StringComparison.Ordinal)
            || IsAccountAlreadyExistsResponse(checkCode, checkMessage);
        addLogCallback?.Invoke(
            $"[VNPT_HTTP] authen_check_account: code={checkCode}; message={checkMessage}; mode={(accountExists ? "authen_miss_password" : "authen_register")}", "INFO");

        return new MyVnptOtpSession(normalizedPhone, accountExists, deviceInfo, userAgent);
    }

    public static async Task<MyVnptOtpSession> SendOtpAsync(
        MyVnptOtpSession session,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
    {
        // Ten COM may remain active concurrently, but the observed VNPT flow
        // starts silently dropping SMS after roughly twenty OTP requests in a
        // short window. Keep otp_send at about twenty starts per minute;
        // account checks and OTP waiting still overlap normally.
        TimeSpan pacingDelay = await WaitForOtpSendTurnAsync(cancellationToken);
        LogOtpSendPacingDelay(pacingDelay, addLogCallback);

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

    internal static Task<TimeSpan> WaitForOtpSendTurnAsync(
        CancellationToken cancellationToken = default) =>
        OtpSendPacer.WaitForTurnAsync(cancellationToken);

    internal static void LogOtpSendPacingDelay(
        TimeSpan pacingDelay,
        Action<string, string>? addLogCallback)
    {
        if (pacingDelay >= TimeSpan.FromMilliseconds(100))
        {
            addLogCallback?.Invoke(
                $"[VNPT_HTTP] otp_send chờ giãn nhịp {pacingDelay.TotalSeconds:0.0} giây để tránh dồn yêu cầu.",
                "INFO");
        }
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
                bool refreshedAccountExists =
                    string.Equals(checkCode, "3", StringComparison.Ordinal)
                    || IsAccountAlreadyExistsResponse(checkCode, checkMessage);
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] Kiểm tra lại tài khoản trước khi đặt pass: code={checkCode}; message={checkMessage}; mode={(refreshedAccountExists ? "authen_miss_password" : "authen_register")}",
                    "INFO");
                if (refreshedAccountExists != accountExists)
                {
                    // OTP/PIN is scoped to the service selected by otp_send.
                    // The account check can change while the SMS is in flight;
                    // never switch authen_register <-> authen_miss_password
                    // after the OTP was already requested.
                    addLogCallback?.Invoke(
                        $"[{portName}] [VNPT_FLOW] Nhánh tài khoản thay đổi trong lúc chờ OTP; giữ nhánh đã gửi OTP: {(accountExists ? "authen_miss_password" : "authen_register")}",
                        "WARN");
                }
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

    public static bool IsMyVnptOtpMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        string text = TextEncodingNormalizer.RepairMojibake(content);
        if (text.Contains("MyVNPT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("My VNPT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // VNPT also sends registration OTPs under the eContract template. This
        // message is part of the MyVNPT account flow even though it does not
        // contain the literal word "MyVNPT". Keep the match OTP-bound so an
        // ordinary eContract notification cannot consume a pending password OTP.
        bool isEContract =
            text.Contains("VNPT eContract", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VNPT econtract", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ky hop dong dien tu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ký hợp đồng điện tử", StringComparison.OrdinalIgnoreCase);
        return isEContract
            && text.Contains("OTP", StringComparison.OrdinalIgnoreCase);
    }

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
        => await PostAsyncCore(
            Client,
            service,
            payload,
            deviceInfo,
            userAgent,
            cancellationToken,
            addLogCallback);

    internal static async Task<string> PostAsyncCore(
        HttpClient client,
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
            await ApiConcurrencyGate.WaitAsync(cancellationToken);
            try
            {
                // Re-check cooldown only after entering the bounded queue. A
                // request that queued before a 429 must not bypass the newly
                // published cooldown when a slot becomes available.
                await WaitForApiCooldownAsync(cancellationToken);
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

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                stopwatch.Stop();
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} attempt={attempt} status={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}",
                    response.IsSuccessStatusCode ? "INFO" : "WARN");
                if (response.IsSuccessStatusCode) return content;

                string responseMessage = GetResponseMessage(content, response.ReasonPhrase ?? "Request failed");
                if (!IsTransientStatusCode(response.StatusCode))
                {
                    throw new HttpRequestException(
                        $"VNPT HTTP {(int)response.StatusCode}: {responseMessage}",
                        null,
                        response.StatusCode);
                }

                TimeSpan retryDelay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(attempt * 2);
                if (retryDelay <= TimeSpan.Zero)
                    retryDelay = TimeSpan.FromSeconds(attempt * 2);
                if (retryDelay > TimeSpan.FromSeconds(15)) retryDelay = TimeSpan.FromSeconds(15);
                ExtendApiCooldown(retryDelay);
                if (attempt == MaxTransientAttempts)
                {
                    throw new HttpRequestException(
                        $"VNPT HTTP {(int)response.StatusCode}: {responseMessage}",
                        null,
                        response.StatusCode);
                }
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} tạm lỗi {(int)response.StatusCode}; thử lại sau {retryDelay.TotalSeconds:0.#} giây.",
                    "WARN");
            }
            finally
            {
                ApiConcurrencyGate.Release();
            }
        }

        throw new HttpRequestException("VNPT không phản hồi sau các lần thử lại");
    }

    private static async Task WaitForApiCooldownAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (ApiCooldownLock)
                delay = ApiCooldownUntilUtc - DateTimeOffset.UtcNow;

            if (delay <= TimeSpan.Zero) return;
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static void ExtendApiCooldown(TimeSpan delay)
    {
        DateTimeOffset candidate = DateTimeOffset.UtcNow + delay;
        lock (ApiCooldownLock)
        {
            if (candidate > ApiCooldownUntilUtc)
                ApiCooldownUntilUtc = candidate;
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

    private static string CreateMd5(string input) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    internal static string CreateStableDeviceInfo(string deviceKey)
    {
        string normalizedDeviceKey = deviceKey?.Trim().ToUpperInvariant()
            ?? string.Empty;
        if (string.IsNullOrEmpty(normalizedDeviceKey))
            throw new ArgumentException(
                "Khóa thiết bị không hợp lệ",
                nameof(deviceKey));

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{DeviceFingerprintNamespace}:{normalizedDeviceKey}"));
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        string deviceId =
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-" +
            $"{hex[16..20]}-{hex[20..32]}";

        return $"{deviceId}|{deviceId}|unknown|Android||3.3.97.Prd|motog(7)|10|";
    }

}
