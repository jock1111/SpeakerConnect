using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// Persists small UI preferences (theme choice) separately from the per-device
/// audio settings. Uses the same atomic-write discipline as the other stores.
/// </summary>
public sealed class UiPreferencesStore : IDisposable
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private UiPreferencesDocument _document;
    private bool _disposed;

    public UiPreferencesStore()
        : this(DefaultFilePath)
    {
    }

    public UiPreferencesStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _document = Load();
    }

    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeakerConnect");

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "ui-preferences.json");

    public ThemeMode Theme
    {
        get
        {
            lock (_gate)
            {
                return _document.Theme;
            }
        }
    }

    public void SaveTheme(ThemeMode theme)
    {
        lock (_gate)
        {
            if (_disposed || _document.Theme == theme)
            {
                return;
            }

            _document.Theme = theme;
            FlushLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void FlushLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_document, JsonOptions);
            AtomicFile.WriteAllText(_filePath, json);
        }
        catch (Exception)
        {
            // A preferences write must never break the application.
        }
    }

    private UiPreferencesDocument Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UiPreferencesDocument { Theme = ThemeMode.Dark };
            }

            var json = File.ReadAllText(_filePath);
            var document = JsonSerializer.Deserialize<UiPreferencesDocument>(json, JsonOptions);
            return document ?? new UiPreferencesDocument { Theme = ThemeMode.Dark };
        }
        catch (Exception)
        {
            AtomicFile.TryBackupCorruptFile(_filePath);
            return new UiPreferencesDocument { Theme = ThemeMode.Dark };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class UiPreferencesDocument
    {
        [JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
        public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    }
}
