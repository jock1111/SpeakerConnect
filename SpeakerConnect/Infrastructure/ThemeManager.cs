using System.Windows;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// Swaps the application's theme resource dictionary at runtime. Theme brushes are
/// referenced through <c>DynamicResource</c> everywhere, so replacing the merged
/// dictionary updates the whole UI without restarting.
/// </summary>
public static class ThemeManager
{
    private const string DarkDictionary = "Themes/Dark.xaml";
    private const string LightDictionary = "Themes/Light.xaml";

    /// <summary>The theme currently applied to <see cref="Application.Resources"/>.</summary>
    public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

    /// <summary>Resolves <see cref="ThemeMode.System"/> against the Windows setting.</summary>
    public static ThemeMode EffectiveMode(ThemeMode mode)
    {
        if (mode != ThemeMode.System)
        {
            return mode;
        }

        try
        {
            var light = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return light is int value && value != 0 ? ThemeMode.Light : ThemeMode.Dark;
        }
        catch (Exception)
        {
            return ThemeMode.Dark;
        }
    }

    /// <summary>
    /// Applies a theme before the main window is shown, or swaps it live afterwards.
    /// Safe to call when no application is running (diagnostics host).
    /// </summary>
    public static void Apply(ThemeMode mode)
    {
        var effective = EffectiveMode(mode);
        Current = effective;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                effective == ThemeMode.Light ? LightDictionary : DarkDictionary,
                UriKind.Relative)
        };

        // Replace the existing theme dictionary in place so resource lookup order
        // (and any DynamicResource subscribers) is preserved.
        for (var index = app.Resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var existing = app.Resources.MergedDictionaries[index];
            var source = existing.Source?.OriginalString ?? string.Empty;
            if (source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                app.Resources.MergedDictionaries[index] = dictionary;
                return;
            }
        }

        app.Resources.MergedDictionaries.Add(dictionary);
    }
}
