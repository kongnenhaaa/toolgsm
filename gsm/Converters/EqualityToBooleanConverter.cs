using System;
using System.Globalization;
using System.Windows.Data;

namespace gsm.Converters
{
    public class EqualityToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            return value.ToString() == parameter.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked)
            {
                if (int.TryParse(parameter.ToString(), out int intValue))
                {
                    return intValue;
                }
                return parameter;
            }

            return Binding.DoNothing;
        }
    }
}
