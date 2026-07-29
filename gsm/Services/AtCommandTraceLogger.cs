using System.Diagnostics;
using System.IO;
using System.Text;

namespace gsm.Services;

/// <summary>
/// Nhật ký UART độc lập với log giao diện. Định dạng cố ý giống file capture
/// SAuto để có thể lọc trực tiếp theo TX/RX/OPEN/CLOSE và từng cổng COM.
/// </summary>
internal static class AtCommandTraceLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        AppPaths.UserDataDirectory,
        "Logs");
    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        "at_commands.log");
    private const long RotateAtBytes = 50L * 1024 * 1024;

    private static StreamWriter? _writer;
    private static Timer? _flushTimer;
    private static bool _sessionWritten;

    public static string CurrentLogPath => LogPath;

    public static void Open(string portName) => Write("OPEN", portName);

    public static void Close(string portName) => Write("CLOSE", portName);

    public static void Tx(string portName, string command) =>
        Write("TX", portName, command);

    public static void Rx(string portName, string data) =>
        Write("RX", portName, data);

    public static void Timeout(string portName, string command) =>
        Write("TIMEOUT", portName, command);

    public static void Error(string portName, string data) =>
        Write("ERROR", portName, data);

    public static void State(string portName, string data) =>
        Write("STATE", portName, data);

    private static void Write(string kind, string portName, string? payload = null)
    {
        try
        {
            lock (Sync)
            {
                EnsureWriter();
                if (_writer == null) return;

                if (!_sessionWritten)
                {
                    _writer.WriteLine(
                        $"SESSION|{Timestamp()}|PID={Environment.ProcessId}|PROCESS={Escape(Process.GetCurrentProcess().ProcessName)}");
                    _sessionWritten = true;
                }

                string line = payload == null
                    ? $"{kind}|{Timestamp()}|{Escape(portName)}"
                    : $"{kind}|{Timestamp()}|{Escape(portName)}|{Escape(payload)}";
                _writer.WriteLine(line);
            }
        }
        catch
        {
            // Logging must never delay or stop a modem workflow.
        }
    }

    private static void EnsureWriter()
    {
        if (_writer != null) return;

        Directory.CreateDirectory(LogDirectory);
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= RotateAtBytes)
        {
            string archivePath = Path.Combine(
                LogDirectory,
                $"at_commands_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(LogPath, archivePath, overwrite: true);
        }

        var stream = new FileStream(
            LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false)
        {
            AutoFlush = false
        };

        _flushTimer = new Timer(
            _ => Flush(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(dispose: true);
    }

    private static void Flush(bool dispose = false)
    {
        try
        {
            lock (Sync)
            {
                _writer?.Flush();
                if (!dispose) return;
                _flushTimer?.Dispose();
                _flushTimer = null;
                _writer?.Dispose();
                _writer = null;
            }
        }
        catch
        {
        }
    }

    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
