using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using gsm.Models;

namespace gsm.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status == SimStatus.Active)
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
                if (status == "Mất kết nối" || status == SimStatus.NoResponse || status == SimStatus.SecurityBlocked)
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                if (status == SimStatus.Connecting)
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
