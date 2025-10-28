using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace YellowFox.Desktop.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning 
                ? new SolidColorBrush(Color.Parse("#4CAF50")) // Green when running
                : new SolidColorBrush(Color.Parse("#9E9E9E")); // Gray when stopped
        }
        
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
