using gsm.Services;

namespace gsm.Tests;

public class SmsReceiveFrameTests
{
    [Fact]
    public void ShortDirectCmtBeforeSignalUrc_IsExtractedImmediately()
    {
        const string data = "+CMT: \"505751\",\"\",\"26/07/25,07:47:42+28\"\r\n609998\r\n+CSQ: 25,99\r\n";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data);

        Assert.Single(frames);
        Assert.Contains("609998", frames[0]);
    }

    [Fact]
    public void SplitDirectCmtWithoutBoundary_IsRetainedUntilMoreBytesArrive()
    {
        const string partial = "+CMT: \"505751\"\r\n609";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(partial);

        Assert.Empty(frames);
    }

    [Fact]
    public void DirectCmtWithOk_IsExtractedWithoutConsumingFollowingUrc()
    {
        const string data = "+CMT: \"505751\"\r\nMa OTP la 609998\r\nOK\r\n+CSQ: 25,99\r\n";

        IReadOnlyList<string> frames =
            GsmModemService.ExtractCompleteDirectCmtFramesForTest(data);

        Assert.Single(frames);
        Assert.Contains("Ma OTP la 609998", frames[0]);
        Assert.DoesNotContain("+CSQ", frames[0]);
    }
}
