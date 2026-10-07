using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace gsm.Services;

public sealed record DeviceUnlockAuthResult(
    bool Success,
    string Message,
    bool CbssConfigured,
    bool EmployConfigured,
    string CbssUsername,
    string EmployUsername,
    string Challenge,
    string DeviceId,
    bool CbssPinged = false,
    bool CbssSessionValid = false,
    int CbssHttpStatus = 0,
    string CbssSessionMessage = "",
    bool EmployPinged = false,
    bool EmploySessionValid = false,
    int EmployHttpStatus = 0,
    string EmploySessionMessage = "");

public sealed class DeviceUnlockAuthService
{
    internal const string ResultPrefix = "TOOLGSM_AUTH_RESULT_JSON:";
    internal static Encoding Utf8WithoutBom { get; } = new UTF8Encoding(false);
    private readonly DeviceUnlockService _unlockService = new();

    public Task<DeviceUnlockAuthResult> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("status", cancellationToken: cancellationToken);

    public Task<DeviceUnlockAuthResult> PingSourceSessionsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync("ping_sources", cancellationToken: cancellationToken);

    public Task<DeviceUnlockAuthResult> SendCbssPinAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "cbss_send_pin",
            username: username,
            cancellationToken: cancellationToken);

    public Task<DeviceUnlockAuthResult> LoginCbssAsync(
        string username,
        string password,
        string otp,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "cbss_login",
            username,
            password,
            otp,
            cancellationToken: cancellationToken);

    public Task<DeviceUnlockAuthResult> BeginEmployLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "employ_begin",
            username,
            password,
            cancellationToken: cancellationToken);

    public Task<DeviceUnlockAuthResult> VerifyEmployOtpAsync(
        string username,
        string password,
        string otp,
        string challenge,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "employ_verify",
            username,
            password,
            otp,
            challenge,
            deviceId,
            cancellationToken);

    public async Task<DeviceUnlockAuthResult> LoginEmployWithOtpAsync(
        string username,
        string password,
        string otp,
        string challenge = "",
        string deviceId = "",
        CancellationToken cancellationToken = default)
    {
        DeviceUnlockAuthResult? beginResult = null;
        if (string.IsNullOrWhiteSpace(challenge)
            || string.IsNullOrWhiteSpace(deviceId))
        {
            beginResult = await BeginEmployLoginAsync(
                username, password, cancellationToken);
            if (!beginResult.Success) return beginResult;

            challenge = beginResult.Challenge;
            deviceId = beginResult.DeviceId;
        }

        DeviceUnlockAuthResult verifyResult = await VerifyEmployOtpAsync(
            username,
            password,
            otp,
            challenge,
            deviceId,
            cancellationToken);
        if (verifyResult.Success) return verifyResult;

        // Keep the generated challenge in the UI. If the server issued a new
        // OTP while initializing the session, the user can enter it and click
        // Đăng nhập OTP again without pressing Gửi OTP.
        return verifyResult with
        {
            Challenge = challenge,
            DeviceId = deviceId,
            CbssConfigured = beginResult?.CbssConfigured
                ?? verifyResult.CbssConfigured,
            EmployConfigured = beginResult?.EmployConfigured
                ?? verifyResult.EmployConfigured,
            CbssUsername = beginResult?.CbssUsername
                ?? verifyResult.CbssUsername,
            EmployUsername = username
        };
    }

    private async Task<DeviceUnlockAuthResult> ExecuteAsync(
        string action,
        string username = "",
        string password = "",
        string otp = "",
        string challenge = "",
        string deviceId = "",
        CancellationToken cancellationToken = default)
    {
        string? sourcePath = _unlockService.ResolveSourceScriptPath();
        if (sourcePath == null)
            return Failure("Không tìm thấy ekyc_full.py cạnh ToolGSM.exe hoặc trong Pictures\\kyc");
        string? bridgePath = ResolveBridgeScriptPath();
        if (bridgePath == null)
            return Failure("Thiếu auth_source.py cạnh ToolGSM.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = VoiceTranscriptionService.FindPythonExecutable(),
            WorkingDirectory = Path.GetDirectoryName(sourcePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        ConfigureUtf8PythonEnvironment(startInfo);
        startInfo.ArgumentList.Add(bridgePath);
        startInfo.ArgumentList.Add(sourcePath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return Failure("Không khởi động được Python đăng nhập nguồn ảnh");
            string requestJson = JsonSerializer.Serialize(new
            {
                action,
                username,
                password,
                otp,
                challenge,
                deviceId
            });
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            DeviceUnlockAuthResult? parsed = null;
            var nonProtocolOutput = new List<string>();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(
                    cancellationToken);
                if (line == null) break;
                if (TryParseResultLine(line, out DeviceUnlockAuthResult result))
                    parsed = result;
                else if (!string.IsNullOrWhiteSpace(line))
                    nonProtocolOutput.Add(line);
            }
            await process.WaitForExitAsync(cancellationToken);
            string stderr = await stderrTask;
            return parsed ?? Failure(BuildProcessFailureMessage(
                process.ExitCode, stderr, nonProtocolOutput));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);
            return Failure($"Không đăng nhập được nguồn ảnh: {ex.Message}");
        }
    }

    private static string? ResolveBridgeScriptPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "auth_source.py"),
            Path.Combine(AppContext.BaseDirectory, "device_unlock", "auth_source.py"),
            Path.Combine(AppContext.BaseDirectory, "gsm", "device_unlock", "auth_source.py")
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    internal static void ConfigureUtf8PythonEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["TOOLGSM_TRANSIENT_MODE"] = "1";
        startInfo.Environment["TOOLGSM_TRANSIENT_PARENT"] = Path.Combine(
            Path.GetTempPath(),
            "ToolGSM",
            "DKTTTB");
        startInfo.Environment["TOOLGSM_ACCOUNT_STATE_DIR"] =
            AppPaths.UserDataDirectory;
    }

    internal static bool TryParseResultLine(
        string? line,
        out DeviceUnlockAuthResult result)
    {
        result = Failure("Kết quả đăng nhập nguồn ảnh không hợp lệ");
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
            result = new DeviceUnlockAuthResult(
                GetBoolean(root, "success"),
                GetString(root, "message"),
                GetBoolean(root, "cbssConfigured"),
                GetBoolean(root, "employConfigured"),
                GetString(root, "cbssUsername"),
                GetString(root, "employUsername"),
                GetString(root, "challenge"),
                GetString(root, "deviceId"),
                GetBoolean(root, "cbssPinged"),
                GetBoolean(root, "cbssSessionValid"),
                GetInt32(root, "cbssHttpStatus"),
                GetString(root, "cbssSessionMessage"),
                GetBoolean(root, "employPinged"),
                GetBoolean(root, "employSessionValid"),
                GetInt32(root, "employHttpStatus"),
                GetString(root, "employSessionMessage"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? value.ToString()
            : string.Empty;

    private static int GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.TryGetInt32(out int result)
            ? result
            : 0;

    internal static string BuildProcessFailureMessage(
        int exitCode,
        string? standardError,
        IEnumerable<string>? standardOutput = null)
    {
        string? detail = FindSafeDiagnostic(standardError)
            ?? (standardOutput ?? []).Reverse().Select(FindSafeDiagnostic)
                .FirstOrDefault(value => value != null);
        string prefix = $"Python đăng nhập nguồn ảnh kết thúc với mã {exitCode}";
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
    }

    private static string? FindSafeDiagnostic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] sensitiveMarkers =
        [
            "password", "token", "authorization", "bearer", "secret",
            "cookie", "session"
        ];
        string? diagnostic = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Reverse()
            .FirstOrDefault(line =>
                !line.Contains("Traceback", StringComparison.OrdinalIgnoreCase)
                && !sensitiveMarkers.Any(marker =>
                    line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        return diagnostic is null || diagnostic.Length <= 400
            ? diagnostic
            : diagnostic[..400];
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

    private static DeviceUnlockAuthResult Failure(string message) =>
        new(false, message, false, false, string.Empty, string.Empty,
            string.Empty, string.Empty);
}
