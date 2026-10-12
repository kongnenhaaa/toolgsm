using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.IO;

namespace gsm.Services;

/// <summary>
/// A small, sanitized, per-batch diagnostic trace for the MyVNPT password
/// workflow. Normal application logs remain in RAM; this trace exists only so
/// a live batch can be tailed without controlling the application window.
/// </summary>
internal static class MyVnptDiagnosticLogger
{
    private const int MaximumRetainedFiles = 10;
    private static readonly object SessionLock = new();
    private static readonly Channel<DiagnosticEntry> Entries =
        Channel.CreateUnbounded<DiagnosticEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    private static readonly Regex SensitiveNumberRegex = new(
        @"(?<!\d)(?:\+?84|0)?\d{6,12}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static string? _currentLogPath;
    private static int _writerStarted;

    public static string CurrentLogPath
    {
        get
        {
            lock (SessionLock)
                return _currentLogPath ?? string.Empty;
        }
    }

    public static string StartBatch(int portCount)
    {
        try
        {
            string directory = Path.Combine(
                AppPaths.UserDataDirectory,
                "Diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"myvnpt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            string header =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INFO [VNPT_DIAG] "
                + $"batch_start ports={portCount} pid={Environment.ProcessId}"
                + Environment.NewLine;
            File.WriteAllText(path, header, new UTF8Encoding(false));

            lock (SessionLock)
                _currentLogPath = path;

            EnsureWriterStarted();
            DeleteOldLogs(directory, path);
            return path;
        }
        catch
        {
            lock (SessionLock)
                _currentLogPath = null;
            return string.Empty;
        }
    }

    public static void Capture(
        DateTime timestamp,
        string level,
        string message)
    {
        try
        {
            if (!ShouldCapture(message)) return;

            string path;
            lock (SessionLock)
                path = _currentLogPath ?? string.Empty;
            if (path.Length == 0) return;

            string safeLevel = string.IsNullOrWhiteSpace(level)
                ? "INFO"
                : level.Trim().ReplaceLineEndings(" ");
            string safeMessage = Sanitize(message);
            EnsureWriterStarted();
            Entries.Writer.TryWrite(new DiagnosticEntry(
                path,
                $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} {safeLevel} {safeMessage}{Environment.NewLine}"));
        }
        catch
        {
            // Diagnostics must never slow or fail a modem workflow.
        }
    }

    internal static bool ShouldCapture(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("VNPT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MyVNPT", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSmsTransportEvent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        return message.Contains("[SMS_", StringComparison.OrdinalIgnoreCase)
            || message.Contains("+CMTI", StringComparison.OrdinalIgnoreCase)
            || message.Contains("+CMT:", StringComparison.OrdinalIgnoreCase)
            || message.Contains("+CMGL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("+CMGR", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CMS ERROR", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Sanitize(string? message)
    {
        string singleLine = (message ?? string.Empty)
            .ReplaceLineEndings(" ")
            .Trim();
        return SensitiveNumberRegex.Replace(singleLine, static match =>
        {
            string digits = new(match.Value.Where(char.IsDigit).ToArray());
            string suffix = digits.Length >= 4 ? digits[^4..] : "****";
            return $"[REDACTED:*{suffix}]";
        });
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) != 0) return;
        _ = Task.Run(WriteLoopAsync);
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (DiagnosticEntry first in Entries.Reader.ReadAllAsync())
        {
            var batches = new Dictionary<string, StringBuilder>(
                StringComparer.OrdinalIgnoreCase);
            Add(first);
            while (Entries.Reader.TryRead(out DiagnosticEntry? next))
            {
                if (next != null) Add(next);
            }

            foreach ((string path, StringBuilder buffer) in batches)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        path,
                        buffer.ToString(),
                        new UTF8Encoding(false));
                }
                catch
                {
                    // Keep the writer alive; a later line may succeed after a
                    // transient antivirus/file-system lock is released.
                }
            }

            void Add(DiagnosticEntry entry)
            {
                if (!batches.TryGetValue(entry.Path, out StringBuilder? buffer))
                {
                    buffer = new StringBuilder();
                    batches.Add(entry.Path, buffer);
                }
                buffer.Append(entry.Line);
            }
        }
    }

    private static void DeleteOldLogs(string directory, string currentPath)
    {
        try
        {
            DateTime cutoffUtc = DateTime.UtcNow - AppBootstrap.LogRetention;
            FileInfo[] files = new DirectoryInfo(directory)
                .EnumerateFiles("myvnpt-*.log")
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .ToArray();

            foreach (FileInfo file in files.Where(candidate =>
                         candidate.LastWriteTimeUtc <= cutoffUtc))
            {
                if (!string.Equals(
                        file.FullName,
                        currentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    file.Delete();
                }
            }

            foreach (FileInfo file in new DirectoryInfo(directory)
                         .EnumerateFiles("myvnpt-*.log")
                         .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                         .Skip(MaximumRetainedFiles))
            {
                if (!string.Equals(
                        file.FullName,
                        currentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    file.Delete();
                }
            }
        }
        catch
        {
        }
    }

    private sealed record DiagnosticEntry(string Path, string Line);
}
