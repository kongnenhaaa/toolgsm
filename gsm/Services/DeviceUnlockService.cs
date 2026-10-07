using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record DeviceUnlockRunResult(
    bool Success,
    bool AlreadyCompleted,
    string FullName,
    string Msisdn,
    string Message);

public sealed record DeviceUnlockOtpRequestResult(
    bool Success,
    MyVnptLoginOtpSession? Session,
    string Message,
    string ErrorCode,
    int HttpStatus);

public sealed class DeviceUnlockService
{
    internal const string ResultPrefix = "TOOLGSM_RESULT_JSON:";
    internal const string OtpResultPrefix = "TOOLGSM_OTP_REQUEST_JSON:";
    private static readonly string[] SensitiveLogMarkers =
    [
        "authorization", "bearer", "token", "session", "secret",
        "signature", "requestdata", "private_key", "public_key",
        "aes_key", "x-secret", "x-signature"
    ];

    public string? ResolveSourceScriptPath(string? configuredPath = null)
    {
        string pictures = Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures);
        string? environmentPath = Environment.GetEnvironmentVariable(
            "TOOLGSM_DKTTTB_SCRIPT");
        string[] candidates =
        [
            configuredPath ?? string.Empty,
            environmentPath ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "ekyc_full.py"),
            Path.Combine(AppContext.BaseDirectory, "device_unlock", "ekyc_full.py"),
            Path.Combine(pictures, "kyc", "ekyc_full.py"),
            Path.Combine(pictures, "kyc", "ekyc", "_full.py")
        ];

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    public string? ResolveBridgeScriptPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "run_unlock.py"),
            Path.Combine(AppContext.BaseDirectory, "device_unlock", "run_unlock.py"),
            Path.Combine(AppContext.BaseDirectory, "gsm", "device_unlock", "run_unlock.py")
        ];
        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    public async Task<DeviceUnlockOtpRequestResult> RequestLoginOtpAsync(
        string phone,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedPhone = MyVnptService.NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return OtpFailure("Số điện thoại không hợp lệ");

        string? sourcePath = ResolveSourceScriptPath();
        if (sourcePath == null)
            return OtpFailure("Không tìm thấy ekyc_full.py cạnh ToolGSM.exe hoặc trong Pictures\\kyc");
        string? bridgePath = ResolveBridgeScriptPath();
        if (bridgePath == null)
            return OtpFailure("Thiếu run_unlock.py cạnh ToolGSM.exe");

        using var process = new Process
        {
            StartInfo = CreateBridgeStartInfo(sourcePath, bridgePath)
        };
        try
        {
            if (!process.Start())
                return OtpFailure("Không khởi động được Python gửi OTP eKYC");

            string requestJson = JsonSerializer.Serialize(new
            {
                action = "requestOtp",
                phone = normalizedPhone
            });
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            DeviceUnlockOtpRequestResult? parsedResult = null;
            string lastUsefulLine = string.Empty;
            Task stderrTask = DrainLinesAsync(
                process.StandardError,
                line =>
                {
                    string? safe = SanitizeProgressLine(line);
                    if (safe == null) return;
                    lastUsefulLine = safe;
                    progress?.Invoke(safe);
                },
                cancellationToken);

            while (true)
            {
                string? line = await process.StandardOutput
                    .ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (TryParseOtpRequestResultLine(
                        line,
                        normalizedPhone,
                        out DeviceUnlockOtpRequestResult result))
                {
                    parsedResult = result;
                    continue;
                }

                string? safe = SanitizeProgressLine(line);
                if (safe == null) continue;
                lastUsefulLine = safe;
                progress?.Invoke(safe);
            }

            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            return parsedResult
                ?? OtpFailure(string.IsNullOrWhiteSpace(lastUsefulLine)
                    ? $"Python gửi OTP kết thúc với mã {process.ExitCode}"
                    : lastUsefulLine);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);
            return OtpFailure($"Không gửi được OTP bằng eKYC: {ex.Message}");
        }
    }

    public async Task<DeviceUnlockRunResult> RunAsync(
        string phone,
        string otp,
        MyVnptLoginOtpSession loginSession,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedPhone = MyVnptService.NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return Failure("Số điện thoại không hợp lệ");
        string normalizedOtp = otp ?? string.Empty;
        if (!Regex.IsMatch(normalizedOtp, @"^\d{4,8}$"))
            return Failure("OTP đăng nhập không hợp lệ");
        if (!string.Equals(
                normalizedPhone,
                loginSession.Phone,
                StringComparison.Ordinal))
        {
            return Failure("Phiên OTP không thuộc số điện thoại đang xử lý");
        }

        string? sourcePath = ResolveSourceScriptPath();
        if (sourcePath == null)
            return Failure("Không tìm thấy ekyc_full.py cạnh ToolGSM.exe hoặc trong Pictures\\kyc");
        string? bridgePath = ResolveBridgeScriptPath();
        if (bridgePath == null)
            return Failure("Thiếu run_unlock.py cạnh ToolGSM.exe");

        using var process = new Process
        {
            StartInfo = CreateBridgeStartInfo(sourcePath, bridgePath)
        };
        try
        {
            if (!process.Start())
                return Failure("Không khởi động được Python DKTTTB");

            string requestJson = BuildBridgeRequestJson(
                normalizedPhone,
                normalizedOtp,
                loginSession);
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            DeviceUnlockRunResult? parsedResult = null;
            string lastUsefulLine = string.Empty;
            Task stderrTask = DrainLinesAsync(
                process.StandardError,
                line =>
                {
                    string? safe = SanitizeProgressLine(line);
                    if (safe == null) return;
                    lastUsefulLine = safe;
                    progress?.Invoke(safe);
                },
                cancellationToken);

            while (true)
            {
                string? line = await process.StandardOutput
                    .ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (TryParseResultLine(line, out DeviceUnlockRunResult result))
                {
                    parsedResult = result;
                    continue;
                }

                string? safe = SanitizeProgressLine(line);
                if (safe == null) continue;
                lastUsefulLine = safe;
                progress?.Invoke(safe);
            }

            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            return parsedResult
                ?? Failure(string.IsNullOrWhiteSpace(lastUsefulLine)
                    ? $"Python DKTTTB kết thúc với mã {process.ExitCode}"
                    : lastUsefulLine);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);
            return Failure($"Không chạy được DKTTTB: {ex.Message}");
        }
    }

    internal static string BuildBridgeRequestJson(
        string phone,
        string otp,
        MyVnptLoginOtpSession loginSession) => JsonSerializer.Serialize(new
        {
            phone,
            otp,
            deviceProfile = new
            {
                di = loginSession.DeviceInfo,
                model = loginSession.DeviceModel,
                android_ver = loginSession.AndroidVersion,
                fcm_token = loginSession.FcmToken,
                mac = loginSession.MacAddress
            }
        });

    internal static bool TryParseOtpRequestResultLine(
        string? line,
        string phone,
        out DeviceUnlockOtpRequestResult result)
    {
        result = OtpFailure("Kết quả gửi OTP eKYC không hợp lệ");
        if (string.IsNullOrWhiteSpace(line)
            || !line.StartsWith(OtpResultPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                line[OtpResultPrefix.Length..]);
            JsonElement root = document.RootElement;
            bool success = root.TryGetProperty("success", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
            string message = GetString(root, "message");
            string errorCode = GetString(root, "errorCode");
            int httpStatus = root.TryGetProperty(
                    "httpStatus",
                    out JsonElement statusElement)
                && statusElement.TryGetInt32(out int parsedStatus)
                ? parsedStatus
                : 0;
            MyVnptLoginOtpSession? session = null;
            if (success
                && root.TryGetProperty(
                    "deviceProfile",
                    out JsonElement profile)
                && profile.ValueKind == JsonValueKind.Object)
            {
                session = new MyVnptLoginOtpSession(
                    phone,
                    GetString(profile, "di"),
                    GetString(profile, "model"),
                    GetString(profile, "android_ver"),
                    GetString(profile, "fcm_token"),
                    GetString(profile, "mac"),
                    GetString(root, "userAgent"));
                if (string.IsNullOrWhiteSpace(session.DeviceInfo)
                    || string.IsNullOrWhiteSpace(session.DeviceModel)
                    || string.IsNullOrWhiteSpace(session.FcmToken))
                {
                    success = false;
                    session = null;
                    message = "eKYC gửi OTP nhưng không trả đủ thông tin thiết bị";
                }
            }

            if (!success && string.IsNullOrWhiteSpace(message))
                message = "Yêu cầu OTP eKYC thất bại";
            string detail = success
                ? message
                : $"otp_send(authen_msisdn): HTTP {httpStatus}, ec={errorCode}, msg={message}";
            result = new DeviceUnlockOtpRequestResult(
                success,
                session,
                detail,
                errorCode,
                httpStatus);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseResultLine(
        string? line,
        out DeviceUnlockRunResult result)
    {
        result = Failure("Kết quả DKTTTB không hợp lệ");
        if (string.IsNullOrWhiteSpace(line)
            || !line.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                line[ResultPrefix.Length..]);
            JsonElement root = document.RootElement;
            bool success = root.TryGetProperty("success", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
            bool alreadyCompleted = root.TryGetProperty(
                    "alreadyCompleted",
                    out JsonElement completed)
                && completed.ValueKind == JsonValueKind.True;
            result = new DeviceUnlockRunResult(
                success,
                alreadyCompleted,
                GetString(root, "fullName"),
                GetString(root, "msisdn"),
                GetString(root, "message"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? SanitizeProgressLine(string? line)
    {
        string value = (line ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (SensitiveLogMarkers.Any(marker => value.Contains(
                marker,
                StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return value.Length <= 400 ? value : value[..400];
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? value.ToString()
            : string.Empty;

    private static async Task DrainLinesAsync(
        StreamReader reader,
        Action<string> consume,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) return;
            consume(line);
        }
    }

    private static ProcessStartInfo CreateBridgeStartInfo(
        string sourcePath,
        string bridgePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = VoiceTranscriptionService.FindPythonExecutable(),
            WorkingDirectory = Path.GetDirectoryName(sourcePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = DeviceUnlockAuthService.Utf8WithoutBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        DeviceUnlockAuthService.ConfigureUtf8PythonEnvironment(startInfo);
        startInfo.ArgumentList.Add(bridgePath);
        startInfo.ArgumentList.Add(sourcePath);
        return startInfo;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static DeviceUnlockRunResult Failure(string message) =>
        new(false, false, string.Empty, string.Empty, message);

    private static DeviceUnlockOtpRequestResult OtpFailure(string message) =>
        new(false, null, message, string.Empty, 0);
}
