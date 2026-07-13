using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace gsm.Models
{
    public class ImeiRecord : INotifyPropertyChanged
    {
        private int _id;
        public int Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        private string _phoneNumber = "";
        public string PhoneNumber
        {
            get => _phoneNumber;
            set { _phoneNumber = value; OnPropertyChanged(); }
        }

        private string _iccid = "";
        public string Iccid
        {
            get => _iccid;
            set { _iccid = value; OnPropertyChanged(); }
        }

        private string _imei = "";
        public string Imei
        {
            get => _imei;
            set { _imei = value; OnPropertyChanged(); }
        }

        private string _message = "";
        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
