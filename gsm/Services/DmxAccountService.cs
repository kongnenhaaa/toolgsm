using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record DmxRegistrationSession(
    string Phone,
    string DeviceId,
    string DeviceName,
    string OAuthAuthorization,
    string AccessToken,
    string? PbxPhoneNumber,
    string? DeviceToken,
    string CustomerId,
    string CredentialStage,
    bool IsPasswordRecovery = false,
    string? PasswordToken = null);

public sealed record DmxProfile(
    string FullName,
    DateOnly BirthDate,
    int Gender);

public sealed record DmxAccountResult(
    bool Success,
    string Message,
    bool PasswordRecovered = false);

public sealed class DmxApiException : Exception
{
    public DmxApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public static class DmxAccountService
{
    public const string DefaultOAuthAuthorization =
        "Basic bG95YWx0eS1hcHA6OTdIanNiODdMbnZoajdzZw==";

    private const string ProfileRoot =
        "https://wsloyalty.tgdd.vn/mwg-app-cdp-profile-service";
    private const string OAuthUrl = ProfileRoot + "/oauth/token";
    private const string AccountExistsUrl =
        ProfileRoot + "/api/cdp/profile/customer/isExist";
    private const string ConsentPurposeUrl =
        ProfileRoot + "/api/cdp/profile/consent/getListPurposeByCompany";
    private const string GraphQlUrl = ProfileRoot + "/graphql";
    private const string OfficialAppVersion = "2.0.26";
    private const string AndroidSystemVersion = "14";

    private const string SendOtpMutation = """
        mutation sendSMSOTP($typeOTP: String) {
          sendSMSOTP(typeOTP: $typeOTP) {
            isError
            message
            expireTime
            pbxPhoneNumber
            remaining
            remainingVoice
            remainingSmartOTP
          }
        }
        """;

    private const string ForgotPasswordMutation = """
        mutation forgotPassword($phoneNumber: String!, $typeOTP: String!) {
          forgotPassword(phoneNumber: $phoneNumber, typeOTP: $typeOTP) {
            isError
            message
            pbxPhoneNumber
            expireTime
            passwordToken
            remaining
          }
        }
        """;

    private const string VerifyOtpMutation = """
        mutation verifyOTP($otpCode: String!, $pbxPhoneNumber: String) {
          verifyOTP(otp: $otpCode, pbxPhoneNumber: $pbxPhoneNumber) {
            deviceToken
            deviceStage
          }
        }
        """;

    private const string UpdateProfileMutation = """
        mutation updateCustomerInfoV2($customer: RequestUpdateCustomerInput!) {
          updateCustomerInfoV2(customer: $customer) {
            isError
            message
          }
        }
        """;

    private const string CreatePasswordMutation = """
        mutation createPassword($code: String!) {
          createPassword(password: $code)
        }
        """;

    private const string InsertConsentMutation = """
        mutation insertConsentByCustomer(
          $inputRequest: InsertConsentByCustomerRequest
        ) {
          insertConsentByCustomer(inputRequest: $inputRequest) {
            isError
            message
          }
        }
        """;

    private static readonly Regex QuaTangVipOtpRegex = new(
        @"Qua\s*Tang\s*VIP\s*-\s*Ma\s+OTP\s+cua\s+ban\s+la\s+(?<otp>\d{6})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HttpClient Client => WorkflowProxyService.GetHttpClient(
        "dien-may-xanh",
        TimeSpan.FromSeconds(25),
        SettingsService.Current.UseProxyForDmx);
    private static readonly SemaphoreSlim HttpGate = new(20, 20);
    // The official client sends one password-reset request at a time. A short
    // shared start delay keeps a multi-COM batch from hitting the security
    // endpoint with many identical requests in the same millisecond while the
    // requests themselves can still complete in parallel.
    internal static readonly TimeSpan MinimumForgotPasswordSendSpacing =
        TimeSpan.FromMilliseconds(500);
    private static readonly AsyncRequestPacer ForgotPasswordSendPacer =
        new(MinimumForgotPasswordSendSpacing);
    private static readonly AsyncRequestPacer PasswordResetPacer =
        new(TimeSpan.FromMilliseconds(500));

    private static readonly string[] Surnames =
    [
        "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan",
        "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý"
    ];

    private static readonly string[] MaleMiddleNames =
    [
        "Văn", "Đức", "Minh", "Quang", "Hữu", "Gia", "Anh", "Tuấn"
    ];

    private static readonly string[] MaleGivenNames =
    [
        "An", "Bảo", "Dũng", "Hải", "Hiếu", "Huy", "Khang", "Khánh",
        "Kiên", "Long", "Nam", "Phong", "Phúc", "Sơn", "Thành", "Trung"
    ];

    private static readonly string[] FemaleMiddleNames =
    [
        "Thị", "Ngọc", "Thanh", "Thu", "Kim", "Phương", "Bảo", "Hoài"
    ];

    private static readonly string[] FemaleGivenNames =
    [
        "Anh", "Chi", "Giang", "Hà", "Hân", "Hương", "Lan", "Linh",
        "Mai", "My", "Ngân", "Nhung", "Thảo", "Trang", "Trâm", "Vy"
    ];

    private static readonly string[] AndroidDeviceModels =
    [
        "SM-A546E", "SM-A556E", "SM-G991B", "SM-G996B", "SM-S911B",
        "CPH2387", "CPH2487", "CPH2529", "V2149", "V2248",
        "23053RN02A", "22101316G", "Pixel 7", "Pixel 8"
    ];

    public static string NormalizePhone(string? value)
    {
        string phone = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
        if (phone.StartsWith("84", StringComparison.Ordinal) && phone.Length == 11)
            phone = "0" + phone[2..];
        return Regex.IsMatch(phone, @"^0\d{9}$", RegexOptions.CultureInvariant)
            ? phone
            : string.Empty;
    }

    public static bool TryExtractOtp(string? message, out string otp)
    {
        otp = string.Empty;
        if (string.IsNullOrWhiteSpace(message)) return false;

        string normalized = RemoveDiacritics(message);
        Match match = QuaTangVipOtpRegex.Match(normalized);
        if (!match.Success) return false;

        otp = match.Groups["otp"].Value;
        return otp.Length == 6;
    }

    public static DmxProfile CreateRandomProfile(int minimumYear, int maximumYear)
    {
        int currentYear = DateTime.Today.Year;
        if (minimumYear < 1900 || maximumYear >= currentYear || minimumYear > maximumYear)
            throw new ArgumentOutOfRangeException(
                nameof(minimumYear),
                $"Khoảng năm sinh phải từ 1900 đến {currentYear - 1}.");

        bool male = Random.Shared.Next(2) == 1;
        string middleName = male
            ? MaleMiddleNames[Random.Shared.Next(MaleMiddleNames.Length)]
            : FemaleMiddleNames[Random.Shared.Next(FemaleMiddleNames.Length)];
        string givenName = male
            ? MaleGivenNames[Random.Shared.Next(MaleGivenNames.Length)]
            : FemaleGivenNames[Random.Shared.Next(FemaleGivenNames.Length)];
        string fullName = string.Join(
            ' ',
            Surnames[Random.Shared.Next(Surnames.Length)],
            middleName,
            givenName);

        int year = Random.Shared.Next(minimumYear, maximumYear + 1);
        int month = Random.Shared.Next(1, 13);
        int day = Random.Shared.Next(1, DateTime.DaysInMonth(year, month) + 1);
        return new DmxProfile(fullName, new DateOnly(year, month, day), male ? 1 : 0);
    }

    public static async Task<DmxRegistrationSession> PrepareRegistrationAsync(
        string phoneValue,
        string deviceKey,
        string oauthAuthorization,
        Action<string, string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string phone = RequirePhone(phoneValue);
        string authorization = oauthAuthorization?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(authorization))
        {
            throw new DmxApiException(
                "Thiếu OAuth Authorization. Nhập credential của ứng dụng trong tab Điện Máy Xanh.");
        }

        (string deviceId, string deviceName) =
            CreateStableDeviceIdentity(deviceKey);

        bool? accountExists = await CheckAccountExistsAsync(
            phone,
            cancellationToken).ConfigureAwait(false);
        if (accountExists is null)
            throw new DmxApiException("Không xác định được trạng thái tài khoản từ máy chủ.");

        // Do not deliberately submit an empty password for an existing
        // account. Every rejected password grant counts as a security attempt
        // for the public IP and can make a 64-port batch trip the server's
        // "reached max attempts" protection.
        if (accountExists is true)
        {
            log?.Invoke(
                "Tài khoản đã tồn tại; chuyển thẳng sang luồng quên mật khẩu, không thử mật khẩu rỗng.",
                "INFO");
            return CreatePasswordRecoverySession(
                phone,
                deviceId,
                deviceName,
                authorization);
        }

        log?.Invoke("Tài khoản chưa tồn tại; đang khởi tạo hồ sơ đăng ký...", "INFO");
        await PrevalidateCustomerAsync(
            phone,
            deviceId,
            deviceName,
            authorization,
            cancellationToken).ConfigureAwait(false);

        JsonElement oauthResponse;
        try
        {
            oauthResponse = await OpenOAuthSessionAsync(
                phone,
                deviceId,
                deviceName,
                authorization,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DmxApiException ex) when (
            ex.Message.Contains("invalidated", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke(
                "Số điện thoại đã có mật khẩu; chuyển sang luồng quên mật khẩu.",
                "INFO");
            return CreatePasswordRecoverySession(
                phone,
                deviceId,
                deviceName,
                authorization);
        }

        string accessToken = GetString(oauthResponse, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new DmxApiException("Không lấy được access token cho phiên đăng ký.");

        string credentialStage = GetString(oauthResponse, "credential_stage").ToUpperInvariant();
        if (credentialStage is not ("INITIAL" or "OTP_UPDATED"))
        {
            throw new DmxApiException(
                $"Tài khoản không ở bước tạo mới: {(string.IsNullOrWhiteSpace(credentialStage) ? "không xác định" : credentialStage)}");
        }

        log?.Invoke($"Đã mở phiên đăng ký ở bước {credentialStage}.", "INFO");
        string customerId = FindStringByPropertyName(
            oauthResponse,
            "customerid",
            "customer_id",
            "customerId");
        return new DmxRegistrationSession(
            phone,
            deviceId,
            deviceName,
            authorization,
            accessToken,
            null,
            null,
            customerId,
            credentialStage);
    }

    public static async Task<(DmxRegistrationSession Session, string Message)> SendOtpAsync(
        DmxRegistrationSession session,
        Action<string, string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (session.IsPasswordRecovery)
        {
            const int maximumRequestAttempts = 2;
            for (int requestAttempt = 1;
                 requestAttempt <= maximumRequestAttempts;
                 requestAttempt++)
            {
                TimeSpan pacingDelay = await ForgotPasswordSendPacer.WaitForTurnAsync(
                    cancellationToken).ConfigureAwait(false);
                if (pacingDelay >= TimeSpan.FromMilliseconds(10))
                {
                    log?.Invoke(
                        $"Rải tải yêu cầu quên mật khẩu; chờ {pacingDelay.TotalMilliseconds:0} ms.",
                        "INFO");
                }

                JsonElement recoveryData = await GraphQlAsync(
                    session,
                    "forgotPassword",
                    ForgotPasswordMutation,
                    new { phoneNumber = session.Phone, typeOTP = "SMS" },
                    cancellationToken).ConfigureAwait(false);
                if (!recoveryData.TryGetProperty(
                        "forgotPassword",
                        out JsonElement recoveryResult)
                    || recoveryResult.ValueKind != JsonValueKind.Object)
                {
                    throw new DmxApiException(
                        "Máy chủ không trả kết quả gửi OTP quên mật khẩu.");
                }

                string recoveryMessage = GetString(
                    recoveryResult,
                    "message",
                    "Máy chủ đã nhận yêu cầu gửi OTP quên mật khẩu.");
                if (GetBoolean(recoveryResult, "isError"))
                {
                    if (requestAttempt < maximumRequestAttempts
                        && IsTransientForgotPasswordFailure(recoveryMessage))
                    {
                        log?.Invoke(
                            "Dịch vụ quên mật khẩu báo lỗi tạm thời; sẽ thử lại một lần sau khi rải tải.",
                            "WARNING");
                        continue;
                    }

                    throw new DmxApiException(recoveryMessage);
                }

                string recoveryPbxPhoneNumber = GetString(
                    recoveryResult,
                    "pbxPhoneNumber");
                string passwordToken = GetString(recoveryResult, "passwordToken");
                if (string.IsNullOrWhiteSpace(passwordToken))
                {
                    throw new DmxApiException(
                        "Yêu cầu quên mật khẩu không trả password token.");
                }

                log?.Invoke(recoveryMessage, "SUCCESS");
                return (
                    session with
                    {
                        PbxPhoneNumber = recoveryPbxPhoneNumber,
                        PasswordToken = passwordToken
                    },
                    recoveryMessage);
            }

            throw new DmxApiException("Gửi OTP quên mật khẩu thất bại.");
        }

        JsonElement data = await GraphQlAsync(
            session,
            "sendSMSOTP",
            SendOtpMutation,
            new { typeOTP = "SMS" },
            cancellationToken).ConfigureAwait(false);
        if (!data.TryGetProperty("sendSMSOTP", out JsonElement result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new DmxApiException("Máy chủ không trả kết quả gửi OTP.");
        }

        if (GetBoolean(result, "isError"))
            throw new DmxApiException(GetString(result, "message", "Gửi OTP thất bại."));

        string message = GetString(
            result,
            "message",
            "Máy chủ đã nhận yêu cầu gửi OTP.");
        string pbxPhoneNumber = GetString(result, "pbxPhoneNumber");
        log?.Invoke(message, "SUCCESS");
        return (session with { PbxPhoneNumber = pbxPhoneNumber }, message);
    }

    public static async Task<DmxAccountResult> CompleteRegistrationAsync(
        DmxRegistrationSession session,
        string otpValue,
        string password,
        DmxProfile profile,
        Action<string, string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string otp = Regex.Replace(otpValue ?? string.Empty, @"\D", string.Empty);
        if (!Regex.IsMatch(otp, @"^\d{6}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("OTP phải gồm đúng 6 chữ số.", nameof(otpValue));
        ValidatePassword(password);

        if (session.IsPasswordRecovery)
        {
            return await CompletePasswordRecoveryAsync(
                session,
                otp,
                password,
                log,
                cancellationToken).ConfigureAwait(false);
        }

        log?.Invoke("Đang xác minh OTP Quà Tặng VIP...", "INFO");
        JsonElement verifyData = await GraphQlAsync(
            session,
            "verifyOTP",
            VerifyOtpMutation,
            new { otpCode = otp, pbxPhoneNumber = session.PbxPhoneNumber },
            cancellationToken).ConfigureAwait(false);
        if (!verifyData.TryGetProperty("verifyOTP", out JsonElement verifyResult)
            || verifyResult.ValueKind != JsonValueKind.Object)
        {
            throw new DmxApiException("Xác minh OTP không thành công.");
        }

        string deviceToken = GetString(verifyResult, "deviceToken");
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new DmxApiException("Xác minh OTP không trả device token.");
        session = session with { DeviceToken = deviceToken };

        log?.Invoke(
            $"OTP hợp lệ; đang lưu hồ sơ {profile.FullName}, {profile.BirthDate:dd/MM/yyyy}...",
            "INFO");
        long birthdayMilliseconds = ToUnixMilliseconds(profile.BirthDate);
        var customer = new
        {
            customerUpdate = new
            {
                FULLNAME = profile.FullName,
                FULLNAME_EN = profile.FullName,
                EMAIL = string.Empty,
                RESIDENCEPROVINCEID = -1,
                BIRTHDAY = birthdayMilliseconds,
                GENDER = profile.Gender
            }
        };
        JsonElement profileData = await GraphQlAsync(
            session,
            "updateCustomerInfoV2",
            UpdateProfileMutation,
            new { customer },
            cancellationToken).ConfigureAwait(false);
        if (!profileData.TryGetProperty(
                "updateCustomerInfoV2",
                out JsonElement profileResult)
            || profileResult.ValueKind != JsonValueKind.Object
            || GetBoolean(profileResult, "isError"))
        {
            throw new DmxApiException(
                profileResult.ValueKind == JsonValueKind.Object
                    ? GetString(profileResult, "message", "Cập nhật hồ sơ thất bại.")
                    : "Cập nhật hồ sơ thất bại.");
        }

        log?.Invoke("Đã lưu hồ sơ; đang tạo mật khẩu...", "INFO");
        JsonElement passwordData = await GraphQlAsync(
            session,
            "createPassword",
            CreatePasswordMutation,
            new { code = password },
            cancellationToken).ConfigureAwait(false);
        if (!passwordData.TryGetProperty("createPassword", out JsonElement passwordResult)
            || passwordResult.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || passwordResult.ValueKind == JsonValueKind.False)
        {
            throw new DmxApiException("Tạo mật khẩu không thành công.");
        }

        log?.Invoke(
            "Đã tạo mật khẩu; đang xác nhận toàn bộ mục xử lý dữ liệu cá nhân...",
            "INFO");
        IReadOnlyList<long> purposeIds = await GetAllConsentPurposeIdsAsync(
            session,
            cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await InsertAllConsentsAsync(
                    session,
                    purposeIds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DmxApiException ex) when (IsAuthenticationFailure(ex))
            {
                log?.Invoke(
                    "Phiên đăng ký đã đổi trạng thái; đang đăng nhập lại để xác nhận đồng ý...",
                    "INFO");
                session = await RefreshAuthenticatedSessionAsync(
                    session,
                    password,
                    cancellationToken).ConfigureAwait(false);
                await InsertAllConsentsAsync(
                    session,
                    purposeIds,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DmxApiException ex) when (IsConsentMutationUnavailable(ex))
        {
            log?.Invoke(
                "Bỏ qua bước xác nhận dữ liệu cá nhân vì thao tác này không còn trong schema hiện tại.",
                "WARNING");
            return new DmxAccountResult(true, "Đã tạo tài khoản và mật khẩu");
        }

        log?.Invoke(
            $"Đã xác nhận toàn bộ {purposeIds.Count} mục xử lý dữ liệu cá nhân.",
            "SUCCESS");
        return new DmxAccountResult(
            true,
            $"Đã tạo tài khoản, mật khẩu và đồng ý toàn bộ {purposeIds.Count} mục dữ liệu cá nhân");
    }

    private static async Task<DmxAccountResult> CompletePasswordRecoveryAsync(
        DmxRegistrationSession session,
        string otp,
        string password,
        Action<string, string>? log,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.PasswordToken))
        {
            throw new DmxApiException(
                "Phiên quên mật khẩu chưa có password token.");
        }

        log?.Invoke("Đang xác minh OTP quên mật khẩu...", "INFO");
        JsonElement oauthResponse = await OpenPasswordRecoverySessionAsync(
            session,
            otp,
            cancellationToken).ConfigureAwait(false);
        string accessToken = GetString(oauthResponse, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new DmxApiException(
                "Xác minh OTP quên mật khẩu không trả access token.");
        }

        string deviceToken = FindStringByPropertyName(
            oauthResponse,
            "DeviceToken",
            "deviceToken");
        if (string.IsNullOrWhiteSpace(deviceToken)
            || deviceToken.Equals("{noop}", StringComparison.OrdinalIgnoreCase))
        {
            throw new DmxApiException(
                "Xác minh OTP quên mật khẩu không trả device token.");
        }

        string customerId = FindStringByPropertyName(
            oauthResponse,
            "customerid",
            "customer_id",
            "customerId");
        string credentialStage = GetString(
            oauthResponse,
            "credential_stage",
            session.CredentialStage).ToUpperInvariant();
        session = session with
        {
            AccessToken = accessToken,
            DeviceToken = deviceToken,
            CustomerId = customerId,
            CredentialStage = credentialStage
        };

        log?.Invoke("OTP hợp lệ; đang đặt lại mật khẩu...", "INFO");
        TimeSpan resetDelay = await PasswordResetPacer.WaitForTurnAsync(
            cancellationToken).ConfigureAwait(false);
        if (resetDelay >= TimeSpan.FromMilliseconds(10))
        {
            log?.Invoke(
                $"Rải tải đặt lại mật khẩu; chờ {resetDelay.TotalMilliseconds:0} ms.",
                "INFO");
        }
        JsonElement passwordData = await GraphQlAsync(
            session,
            "createPassword",
            CreatePasswordMutation,
            new { code = password },
            cancellationToken).ConfigureAwait(false);
        string? failure = GetPasswordRecoveryFailure(passwordData);
        if (failure != null) throw new DmxApiException(failure);

        log?.Invoke("Đã đặt lại mật khẩu thành công.", "SUCCESS");
        return new DmxAccountResult(
            true,
            "Đã đặt lại mật khẩu",
            PasswordRecovered: true);
    }

    internal static string? GetPasswordRecoveryFailure(JsonElement passwordData)
    {
        if (passwordData.ValueKind != JsonValueKind.Object
            || !passwordData.TryGetProperty(
                "createPassword", out JsonElement result)
            || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "Máy chủ không trả kết quả đặt lại mật khẩu sau khi xác minh OTP.";
        }
        if (result.ValueKind == JsonValueKind.False)
        {
            return "Máy chủ từ chối đặt lại mật khẩu sau khi xác minh OTP (createPassword=false).";
        }
        return result.ValueKind == JsonValueKind.True
            ? null
            : $"Máy chủ trả kết quả đặt lại mật khẩu không hợp lệ ({result.ValueKind}).";
    }

    public static string GetFriendlyExceptionMessage(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => "Đã hủy",
            TimeoutException => "Không nhận được SMS OTP trong 130 giây",
            DmxApiException apiException => apiException.Message,
            ArgumentException argumentException => argumentException.Message,
            _ => $"Lỗi Điện Máy Xanh: {exception.Message}"
        };
    }

    internal static (string DeviceId, string DeviceName)
        CreateStableDeviceIdentity(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("Device key is required.", nameof(deviceKey));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            deviceKey.Trim().ToUpperInvariant()));
        string deviceId = new Guid(hash.AsSpan(0, 16)).ToString();
        string deviceName = AndroidDeviceModels[hash[16] % AndroidDeviceModels.Length];
        return (deviceId, deviceName);
    }

    private static DmxRegistrationSession CreatePasswordRecoverySession(
        string phone,
        string deviceId,
        string deviceName,
        string authorization) => new(
            phone,
            deviceId,
            deviceName,
            authorization,
            string.Empty,
            null,
            null,
            string.Empty,
            "PASSWORD_RECOVERY",
            IsPasswordRecovery: true);

    private static async Task<bool?> CheckAccountExistsAsync(
        string phone,
        CancellationToken cancellationToken)
    {
        JsonElement response = await PostJsonAsync(
            AccountExistsUrl,
            new { principal = phone },
            null,
            cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("object", out JsonElement value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("isExist", out JsonElement isExist)
                && isExist.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return isExist.GetBoolean();
            }
            if (value.TryGetProperty("exists", out JsonElement exists)
                && exists.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return exists.GetBoolean();
            }
        }
        return value.ValueKind == JsonValueKind.Null ? null : true;
    }

    private static async Task<IReadOnlyList<long>> GetAllConsentPurposeIdsAsync(
        DmxRegistrationSession session,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = CreateGraphQlHeaders(session);
        headers["customerid"] = session.CustomerId;
        JsonElement response = await PostJsonAsync(
            ConsentPurposeUrl,
            new { },
            headers,
            cancellationToken).ConfigureAwait(false);
        if (GetBoolean(response, "error"))
        {
            throw new DmxApiException(GetString(
                response,
                "errorReason",
                GetString(response, "toastMessage", "Không lấy được danh sách mục đích đồng ý.")));
        }

        var purposeIds = new HashSet<long>();
        CollectPurposeIds(response, purposeIds);
        if (purposeIds.Count == 0)
            throw new DmxApiException("Máy chủ không trả danh sách mục đích xử lý dữ liệu cá nhân.");
        return purposeIds.OrderBy(id => id).ToArray();
    }

    private static async Task InsertAllConsentsAsync(
        DmxRegistrationSession session,
        IReadOnlyList<long> purposeIds,
        CancellationToken cancellationToken)
    {
        object[] purposes = purposeIds
            .Select(purposeId => (object)new { purposeId })
            .ToArray();
        JsonElement data = await GraphQlAsync(
            session,
            "insertConsentByCustomer",
            InsertConsentMutation,
            new
            {
                inputRequest = new
                {
                    lstPurposeOfContent = purposes
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!data.TryGetProperty(
                "insertConsentByCustomer",
                out JsonElement result)
            || result.ValueKind != JsonValueKind.Object
            || GetBoolean(result, "isError"))
        {
            throw new DmxApiException(
                result.ValueKind == JsonValueKind.Object
                    ? GetString(result, "message", "Xác nhận đồng ý dữ liệu cá nhân thất bại.")
                    : "Xác nhận đồng ý dữ liệu cá nhân thất bại.");
        }
    }

    private static async Task<DmxRegistrationSession> RefreshAuthenticatedSessionAsync(
        DmxRegistrationSession session,
        string password,
        CancellationToken cancellationToken)
    {
        JsonElement oauthResponse = await OpenOAuthSessionAsync(
            session.Phone,
            session.DeviceId,
            session.DeviceName,
            session.OAuthAuthorization,
            cancellationToken,
            password).ConfigureAwait(false);
        string accessToken = GetString(oauthResponse, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new DmxApiException("Không đăng nhập lại được sau khi tạo mật khẩu.");
        string customerId = FindStringByPropertyName(
            oauthResponse,
            "customerid",
            "customer_id",
            "customerId");
        string credentialStage = GetString(
            oauthResponse,
            "credential_stage",
            session.CredentialStage).ToUpperInvariant();
        return session with
        {
            AccessToken = accessToken,
            CustomerId = string.IsNullOrWhiteSpace(customerId)
                ? session.CustomerId
                : customerId,
            CredentialStage = credentialStage
        };
    }

    private static async Task PrevalidateCustomerAsync(
        string phone,
        string deviceId,
        string deviceName,
        string authorization,
        CancellationToken cancellationToken)
    {
        JsonElement response = await PostJsonAsync(
            OAuthUrl + "/preValidate",
            new
            {
                username = phone,
                password = string.Empty,
                device_id = deviceId,
                os = "android",
                deviceName
            },
            new Dictionary<string, string>
            {
                ["Authorization"] = authorization
            },
            cancellationToken).ConfigureAwait(false);
        if (GetBoolean(response, "error")
            || !string.IsNullOrWhiteSpace(GetString(response, "error_description")))
        {
            string reason = GetString(
                response,
                "error_description",
                GetString(
                    response,
                    "errorReason",
                    GetString(response, "toastMessage", "Không thể khởi tạo tài khoản.")));
            throw new DmxApiException(reason);
        }
    }

    private static async Task<JsonElement> OpenOAuthSessionAsync(
        string phone,
        string deviceId,
        string deviceName,
        string authorization,
        CancellationToken cancellationToken,
        string password = "")
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = phone,
            ["password"] = password,
            ["patchVersion"] = string.Empty,
            ["version"] = "2.0",
            ["device_id"] = deviceId,
            ["deviceName"] = deviceName
        };
        using var content = new FormUrlEncodedContent(form);
        return await SendAsync(
            OAuthUrl,
            content,
            new Dictionary<string, string>
            {
                ["Authorization"] = authorization
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> OpenPasswordRecoverySessionAsync(
        DmxRegistrationSession session,
        string otp,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = session.Phone,
            ["password"] = string.Empty,
            ["changeDeviceToken"] = string.Empty,
            ["device_id"] = session.DeviceId,
            ["deviceName"] = session.DeviceName,
            ["passwordOTP"] = otp,
            ["pbxPhoneNumber"] = session.PbxPhoneNumber ?? string.Empty,
            ["passwordToken"] = session.PasswordToken ?? string.Empty,
            ["patchVersion"] = string.Empty,
            ["version"] = "2.0"
        };
        using var content = new FormUrlEncodedContent(form);
        return await SendAsync(
            OAuthUrl,
            content,
            new Dictionary<string, string>
            {
                ["Authorization"] = session.OAuthAuthorization
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> GraphQlAsync(
        DmxRegistrationSession session,
        string operationName,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        JsonElement response = await PostJsonAsync(
            GraphQlUrl,
            new { operationName, query, variables },
            CreateGraphQlHeaders(session),
            cancellationToken).ConfigureAwait(false);

        if (response.TryGetProperty("errors", out JsonElement errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object
                    ? GetString(item, "message", "Lỗi GraphQL")
                    : item.ToString())
                .Where(message => !string.IsNullOrWhiteSpace(message));
            throw new DmxApiException("GraphQL: " + string.Join("; ", messages));
        }

        if (!response.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw new DmxApiException("Máy chủ GraphQL không trả dữ liệu.");
        }
        return data.Clone();
    }

    internal static Dictionary<string, string> CreateGraphQlHeaders(
        DmxRegistrationSession session)
    {
        // Keep this in sync with the current official mobile client's
        // createClient/getHeader pipeline. In particular, anonymous password
        // recovery must not claim an authenticated username.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Os"] = "android",
            ["deviceid"] = session.DeviceId,
            ["device_id"] = session.DeviceId,
            ["platform"] = "2",
            ["SiteId"] = "99",
            ["x-version-app"] = OfficialAppVersion,
            ["codePushVersion"] = string.Empty,
            ["User-Agent"] = $"Android||{AndroidSystemVersion}||{session.DeviceId}"
        };
        if (!string.IsNullOrWhiteSpace(session.AccessToken))
        {
            headers["Authorization"] = $"Bearer {session.AccessToken}";
            headers["accessToken"] = session.AccessToken;
            headers["username"] = session.Phone;
        }
        if (!string.IsNullOrWhiteSpace(session.DeviceToken))
            headers["devicetoken"] = session.DeviceToken;
        if (!string.IsNullOrWhiteSpace(session.CustomerId))
            headers["customerid"] = session.CustomerId;
        return headers;
    }

    internal static bool IsTransientForgotPasswordFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        string normalized = RemoveDiacritics(message).ToLowerInvariant();
        return normalized.Contains(
                "loi chuc nang quen mat khau",
                StringComparison.Ordinal)
            || normalized.Contains(
                "forgot password function error",
                StringComparison.Ordinal);
    }

    private static bool IsAuthenticationFailure(DmxApiException exception)
    {
        return exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || exception.Message.Contains("unauthor", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("invalid token", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("token expired", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConsentMutationUnavailable(DmxApiException exception)
    {
        string message = exception.Message;
        bool mentionsOperation =
            message.Contains("insertConsentByCustomer", StringComparison.OrdinalIgnoreCase)
            || message.Contains(
                "InsertConsentByCustomerRequest",
                StringComparison.OrdinalIgnoreCase);
        bool schemaRejectedOperation =
            message.Contains("UnknownType", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unknown type", StringComparison.OrdinalIgnoreCase)
            || message.Contains("FieldUndefined", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is undefined", StringComparison.OrdinalIgnoreCase);
        return mentionsOperation && schemaRejectedOperation;
    }

    private static void CollectPurposeIds(
        JsonElement element,
        ISet<long> purposeIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals("purposeId", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt64(out long numericId))
                    {
                        purposeIds.Add(numericId);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String
                        && long.TryParse(
                            property.Value.GetString(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out long textId))
                    {
                        purposeIds.Add(textId);
                    }
                }
                CollectPurposeIds(property.Value, purposeIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                CollectPurposeIds(item, purposeIds);
        }
    }

    private static string FindStringByPropertyName(
        JsonElement element,
        params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (propertyNames.Any(name => property.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind is not (
                        JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.ToString();
                }
                string nested = FindStringByPropertyName(
                    property.Value,
                    propertyNames);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string nested = FindStringByPropertyName(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return string.Empty;
    }

    private static async Task<JsonElement> PostJsonAsync(
        string url,
        object body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        return await SendAsync(url, content, headers, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement> SendAsync(
        string url,
        HttpContent content,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        await HttpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.12.0");
            if (headers != null)
            {
                foreach ((string name, string value) in headers)
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            string raw = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DmxApiException(
                    $"HTTP {(int)response.StatusCode}: {ExtractServerError(raw)}",
                    response.StatusCode);
            }

            if (string.IsNullOrWhiteSpace(raw)) return EmptyJsonObject();
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new DmxApiException(
                    $"Máy chủ trả dữ liệu không hợp lệ: {ex.Message}");
            }
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DmxApiException(
                $"Máy chủ quá thời gian phản hồi: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            throw new DmxApiException($"Không kết nối được máy chủ: {ex.Message}");
        }
        finally
        {
            HttpGate.Release();
        }
    }

    private static string ExtractServerError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Máy chủ không trả nội dung.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            foreach (string property in new[] { "error_description", "message", "error" })
            {
                string value = GetString(root, property);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (JsonException)
        {
        }
        return raw.Length <= 300 ? raw : raw[..300];
    }

    private static string RequirePhone(string? value)
    {
        string phone = NormalizePhone(value);
        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("Số điện thoại phải có 10 chữ số và bắt đầu bằng 0.");
        return phone;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password)
            || password.Length < 8
            || !Regex.IsMatch(password, "[A-Za-z]")
            || !Regex.IsMatch(password, @"\d"))
        {
            throw new ArgumentException(
                "Mật khẩu cần ít nhất 8 ký tự, có cả chữ cái và số.");
        }
    }

    private static long ToUnixMilliseconds(DateOnly birthDate)
    {
        DateTime localDate = birthDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return new DateTimeOffset(localDate).ToUnixTimeMilliseconds();
    }

    private static string RemoveDiacritics(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character switch
                {
                    'Đ' => 'D',
                    'đ' => 'd',
                    _ => character
                });
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string GetString(
        JsonElement element,
        string propertyName,
        string defaultValue = "")
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : value.ToString();
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool parsed)
            && parsed;
    }

    private static JsonElement EmptyJsonObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

}
