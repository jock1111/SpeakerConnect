using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// Persisted Smart Volume Balance settings. Gains are stored by the Windows
/// endpoint id, which is stable for a physical/reconnected endpoint (the same id is
/// used by the routing engine and the device watcher). A missing id simply means
/// "no calibration yet" and falls back to 1.0 (no attenuation).
/// </summary>
public sealed class VolumeBalanceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private VolumeBalanceSettings _settings = new();

    public VolumeBalanceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeakerConnect",
            "volume-balance.json"))
    {
    }

    public VolumeBalanceStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Load();
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _settings.Enabled;
            }
        }
    }

    public bool TryGetGain(string deviceId, out float gain)
    {
        lock (_gate)
        {
            return _settings.Gains.TryGetValue(deviceId, out gain);
        }
    }

    /// <summary>Returns a copy so callers can inspect the saved set without holding the lock.</summary>
    public IReadOnlyDictionary<string, float> GetSnapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, float>(_settings.Gains, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_settings.Enabled == enabled)
            {
                return;
            }

            _settings.Enabled = enabled;
            WriteFile();
        }
    }

    public void SaveGain(string deviceId, float gain)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        lock (_gate)
        {
            _settings.Gains[deviceId] = Math.Clamp(gain, 0.01f, 1f);
            WriteFile();
        }
    }

    public void RemoveGain(string deviceId)
    {
        lock (_gate)
        {
            if (_settings.Gains.Remove(deviceId))
            {
                WriteFile();
            }
        }
    }

    /// <summary>
    /// Replaces the saved per-device gains in one write. Used by Save Calibration so
    /// all manual adjustments and freshly measured gains are committed atomically.
    /// </summary>
    public void SaveGains(IEnumerable<KeyValuePair<string, float>> gains)
    {
        ArgumentNullException.ThrowIfNull(gains);

        lock (_gate)
        {
            var updated = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in gains)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && float.IsFinite(pair.Value))
                {
                    updated[pair.Key] = Math.Clamp(pair.Value, 0.01f, 1f);
                }
            }

            _settings.Gains = updated;
            WriteFile();
        }
    }

    /// <summary>
    /// Clears the saved loudness corrections while keeping the user's automatic
    /// application preference. Reset Calibration uses this: the feature stays
    /// enabled, but every speaker returns to unity gain.
    /// </summary>
    public void ResetGains()
    {
        lock (_gate)
        {
            _settings.Gains.Clear();
            WriteFile();
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _settings = new VolumeBalanceSettings();
            WriteFile();
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
            var loaded = JsonSerializer.Deserialize<VolumeBalanceSettings>(json, SerializerOptions);
            if (loaded is null)
            {
                return;
            }

            var gains = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in loaded.Gains)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && float.IsFinite(pair.Value))
                {
                    gains[pair.Key] = Math.Clamp(pair.Value, 0.01f, 1f);
                }
            }

            _settings = new VolumeBalanceSettings
            {
                Enabled = loaded.Enabled,
                Gains = gains
            };
        }
        catch (JsonException)
        {
            // Preserve the bad document for inspection, then start with defaults.
            AtomicFile.TryBackupCorruptFile(_filePath);
            _settings = new VolumeBalanceSettings();
        }
        catch (Exception)
        {
            // A corrupt or unreadable settings file must never prevent startup.
            _settings = new VolumeBalanceSettings();
        }
    }

    private void WriteFile()
    {
        try
        {
            AtomicFile.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(_settings, SerializerOptions));
        }
        catch (Exception)
        {
            // Losing a calibration file is non-fatal; a later save retries.
        }
    }

    private sealed class VolumeBalanceSettings
    {
        public bool Enabled { get; set; }

        public Dictionary<string, float> Gains { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
