namespace gsm.Services;

/// <summary>
/// Compatibility facade for the former on-disk UART trace logger.
///
/// ToolGSM keeps operational logs in memory only. These methods intentionally
/// do nothing so modem call sites cannot create at_commands.log or rotated
/// trace files, even during very noisy serial sessions.
/// </summary>
internal static class AtCommandTraceLogger
{
    public static string CurrentLogPath => string.Empty;

    public static void Open(string portName) { }

    public static void Close(string portName) { }

    public static void Tx(string portName, string command) { }

    public static void Rx(string portName, string data) { }

    public static void Timeout(string portName, string command) { }

    public static void Error(string portName, string data) { }

    public static void State(string portName, string data) { }
}
