using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpeakerConnect.ViewModels;

/// <summary>
/// Shows a view only when the bound <see cref="AppPage"/> equals the
/// <see cref="IValueConverter.Parameter"/> (the page this view represents).
/// </summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AppPage page &&
            parameter is string name &&
            Enum.TryParse<AppPage>(name, ignoreCase: true, out var expected))
        {
            return page == expected ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Inverts a boolean for visibility binding.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Maps the routing-running flag to "Routing" / "Stopped" text.</summary>
public sealed class RunningStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool running && running ? "Routing" : "Stopped";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Shows content only when the bound string is non-empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Maps the routing-running flag to a status brush (green when running, grey
/// otherwise). Brushes are looked up from application resources so the color
/// follows the active theme.
/// </summary>
public sealed class RunningToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is bool running && running ? "SuccessBrush" : "TextTertiaryBrush";
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
