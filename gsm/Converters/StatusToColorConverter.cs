using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace gsm.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status == "Đang hoạt động")
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                if (status == "Mất kết nối")
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726")); // Đang kết nối...
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
