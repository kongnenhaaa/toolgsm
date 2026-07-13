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
    private string _recipient = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _result = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _updatedAt = string.Empty;
}
