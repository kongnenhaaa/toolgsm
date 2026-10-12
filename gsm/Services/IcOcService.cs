using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace gsm.Services;

public sealed record IcOcPortRequest(
    [property: JsonPropertyName("port")] string Port,
    [property: JsonPropertyName("phone")] string Phone);

public sealed record IcOcResultItem(
    string Port,
    string Phone,
    string NormalizedPhone,
    bool Success,
    bool? IncomingEnabled,
    bool? OutgoingEnabled,
    bool EligibleForEmploy,
    bool Changed,
    string Message);

public sealed record IcOcBatchResult(
    bool Success,
    string Message,
    IReadOnlyList<IcOcResultItem> Items);

public sealed record IcOcProgress(
    int Completed,
    int Total,
    string Port,
    string Phone,
    string Stage,
    int WaitSeconds,
    IcOcResultItem? Item);

public sealed class IcOcService
{
    internal const string ResultPrefix = "TOOLGSM_ICOC_RESULT_JSON:";
    internal const string ProgressPrefix = "TOOLGSM_ICOC_PROGRESS_JSON:";
    private readonly DeviceUnlockService _unlockService = new();

    public Task<IcOcBatchResult> LookupCcbsAsync(
        IReadOnlyList<IcOcPortRequest> items,
        int maxParallelism,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new
            {
                action = "cbss_lookup",
                items,
                maxParallelism = Math.Clamp(maxParallelism, 1, 8)
            },
            cancellationToken: cancellationToken);

    public Task<IcOcBatchResult> UnlockEmployAsync(
        IReadOnlyList<IcOcPortRequest> items,
        string note,
        int delaySeconds,
        string icAction,
        string ocAction,
        Action<IcOcProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new
            {
                action = "employ_unlock",
                items,
                note = note.Trim(),
                delaySeconds = Math.Clamp(delaySeconds, 2, 3600),
                icAction = NormalizeAction(icAction),
                ocAction = NormalizeAction(ocAction)
            },
            progress,
            cancellationToken);

    private async Task<IcOcBatchResult> ExecuteAsync(
        object request,
        Action<IcOcProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? sourcePath = _unlockService.ResolveSourceScriptPath();
        if (sourcePath == null)
            return Failure("Không tìm thấy ekyc_full.py cạnh ToolGSM.exe hoặc trong Pictures\\kyc");
        string? bridgePath = ResolveBridgeScriptPath();
        if (bridgePath == null)
            return Failure("Thiếu icoc_bridge.py cạnh ToolGSM.exe");

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

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return Failure("Không khởi động được Python IC/OC");

            string requestJson = JsonSerializer.Serialize(request);
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            IcOcBatchResult? parsed = null;
            var nonProtocolOutput = new List<string>();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (TryParseResultLine(line, out IcOcBatchResult result))
                    parsed = result;
                else if (TryParseProgressLine(line, out IcOcProgress update))
                    progress?.Invoke(update);
                else if (!string.IsNullOrWhiteSpace(line))
                    nonProtocolOutput.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken);
            string stderr = await stderrTask;
            if (parsed is not null) return parsed;

            string message = DeviceUnlockAuthService.BuildProcessFailureMessage(
                process.ExitCode, stderr, nonProtocolOutput);
            return Failure(message.Replace(
                "Python đăng nhập nguồn ảnh",
                "Python IC/OC",
                StringComparison.Ordinal));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);
            return Failure($"Không chạy được IC/OC: {ex.Message}");
        }
    }

    internal static bool TryParseResultLine(
        string? line,
        out IcOcBatchResult result)
    {
        result = Failure("Kết quả IC/OC không hợp lệ");
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
            var items = new List<IcOcResultItem>();
            if (root.TryGetProperty("items", out JsonElement itemArray)
                && itemArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in itemArray.EnumerateArray())
                {
                    items.Add(ParseItem(item));
                }
            }

            result = new IcOcBatchResult(
                GetBoolean(root, "success"),
                GetString(root, "message"),
                items);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseProgressLine(
        string? line,
        out IcOcProgress progress)
    {
        progress = new IcOcProgress(
            0, 0, string.Empty, string.Empty, string.Empty, 0, null);
        if (string.IsNullOrWhiteSpace(line)
            || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                line[ProgressPrefix.Length..]);
            JsonElement root = document.RootElement;
            IcOcResultItem? item = null;
            if (root.TryGetProperty("item", out JsonElement itemElement)
                && itemElement.ValueKind == JsonValueKind.Object)
            {
                item = ParseItem(itemElement);
            }
            progress = new IcOcProgress(
                GetInt32(root, "completed"),
                GetInt32(root, "total"),
                GetString(root, "port"),
                GetString(root, "phone"),
                GetString(root, "stage"),
                GetInt32(root, "waitSeconds"),
                item);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IcOcResultItem ParseItem(JsonElement item) => new(
        GetString(item, "port"),
        GetString(item, "phone"),
        GetString(item, "normalizedPhone"),
        GetBoolean(item, "success"),
        GetNullableBoolean(item, "incomingEnabled"),
        GetNullableBoolean(item, "outgoingEnabled"),
        GetBoolean(item, "eligibleForEmploy"),
        GetBoolean(item, "changed"),
        GetString(item, "message"));

    internal static bool? NormalizeState(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized is "1" or "TRUE" or "MO" or "OPEN" or "ACTIVE" or "HOAT_DONG")
            return true;
        if (normalized is "0" or "FALSE" or "KHOA" or "LOCK" or "LOCKED" or "CLOSED" or "CAT" or "CHAN")
            return false;
        return null;
    }

    internal static string NormalizeAction(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "GIU" => "GIU",
            "KHOA" => "KHOA",
            _ => "MO"
        };

    private static bool GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static bool? GetNullableBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? value.ToString()
            : string.Empty;

    private static int GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static string? ResolveBridgeScriptPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "icoc_bridge.py"),
            Path.Combine(AppContext.BaseDirectory, "device_unlock", "icoc_bridge.py"),
            Path.Combine(AppContext.BaseDirectory, "gsm", "device_unlock", "icoc_bridge.py")
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
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

    private static IcOcBatchResult Failure(string message) =>
        new(false, message, []);
}
