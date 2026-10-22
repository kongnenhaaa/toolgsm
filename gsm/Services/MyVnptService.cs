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

public sealed record MyVnptPasswordResult(
    bool Success,
    string Message,
    bool NeedsRetryWithMissPassword = false,
    bool NeedsRetryWithFreshOtp = false);

public static class MyVnptService
{
    private const string ApiRoot = "https://api-myvnpt.vnpt.vn/mapi_v2/services/";
    private const string AuthorizationToken = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b";
    private const string DeviceFingerprintNamespace =
        "ToolGSM.MyVNPT.Device.v1";
    private const string StableUserAgent = "okhttp/4.7.2";
    // Preserve the original MyVNPT transport byte-for-byte when proxy is not
    // selected. This keeps changes made for other workflows from affecting
    // the established password flow or its latency.
    private static readonly HttpClient DirectClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private static HttpClient Client => SettingsService.Current.UseProxyForMyVnpt
        ? WorkflowProxyService.GetHttpClient(
            "myvnpt",
            TimeSpan.FromSeconds(60),
            useConfiguredProxy: true)
        : DirectClient;
    // Toàn bộ bank COM có thể cùng chạy workflow, nhưng chỉ cho tối đa 20
    // request HTTP đi đồng thời để giữ thông lượng cao mà không làm nghẽn API.
    internal const int ApiConcurrencyLimit = 20;
    private static readonly SemaphoreSlim ApiConcurrencyGate =
        new(ApiConcurrencyLimit, ApiConcurrencyLimit);
    // Pacer này chỉ còn dùng cho luồng đăng nhập eKYC/đổi thiết bị. Pass VNPT
    // có retry riêng theo từng COM và cần khởi chạy theo lô 20.
    internal static readonly TimeSpan MinimumOtpSendSpacing =
        TimeSpan.FromSeconds(3);
    private static readonly AsyncRequestPacer OtpSendPacer =
        new(MinimumOtpSendSpacing);
    // API chịu được 20 request đồng thời nhưng cổng giao SMS bị hụt khi hàng
    // chục otp_send bắt đầu cùng một mili-giây. Chỉ rải thời điểm bắt đầu rất
    // ngắn; các workflow và request sau khi bắt đầu vẫn chạy song song.
    internal static readonly TimeSpan MinimumPasswordOtpSendSpacing =
        TimeSpan.FromMilliseconds(250);
    private static readonly AsyncRequestPacer PasswordOtpSendPacer =
        new(MinimumPasswordOtpSendSpacing);
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
        bool serverAccountExists =
            string.Equals(checkCode, "3", StringComparison.Ordinal)
            || IsAccountAlreadyExistsResponse(checkCode, checkMessage);
        bool cachedAccountExists =
            MyVnptAccountStateCache.Default.IsKnownAccount(normalizedPhone);
        bool accountExists = serverAccountExists || cachedAccountExists;
        if (serverAccountExists)
            MyVnptAccountStateCache.Default.MarkAccountExists(normalizedPhone);
        if (cachedAccountExists && !serverAccountExists)
        {
            addLogCallback?.Invoke(
                "[VNPT_FLOW] authen_check_account chưa đồng bộ; dùng trạng thái tài khoản đã xác nhận ở lần thành công trước.",
                "INFO");
        }
        addLogCallback?.Invoke(
            $"[VNPT_HTTP] authen_check_account: code={checkCode}; message={checkMessage}; mode={(accountExists ? "authen_miss_password" : "authen_register")}", "INFO");

        return new MyVnptOtpSession(normalizedPhone, accountExists, deviceInfo, userAgent);
    }

    public static async Task<MyVnptOtpSession> SendOtpAsync(
        MyVnptOtpSession session,
        CancellationToken cancellationToken = default,
        Action<string, string>? addLogCallback = null)
    {
        TimeSpan pacingDelay = await PasswordOtpSendPacer.WaitForTurnAsync(
            cancellationToken).ConfigureAwait(false);
        if (pacingDelay >= TimeSpan.FromMilliseconds(10))
        {
            addLogCallback?.Invoke(
                $"[VNPT_HTTP] otp_send rải tải SMS gateway; chờ {pacingDelay.TotalMilliseconds:0} ms trước khi bắt đầu.",
                "INFO");
        }

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

        // authen_check_account đôi khi trả sai nhánh. Nếu chính otp_send phát
        // hiện trạng thái tài khoản xung đột, thử đúng một lần với nhánh còn
        // lại và mang session đã sửa về cho caller.
        if (otpCode != "0"
            && !IsOtpAlreadyPendingMessage(otpMessage)
            && IsAccountStateConflictMessage(otpMessage))
        {
            bool fallbackAccountExists = !session.AccountExists;
            string fallbackService = fallbackAccountExists
                ? "authen_miss_password"
                : "authen_register";
            addLogCallback?.Invoke(
                $"[VNPT_HTTP] otp_send {otpService} báo sai nhánh ({otpMessage.Trim()}); chuyển sang {fallbackService}.",
                "WARN");

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
            {
                session = session with { AccountExists = fallbackAccountExists };
                if (fallbackAccountExists)
                    MyVnptAccountStateCache.Default.MarkAccountExists(session.Phone);
                else
                    MyVnptAccountStateCache.Default.MarkAccountMissing(session.Phone);
            }
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

        addLogCallback?.Invoke(
            "[VNPT_HTTP] otp_send được server chấp nhận; OTP mới đã được xếp gửi.",
            "INFO");

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
                MyVnptAccountStateCache.Default.MarkAccountExists(session.Phone);
                addLogCallback?.Invoke($"[{portName}] Đặt mật khẩu MyVNPT {session.Phone} thành công ({mode}).", "SUCCESS");
                return new MyVnptPasswordResult(true,
                    accountExists ? "Đặt lại pass thành công" : "Đăng ký thành công");
            }

            // Log chi tiết error_code để debug
            addLogCallback?.Invoke(
                $"[{portName}] [VNPT_DEBUG] {targetService} error_code={respCode} msg={respMsg}", "WARN");

            // Check account có thể báo chưa có, nhưng authen_register mới là
            // nguồn xác nhận cuối cùng rằng thuê bao đã tồn tại. OTP register
            // không dùng được cho quên mật khẩu, nên báo caller phát OTP
            // authen_miss_password mới thay vì ghi lỗi ngay.
            if (ShouldRetryWithMissPassword(accountExists, respCode, respMsg))
            {
                MyVnptAccountStateCache.Default.MarkAccountExists(session.Phone);
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] Thuê bao đã có tài khoản; chuyển sang quên mật khẩu và gửi OTP mới.",
                    "WARN");
                return new MyVnptPasswordResult(
                    false,
                    respMsg,
                    NeedsRetryWithMissPassword: true);
            }

            // Một SMS cũ có thể tới đúng lúc ngay sau khi tool vừa gửi lại
            // otp_send. Đừng chốt COM là lỗi chỉ vì mã cũ bị VNPT từ chối:
            // caller sẽ mở một vòng OTP mới, còn mã đã dùng vẫn bị loại.
            if (IsRecoverableOtpFailure(respCode, respMsg))
            {
                addLogCallback?.Invoke(
                    $"[{portName}] [VNPT_FLOW] OTP/PIN bị từ chối; xin OTP mới trước khi kết luận thất bại.",
                    "WARN");
                return new MyVnptPasswordResult(
                    false,
                    respMsg,
                    NeedsRetryWithFreshOtp: true);
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
            || message.Contains("dang gui OTP", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OTP đã được gửi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OTP da duoc gui", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OTP has been sent", StringComparison.OrdinalIgnoreCase));

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

    internal static bool ShouldRetryWithMissPassword(
        bool accountExists,
        string? errorCode,
        string? message) =>
        !accountExists && IsAccountAlreadyExistsResponse(errorCode, message);

    internal static bool IsRecoverableOtpFailure(
        string? errorCode,
        string? message)
    {
        if (errorCode is not null
            && (errorCode.Equals("otp_nok", StringComparison.OrdinalIgnoreCase)
                || errorCode.Equals("pin_nok", StringComparison.OrdinalIgnoreCase)
                || errorCode.Equals("otp_invalid", StringComparison.OrdinalIgnoreCase)
                || errorCode.Equals("invalid_otp", StringComparison.OrdinalIgnoreCase)
                || errorCode.Equals("otp_expired", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message)) return false;

        bool mentionsOtp =
            message.Contains("OTP", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PIN", StringComparison.OrdinalIgnoreCase)
            || message.Contains("mã xác thực", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ma xac thuc", StringComparison.OrdinalIgnoreCase);
        bool isRejected =
            message.Contains("không hợp lệ", StringComparison.OrdinalIgnoreCase)
            || message.Contains("khong hop le", StringComparison.OrdinalIgnoreCase)
            || message.Contains("không đúng", StringComparison.OrdinalIgnoreCase)
            || message.Contains("khong dung", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sai", StringComparison.OrdinalIgnoreCase)
            || message.Contains("hết hạn", StringComparison.OrdinalIgnoreCase)
            || message.Contains("het han", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("chưa yêu cầu", StringComparison.OrdinalIgnoreCase)
            || message.Contains("chua yeu cau", StringComparison.OrdinalIgnoreCase);
        return mentionsOtp && isRejected;
    }

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
            var queueStopwatch = Stopwatch.StartNew();
            await ApiConcurrencyGate.WaitAsync(cancellationToken);
            queueStopwatch.Stop();
            try
            {
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} attempt={attempt} event=gate_acquired queueMs={queueStopwatch.ElapsedMilliseconds}",
                    "INFO");
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

                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} attempt={attempt} event=request_start",
                    "INFO");
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
            catch (OperationCanceledException ex)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaxTransientAttempts)
                {
                    throw new HttpRequestException(
                        "VNPT timeout sau các lần tự thử lại",
                        ex);
                }

                TimeSpan retryDelay = TimeSpan.FromSeconds(attempt * 2);
                ExtendApiCooldown(retryDelay);
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} timeout; tự thử lại lần {attempt + 1}/{MaxTransientAttempts} sau {retryDelay.TotalSeconds:0.#} giây.",
                    "WARN");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                if (attempt == MaxTransientAttempts) throw;

                TimeSpan retryDelay = TimeSpan.FromSeconds(attempt * 2);
                ExtendApiCooldown(retryDelay);
                addLogCallback?.Invoke(
                    $"[VNPT_HTTP] service={service} lỗi kết nối ({ex.Message}); tự thử lại lần {attempt + 1}/{MaxTransientAttempts} sau {retryDelay.TotalSeconds:0.#} giây.",
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
