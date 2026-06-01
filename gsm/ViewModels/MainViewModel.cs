using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using gsm.Models;
using gsm.Services;
using System.Threading.Tasks;
using System.Windows;

namespace gsm.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGsmModemService _modemService;

    [ObservableProperty]
    private ObservableCollection<SimPort> _ports = new();

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _smsMessages = new();

    [ObservableProperty]
    private SimPort? _selectedPort;

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0 for GSM, 1 for SMS

    public MainViewModel()
    {
        _modemService = new GsmModemService();
        LoadMockData();
    }

    private void LoadMockData()
    {
        Ports.Add(new SimPort { PortName = "COM216", PhoneNumber = "0828099482", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:45:40 01/06/2026", Otp = "491633", LastMessageContent = "ONEBSS OTP: 491633", Imei = "354123456789012", Balance = "50000", ExpiryDate = "10/10/2026", CallCount = 1 });
        Ports.Add(new SimPort { PortName = "COM222", PhoneNumber = "0833680008", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:30:17 01/06/2026", Otp = "", LastMessageContent = "(TB) UU DAI +200MB data/24h", Imei = "354123456789033", Balance = "0", ExpiryDate = "10/10/2026" });
        Ports.Add(new SimPort { PortName = "COM227", PhoneNumber = "0822222708", NetworkProvider = "VINAPHONE", LastReceivedTime = "14:30:16 01/06/2026", Otp = "", LastMessageContent = "(TB) UU DAI +200MB data/24h" });
        
        SmsMessages.Add(new SmsMessage { PortName = "COM216", Sender = "VNPT", ReceiverPhone = "0828099482", ReceivedTime = "14:45:40 01/06/2026", Otp = "491633", Content = "ONEBSS OTP: 491633" });
        SmsMessages.Add(new SmsMessage { PortName = "COM222", Sender = "888", ReceiverPhone = "0833680008", ReceivedTime = "14:30:17 01/06/2026", Otp = "", Content = "(TB) UU DAI +200MB data/24h khi thue bao 84833680008 soan..." });
        SmsMessages.Add(new SmsMessage { PortName = "COM228", Sender = "VNPT", ReceiverPhone = "0836522379", ReceivedTime = "13:54:59 01/06/2026", Otp = "993963", Content = "ONEBSS OTP: 993963" });
    }

    [RelayCommand]
    private void SwitchTab(string tabIndex)
    {
        if (int.TryParse(tabIndex, out int index))
        {
            SelectedTabIndex = index;
        }
    }

    [RelayCommand]
    private async Task CheckBalanceAsync()
    {
        if (SelectedPort != null)
        {
            string result = await _modemService.SendCommandAsync(SelectedPort.PortName, "AT+CUSD=1,\"*101#\",15");
            MessageBox.Show($"Kết quả kiểm tra số dư cổng {SelectedPort.PortName}:\n{result}", "Thông báo");
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một cổng để kiểm tra số dư.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RenewSim()
    {
        MessageBox.Show("Chức năng gia hạn SIM đang được phát triển.", "Thông báo");
    }

    [RelayCommand]
    private void ChangeImei()
    {
        MessageBox.Show("Chức năng đổi IMEI đang được phát triển.", "Thông báo");
    }
}
