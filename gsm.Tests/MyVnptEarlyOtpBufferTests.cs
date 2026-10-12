using gsm.Services;

namespace gsm.Tests;

public sealed class MyVnptEarlyOtpBufferTests
{
    [Fact]
    public void TryTake_ReturnsOtp_ForSameFreshSimSession()
    {
        var buffer = new MyVnptEarlyOtpBuffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        buffer.Store("COM17", "ccid-a", 7, "123456", now);

        bool taken = buffer.TryTake(
            "COM17",
            "ccid-a",
            7,
            now.AddSeconds(1),
            TimeSpan.FromMinutes(2),
            out string otp);

        Assert.True(taken);
        Assert.Equal("123456", otp);
        Assert.False(buffer.TryTake(
            "COM17",
            "ccid-a",
            7,
            now.AddSeconds(1),
            TimeSpan.FromMinutes(2),
            out _));
    }

    [Theory]
    [InlineData("ccid-b", 7, 1)]
    [InlineData("ccid-a", 8, 1)]
    [InlineData("ccid-a", 7, 121)]
    public void TryTake_RejectsWrongSessionOrExpiredOtp(
        string ccid,
        long epoch,
        int ageSeconds)
    {
        var buffer = new MyVnptEarlyOtpBuffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        buffer.Store("COM17", "ccid-a", 7, "123456", now);

        Assert.False(buffer.TryTake(
            "COM17",
            ccid,
            epoch,
            now.AddSeconds(ageSeconds),
            TimeSpan.FromMinutes(2),
            out _));
    }

    [Fact]
    public void Store_KeepsNewestOtpPerPort()
    {
        var buffer = new MyVnptEarlyOtpBuffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        buffer.Store("COM17", "ccid-a", 7, "111111", now);
        buffer.Store("COM17", "ccid-a", 7, "222222", now.AddSeconds(1));

        Assert.True(buffer.TryTake(
            "COM17",
            "ccid-a",
            7,
            now.AddSeconds(2),
            TimeSpan.FromMinutes(2),
            out string otp));
        Assert.Equal("222222", otp);
    }
}
