using System.IO;
using System.Text.Json;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// Persists the per-device delay offsets the user settled on during the
/// synchronization calibration workflow, so future sessions start with the last
/// known-good alignment instead of 0 ms on every device.
/// </summary>
public sealed class SyncOffsetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, int> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public SyncOffsetStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeakerConnect",
            "sync-offsets.json"))
    {
    }

    public SyncOffsetStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Load();
    }

    public bool TryGet(string deviceId, out int delayMilliseconds)
    {
        lock (_gate)
        {
            return _offsets.TryGetValue(deviceId, out delayMilliseconds);
        }
    }

    public void Save(string deviceId, int delayMilliseconds)
    {
        lock (_gate)
        {
            _offsets[deviceId] = Math.Clamp(delayMilliseconds, 0, 5_000);
            WriteFile();
        }
    }

    public void Remove(string deviceId)
    {
        lock (_gate)
        {
            if (_offsets.Remove(deviceId))
            {
                WriteFile();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json, SerializerOptions);
            if (loaded is not null)
            {
                _offsets = new Dictionary<string, int>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never prevent the app from starting.
            _offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_offsets, SerializerOptions));
        }
        catch (Exception)
        {
            // Losing a saved offset is non-fatal; the next save retries.
        }
    }
}
