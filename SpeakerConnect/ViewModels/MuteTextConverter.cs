using System;
using System.Globalization;
using System.Windows.Data;

namespace SpeakerConnect.ViewModels;

/// <summary>
/// Converts a boolean mute state to toggle button text.
/// </summary>
public sealed class MuteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? "Unmute" : "Mute";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
