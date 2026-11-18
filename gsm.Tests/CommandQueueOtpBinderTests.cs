using gsm.Models;

namespace gsm.Tests;

public sealed class CommandQueueOtpBinderTests
{
    [Fact]
    public void NewOtp_UpdatesOnlyTheMatchingCommand_NotEveryRowOnThePort()
    {
        var oldMessage = new CommandQueueItem
        {
            CommandId = "old-command",
            PortId = "COM7",
            Source = "Firebase",
            Otp = "111111",
            Status = "otp_received"
        };
        var newMessage = new CommandQueueItem
        {
            CommandId = "new-command",
            PortId = "COM7",
            Source = "Firebase",
            Status = "sent"
        };
        CommandQueueItem[] queue = [oldMessage, newMessage];

        bool assigned = CommandQueueOtpBinder.TryAssign(
            queue,
            "new-command",
            "222222");

        Assert.True(assigned);
        Assert.Equal("111111", oldMessage.Otp);
        Assert.Equal("222222", newMessage.Otp);
        Assert.Equal("otp_received", newMessage.Status);
    }

    [Fact]
    public void UnknownCommand_DoesNotAttachOtpToAnotherRowOnSamePort()
    {
        var existing = new CommandQueueItem
        {
            CommandId = "existing-command",
            PortId = "COM7",
            Source = "ZaloTool API"
        };

        bool assigned = CommandQueueOtpBinder.TryAssign(
            [existing],
            "different-command",
            "333333");

        Assert.False(assigned);
        Assert.Equal(string.Empty, existing.Otp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData(null)]
    public void EmptyOtp_DoesNotOverwriteTheMessageOtp(string? otp)
    {
        var item = new CommandQueueItem
        {
            CommandId = "command-1",
            Otp = "444444"
        };

        bool assigned = CommandQueueOtpBinder.TryAssign(
            [item],
            "command-1",
            otp);

        Assert.False(assigned);
        Assert.Equal("444444", item.Otp);
    }
}
