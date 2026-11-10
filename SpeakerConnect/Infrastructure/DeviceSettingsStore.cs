using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// A saved preference snapshot for one persistent Windows endpoint id. Null
/// values mean "the user never saved this setting"; they must never be treated
/// as defaults that overwrite another device's saved data.
/// </summary>
public sealed record DevicePreference(
    double? VolumePercent,
    int? DelayMilliseconds,
    bool? IsSelected);

/// <summary>
/// Persists per-endpoint volume, manual synchronization delay, the last selected
/// output group, the loopback source, and the sync reference. Everything is keyed
/// by the persistent Windows endpoint id, which survives an app restart and a
/// temporary disconnect/reconnect of the same physical device.
///
/// Updates are per device: saving one device never rewrites the entries of other
/// devices, so a disconnected speaker keeps its settings until the user changes
/// or clears them explicitly. Writes are coalesced briefly and performed through
/// <see cref="AtomicFile"/> so an interrupted write cannot corrupt the file.
/// </summary>
public sealed class DeviceSettingsStore : IDisposable
{
    public const string FileName = "device-settings.json";
    public const string LegacyFileName = "sync-offsets.json";

    private const int CurrentVersion = 2;
    private static readonly TimeSpan DefaultAutoSaveDelay = TimeSpan.FromMilliseconds(300);
    private static readonly DevicePreference EmptyPreference = new(null, null, null);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _legacyFilePath;
    private readonly TimeSpan _autoSaveDelay;
    private readonly Timer? _writeTimer;
    private SettingsFile _settings = new();
    private bool _dirty;
    private bool _disposed;

    public DeviceSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeakerConnect",
            FileName))
    {
    }

    public DeviceSettingsStore(string filePath)
        : this(filePath, autoSaveDelay: null)
    {
    }

    /// <param name="autoSaveDelay">
    /// How long writes are coalesced. <see cref="TimeSpan.Zero"/> writes
    /// synchronously (used by diagnostics for deterministic assertions).
    /// </param>
    public DeviceSettingsStore(string filePath, TimeSpan? autoSaveDelay)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _legacyFilePath = Path.Combine(
            Path.GetDirectoryName(_filePath) ?? string.Empty,
            LegacyFileName);

        _autoSaveDelay = autoSaveDelay ?? DefaultAutoSaveDelay;
        if (_autoSaveDelay > TimeSpan.Zero)
        {
            _writeTimer = new Timer(
                _ => Flush(),
                state: null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        Load();
    }

    public string? ReferenceDeviceId
    {
        get
        {
            lock (_gate)
            {
                return _settings.ReferenceDeviceId;
            }
        }
    }

    public string? SourceDeviceId
    {
        get
        {
            lock (_gate)
            {
                return _settings.SourceDeviceId;
            }
        }
    }

    public bool TryGet(string deviceId, out DevicePreference preference)
    {
        var normalized = NormalizeId(deviceId);
        lock (_gate)
        {
            if (normalized is not null &&
                _settings.Devices.TryGetValue(normalized, out var entry))
            {
                preference = entry.ToPreference();
                return true;
            }

            preference = EmptyPreference;
            return false;
        }
    }

    /// <summary>Returns a copy so callers can inspect the saved set without holding the lock.</summary>
    public IReadOnlyDictionary<string, DevicePreference> GetSnapshot()
    {
        lock (_gate)
        {
            var copy = new Dictionary<string, DevicePreference>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _settings.Devices)
            {
                copy[pair.Key] = pair.Value.ToPreference();
            }

            return copy;
        }
    }

    public void SaveVolume(string deviceId, double volumePercent)
    {
        UpdateDevice(deviceId, volumePercent, delayMilliseconds: null, isSelected: null);
    }

    public void SaveDelay(string deviceId, int delayMilliseconds)
    {
        UpdateDevice(deviceId, volumePercent: null, delayMilliseconds, isSelected: null);
    }

    public void SaveSelection(string deviceId, bool isSelected)
    {
        UpdateDevice(deviceId, volumePercent: null, delayMilliseconds: null, isSelected);
    }

    /// <summary>Updates volume and delay in one write; used when flushing coalesced slider changes.</summary>
    public void SaveDevice(string deviceId, double? volumePercent, int? delayMilliseconds)
    {
        UpdateDevice(deviceId, volumePercent, delayMilliseconds, isSelected: null);
    }

    public void SaveReferenceDevice(string? deviceId)
    {
        var normalized = NormalizeId(deviceId);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (string.Equals(_settings.ReferenceDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.ReferenceDeviceId = normalized;
            MarkDirtyLocked();
        }
    }

    public void SaveSourceDevice(string? deviceId)
    {
        var normalized = NormalizeId(deviceId);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (string.Equals(_settings.SourceDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.SourceDeviceId = normalized;
            MarkDirtyLocked();
        }
    }

    /// <summary>Removes every saved value for one endpoint.</summary>
    public void Remove(string deviceId)
    {
        var normalized = NormalizeId(deviceId);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (normalized is not null && _settings.Devices.Remove(normalized))
            {
                MarkDirtyLocked();
            }
        }
    }

    /// <summary>
    /// Removes only the saved delay. Volume, selection, and reference data for the
    /// endpoint are preserved; this is what "Clear saved offsets" uses.
    /// </summary>
    public void RemoveDelay(string deviceId)
    {
        var normalized = NormalizeId(deviceId);
        lock (_gate)
        {
            if (_disposed ||
                normalized is null ||
                !_settings.Devices.TryGetValue(normalized, out var entry) ||
                entry.DelayMilliseconds is null)
            {
                return;
            }

            entry.DelayMilliseconds = null;
            if (entry.IsEmpty)
            {
                _settings.Devices.Remove(normalized);
            }

            MarkDirtyLocked();
        }
    }

    /// <summary>Writes any coalesced change immediately.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            WriteFileLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _writeTimer?.Dispose();
        Flush();
    }

    private void UpdateDevice(
        string deviceId,
        double? volumePercent,
        int? delayMilliseconds,
        bool? isSelected)
    {
        var normalized = NormalizeId(deviceId);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_settings.Devices.TryGetValue(normalized, out var entry))
            {
                entry = new DeviceEntry();
                _settings.Devices[normalized] = entry;
            }

            if (volumePercent is double volume && double.IsFinite(volume))
            {
                entry.VolumePercent = Math.Clamp(volume, 0, 100);
            }

            if (delayMilliseconds is int delay)
            {
                entry.DelayMilliseconds = Math.Clamp(delay, 0, 5_000);
            }

            if (isSelected is bool selected)
            {
                entry.IsSelected = selected;
            }

            MarkDirtyLocked();
        }
    }

    private void MarkDirtyLocked()
    {
        _dirty = true;

        if (_writeTimer is null)
        {
            WriteFileLocked();
            return;
        }

        _writeTimer.Change(_autoSaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<SettingsFile>(json, SerializerOptions);
                _settings = Normalize(loaded);
                return;
            }
            catch (JsonException)
            {
                // Preserve the bad document for inspection, then start with defaults.
                AtomicFile.TryBackupCorruptFile(_filePath);
            }
            catch (Exception)
            {
                // An unreadable file must never prevent the application from starting.
            }

            _settings = new SettingsFile();
            return;
        }

        _settings = new SettingsFile();
        MigrateLegacyOffsets();
    }

    /// <summary>
    /// Imports the pre-2.0 delay-only file once. The new file is written during
    /// migration, so a later clear/remove is not undone by re-importing stale data.
    /// </summary>
    private void MigrateLegacyOffsets()
    {
        try
        {
            if (!File.Exists(_legacyFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_legacyFilePath);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (legacy is null)
            {
                return;
            }

            var imported = false;
            foreach (var pair in legacy)
            {
                var deviceId = NormalizeId(pair.Key);
                if (deviceId is null)
                {
                    continue;
                }

                _settings.Devices[deviceId] = new DeviceEntry
                {
                    DelayMilliseconds = Math.Clamp(pair.Value, 0, 5_000)
                };
                imported = true;
            }

            if (imported)
            {
                _dirty = true;
                WriteFileLocked();
            }
        }
        catch (Exception)
        {
            // A broken legacy file must not block startup.
        }
    }

    private static SettingsFile Normalize(SettingsFile? loaded)
    {
        var normalized = new SettingsFile
        {
            Version = CurrentVersion
        };

        if (loaded is null)
        {
            return normalized;
        }

        normalized.ReferenceDeviceId = NormalizeId(loaded.ReferenceDeviceId);
        normalized.SourceDeviceId = NormalizeId(loaded.SourceDeviceId);

        if (loaded.Devices is null)
        {
            return normalized;
        }

        foreach (var pair in loaded.Devices)
        {
            var deviceId = NormalizeId(pair.Key);
            if (deviceId is null || pair.Value is null)
            {
                continue;
            }

            var entry = new DeviceEntry();

            if (pair.Value.VolumePercent is double volume && double.IsFinite(volume))
            {
                entry.VolumePercent = Math.Clamp(volume, 0, 100);
            }

            if (pair.Value.DelayMilliseconds is int delay)
            {
                entry.DelayMilliseconds = Math.Clamp(delay, 0, 5_000);
            }

            if (pair.Value.IsSelected is bool selected)
            {
                entry.IsSelected = selected;
            }

            if (!entry.IsEmpty)
            {
                normalized.Devices[deviceId] = entry;
            }
        }

        return normalized;
    }

    private void WriteFileLocked()
    {
        try
        {
            _settings.Version = CurrentVersion;
            AtomicFile.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(_settings, SerializerOptions));
            _dirty = false;
        }
        catch (Exception)
        {
            // Keep the dirty flag: the next change or shutdown retry tries again.
        }
    }

    private static string? NormalizeId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return deviceId.Trim();
    }

    private sealed class SettingsFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, DeviceEntry> Devices { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string? ReferenceDeviceId { get; set; }

        public string? SourceDeviceId { get; set; }
    }

    private sealed class DeviceEntry
    {
        public double? VolumePercent { get; set; }

        public int? DelayMilliseconds { get; set; }

        public bool? IsSelected { get; set; }

        [JsonIgnore]
        public bool IsEmpty => VolumePercent is null &&
                               DelayMilliseconds is null &&
                               IsSelected is null;

        public DevicePreference ToPreference() =>
            new(VolumePercent, DelayMilliseconds, IsSelected);
    }
}
