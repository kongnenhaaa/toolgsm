using gsm.Services;

namespace gsm.Tests;

public sealed class IcOcServiceTests
{
    [Fact]
    public void ResultParser_PreservesFalseStateForEmployFiltering()
    {
        const string line =
            "TOOLGSM_ICOC_RESULT_JSON:{\"success\":true,\"message\":\"xong\",\"items\":[" +
            "{\"port\":\"COM7\",\"phone\":\"0812345678\",\"normalizedPhone\":\"84812345678\"," +
            "\"success\":true,\"incomingEnabled\":false,\"outgoingEnabled\":true," +
            "\"eligibleForEmploy\":true,\"changed\":false,\"message\":\"Gọi đến = Không\"}]}";

        bool parsed = IcOcService.TryParseResultLine(line, out IcOcBatchResult result);

        Assert.True(parsed);
        Assert.True(result.Success);
        IcOcResultItem item = Assert.Single(result.Items);
        Assert.Equal("COM7", item.Port);
        Assert.False(item.IncomingEnabled);
        Assert.True(item.OutgoingEnabled);
        Assert.True(item.EligibleForEmploy);
    }

    [Fact]
    public void ResultParser_LeavesMissingStatesUnknown()
    {
        const string line =
            "TOOLGSM_ICOC_RESULT_JSON:{\"success\":false,\"message\":\"lỗi\",\"items\":[" +
            "{\"port\":\"COM8\",\"phone\":\"\",\"success\":false," +
            "\"eligibleForEmploy\":false,\"changed\":false,\"message\":\"không rõ\"}]}";

        Assert.True(IcOcService.TryParseResultLine(line, out IcOcBatchResult result));
        IcOcResultItem item = Assert.Single(result.Items);
        Assert.Null(item.IncomingEnabled);
        Assert.Null(item.OutgoingEnabled);
        Assert.False(item.EligibleForEmploy);
    }

    [Fact]
    public void ProgressParser_ParsesCountdownAndCompletedItem()
    {
        const string line =
            "TOOLGSM_ICOC_PROGRESS_JSON:{\"completed\":3,\"total\":11," +
            "\"port\":\"COM16\",\"phone\":\"0944552351\"," +
            "\"stage\":\"Đã mở IC + OC\",\"waitSeconds\":7," +
            "\"item\":{\"port\":\"COM16\",\"phone\":\"0944552351\"," +
            "\"success\":true,\"incomingEnabled\":true,\"outgoingEnabled\":true," +
            "\"eligibleForEmploy\":true,\"changed\":true,\"message\":\"Thành công\"}}";

        bool parsed = IcOcService.TryParseProgressLine(line, out IcOcProgress progress);

        Assert.True(parsed);
        Assert.Equal(3, progress.Completed);
        Assert.Equal(11, progress.Total);
        Assert.Equal("COM16", progress.Port);
        Assert.Equal(7, progress.WaitSeconds);
        Assert.NotNull(progress.Item);
        Assert.True(progress.Item.Success);
        Assert.True(progress.Item.Changed);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("MO", true)]
    [InlineData("active", true)]
    [InlineData("0", false)]
    [InlineData("KHOA", false)]
    [InlineData("locked", false)]
    public void NormalizeState_RecognizesOneBssValues(string value, bool expected)
    {
        Assert.Equal(expected, IcOcService.NormalizeState(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không rõ")]
    public void NormalizeState_DoesNotGuessUnknownValues(string? value)
    {
        Assert.Null(IcOcService.NormalizeState(value));
    }

    [Theory]
    [InlineData("MO", "MO")]
    [InlineData("mo", "MO")]
    [InlineData(" KHOA ", "KHOA")]
    [InlineData("GIU", "GIU")]
    [InlineData("không hợp lệ", "MO")]
    [InlineData(null, "MO")]
    public void NormalizeAction_RecognizesSupportedActions(string? value, string expected)
    {
        Assert.Equal(expected, IcOcService.NormalizeAction(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOOLGSM_ICOC_RESULT_JSON:{bad-json}")]
    public void ResultParser_RejectsInvalidOutput(string? value)
    {
        Assert.False(IcOcService.TryParseResultLine(value, out _));
    }
}
