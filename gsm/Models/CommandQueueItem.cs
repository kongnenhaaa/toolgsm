using CommunityToolkit.Mvvm.ComponentModel;

namespace gsm.Models;

public partial class CommandQueueItem : ObservableObject
{
    [ObservableProperty]
    private string _commandId = string.Empty;

    [ObservableProperty]
    private string _portId = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _recipient = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _otp = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _result = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _updatedAt = string.Empty;
}

public static class CommandQueueOtpBinder
{
    public static bool TryAssign(
        IEnumerable<CommandQueueItem> items,
        string commandId,
        string? otp,
        string status = "otp_received")
    {
        if (string.IsNullOrWhiteSpace(commandId)
            || string.IsNullOrWhiteSpace(otp)
            || otp.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CommandQueueItem? item = items.FirstOrDefault(candidate =>
            candidate.CommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;

        item.Otp = otp.Trim();
        if (!string.IsNullOrWhiteSpace(status)) item.Status = status;
        item.Result = "OTP received";
        item.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
        return true;
    }
}
