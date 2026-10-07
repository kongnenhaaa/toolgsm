using gsm.Services;

namespace gsm.Tests;

public sealed class AtCommandTraceLoggerTests
{
    [Fact]
    public void TraceFacade_IsDiskDisabled()
    {
        AtCommandTraceLogger.Open("COM1");
        AtCommandTraceLogger.Tx("COM1", "AT");
        AtCommandTraceLogger.Rx("COM1", "OK");
        AtCommandTraceLogger.Timeout("COM1", "AT+CSQ");
        AtCommandTraceLogger.Error("COM1", "test");
        AtCommandTraceLogger.State("COM1", "ready");
        AtCommandTraceLogger.Close("COM1");

        Assert.Empty(AtCommandTraceLogger.CurrentLogPath);
    }
}
