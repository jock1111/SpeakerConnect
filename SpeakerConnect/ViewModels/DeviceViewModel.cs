using SpeakerConnect.Audio;
using SpeakerConnect.Infrastructure;
using SpeakerConnect.Models;

namespace SpeakerConnect.ViewModels;

/// <summary>
/// UI state for one Windows render endpoint.
/// </summary>
public sealed class DeviceViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isSource;
    private bool _isReference;
    private bool _canSelect;
    private double _volume = 80;
    private double _balanceGainPercent = 100;
    private bool _canAdjustBalance = true;
    private bool _canAdjustDelay = true;
    private int _delayMilliseconds;
    private string _runtimeStatus = "Idle";
    private string _softwareStatusLine = string.Empty;
    private bool _isActive;
    private double? _measuredOffsetMilliseconds;
    private double? _measurementConfidencePercent;
    private string? _syncMeasurementMethod;
    private DateTimeOffset? _syncMeasuredAtUtc;
    private string? _syncMeasurementFailure;

    public DeviceViewModel(AudioDeviceInfo device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public AudioDeviceInfo Device { get; }

    public string Id => Device.Id;

    public string DisplayName => Device.DisplayName;

    public string FriendlyName => Device.FriendlyName;

    public string Details => Device.FormatDescription;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsControlEnabled));
                OnPropertyChanged(nameof(SelectedLabel));
                OnPropertyChanged(nameof(BalanceRoleLabel));
                OnPropertyChanged(nameof(ShowOutputBadge));
            }
        }
    }

    public bool IsSource
    {
        get => _isSource;
        set
        {
            if (SetProperty(ref _isSource, value))
            {
                OnPropertyChanged(nameof(RoleLabel));
                OnPropertyChanged(nameof(IsControlEnabled));
                OnPropertyChanged(nameof(IsReference));
                OnPropertyChanged(nameof(BalanceRoleLabel));
                OnPropertyChanged(nameof(ShowSourceBadge));
                OnPropertyChanged(nameof(ShowOutputBadge));
            }
        }
    }

    /// <summary>When true, this destination is the sync reference that other speakers are calibrated against.</summary>
    public bool IsReference
    {
        get => _isReference;
        set
        {
            if (SetProperty(ref _isReference, value))
            {
                OnPropertyChanged(nameof(RoleLabel));
                OnPropertyChanged(nameof(IsControlEnabled));
                OnPropertyChanged(nameof(ShowReferenceBadge));
            }
        }
    }

    public bool CanSelect
    {
        get => _canSelect;
        internal set => SetProperty(ref _canSelect, value);
    }

    public bool IsControlEnabled => IsSelected && !IsSource;

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volume, clamped))
            {
                OnPropertyChanged(nameof(VolumeText));
            }
        }
    }

    /// <summary>
    /// Calibrated loudness correction for this endpoint. 100% means no attenuation;
    /// lower values make this speaker quieter. Values above unity are intentionally
    /// not offered, so balance can never add gain or clip.
    /// </summary>
    public double BalanceGainPercent
    {
        get => _balanceGainPercent;
        set
        {
            var clamped = Math.Clamp(value, 1, 100);
            if (SetProperty(ref _balanceGainPercent, clamped))
            {
                OnPropertyChanged(nameof(BalanceGainText));
                OnPropertyChanged(nameof(BalanceGainDb));
            }
        }
    }

    public double BalanceGainDb => BalanceGainPercent >= 99.95
        ? 0
        : 20.0 * Math.Log10(BalanceGainPercent / 100.0);

    public string BalanceGainText
    {
        get
        {
            var db = BalanceGainDb;
            var dbText = Math.Abs(db) < 0.05 ? "0.0 dB" : $"{db:0.0} dB";
            return $"{BalanceGainPercent:0}% ({dbText})";
        }
    }

    /// <summary>False while a calibration run is in progress; sliders are locked then.</summary>
    public bool CanAdjustBalance
    {
        get => _canAdjustBalance;
        internal set => SetProperty(ref _canAdjustBalance, value);
    }

    public string BalanceRoleLabel
    {
        get
        {
            if (IsSource)
            {
                return "Source";
            }

            return IsSelected ? "Output" : "Not routed";
        }
    }

    /// <summary>
    /// False while Auto Sync is measuring; the manual live test deliberately keeps the
    /// delay sliders editable so the user can align speakers by ear.
    /// </summary>
    public bool CanAdjustDelay
    {
        get => _canAdjustDelay;
        internal set => SetProperty(ref _canAdjustDelay, value);
    }

    public int DelayMilliseconds
    {
        get => _delayMilliseconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 5_000);
            if (SetProperty(ref _delayMilliseconds, clamped))
            {
                OnPropertyChanged(nameof(DelayText));
                OnPropertyChanged(nameof(SyncMeasurementText));
                OnPropertyChanged(nameof(SyncMeasurementDisplay));
            }
        }
    }

    /// <summary>
    /// Acoustic timing measured by Auto Sync, relative to the acoustic anchor (the
    /// latest reliable measured path). Zero is the anchor, negative means the sound
    /// arrived earlier, positive means later. This is not absolute hardware latency:
    /// the underlying correlation offset shares an arbitrary microphone/ring origin,
    /// so only differences between speakers are physically meaningful. It is also not
    /// the configured delay. Null means no acoustic measurement has been saved.
    /// </summary>
    public double? MeasuredOffsetMilliseconds => _measuredOffsetMilliseconds;

    public double? MeasurementConfidencePercent => _measurementConfidencePercent;

    public string? SyncMeasurementMethod => _syncMeasurementMethod;

    public DateTimeOffset? SyncMeasuredAtUtc => _syncMeasuredAtUtc;

    public bool HasSyncMeasurement => !string.IsNullOrWhiteSpace(SyncMeasurementText);

    /// <summary>
    /// Honest per-device timing label. A manual slider value is never described as a
    /// measurement; a failed Auto Sync attempt is shown separately from saved data.
    /// The method string records whether the acoustic result was measured for the
    /// source path or for a routed copy, so it stays honest if the device later
    /// changes role.
    /// </summary>
    public string SyncMeasurementText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_syncMeasurementFailure))
            {
                return $"Auto Sync: {_syncMeasurementFailure}";
            }

            if (_measuredOffsetMilliseconds is double measured)
            {
                var confidence = _measurementConfidencePercent is double value
                    ? $"{value:0}% confidence"
                    : "confidence unavailable";
                var method = string.IsNullOrWhiteSpace(_syncMeasurementMethod)
                    ? "acoustic measurement"
                    : _syncMeasurementMethod;
                var when = _syncMeasuredAtUtc is DateTimeOffset date
                    ? $" · {date.LocalDateTime:g}"
                    : string.Empty;
                var timing = Math.Abs(measured) < 0.5
                    ? "Measured acoustic anchor (0 ms reference)"
                    : measured < 0
                        ? $"Measured {-measured:0} ms earlier than the anchor"
                        : $"Measured {measured:0} ms later than the anchor";
                return $"{timing} ({confidence}) · {method}{when}";
            }

            if (!string.IsNullOrWhiteSpace(_syncMeasurementMethod))
            {
                return $"Manual delay: {DelayMilliseconds} ms (not acoustically measured)";
            }

            return string.Empty;
        }
    }

    public string SyncMeasurementDisplay => string.IsNullOrWhiteSpace(SyncMeasurementText)
        ? "No acoustic measurement saved; configured delay is manual."
        : SyncMeasurementText;

    public void ApplySyncMeasurement(
        double? measuredOffsetMilliseconds,
        double? confidencePercent,
        string? method,
        DateTimeOffset? measuredAtUtc)
    {
        _syncMeasurementFailure = null;
        _measuredOffsetMilliseconds = measuredOffsetMilliseconds;
        _measurementConfidencePercent = confidencePercent;
        _syncMeasurementMethod = method;
        _syncMeasuredAtUtc = measuredAtUtc;
        OnPropertyChanged(nameof(MeasuredOffsetMilliseconds));
        OnPropertyChanged(nameof(MeasurementConfidencePercent));
        OnPropertyChanged(nameof(SyncMeasurementMethod));
        OnPropertyChanged(nameof(SyncMeasuredAtUtc));
        OnPropertyChanged(nameof(SyncMeasurementText));
        OnPropertyChanged(nameof(SyncMeasurementDisplay));
        OnPropertyChanged(nameof(HasSyncMeasurement));
    }

    public void SetSyncMeasurementFailure(string? failure)
    {
        _syncMeasurementFailure = string.IsNullOrWhiteSpace(failure) ? null : failure;
        OnPropertyChanged(nameof(SyncMeasurementText));
        OnPropertyChanged(nameof(SyncMeasurementDisplay));
        OnPropertyChanged(nameof(HasSyncMeasurement));
    }

    public void ClearSyncMeasurement()
    {
        _syncMeasurementFailure = null;
        _measuredOffsetMilliseconds = null;
        _measurementConfidencePercent = null;
        _syncMeasurementMethod = null;
        _syncMeasuredAtUtc = null;
        OnPropertyChanged(nameof(MeasuredOffsetMilliseconds));
        OnPropertyChanged(nameof(MeasurementConfidencePercent));
        OnPropertyChanged(nameof(SyncMeasurementMethod));
        OnPropertyChanged(nameof(SyncMeasuredAtUtc));
        OnPropertyChanged(nameof(SyncMeasurementText));
        OnPropertyChanged(nameof(SyncMeasurementDisplay));
        OnPropertyChanged(nameof(HasSyncMeasurement));
    }

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        set => SetProperty(ref _runtimeStatus, value);
    }

    /// <summary>
    /// Software-side buffer diagnostics for this destination (jitter depth,
    /// underruns, dropped/drift-corrected frames, estimated software routing
    /// latency). Empty when the device is not part of a running session.
    /// </summary>
    public string SoftwareStatusLine
    {
        get => _softwareStatusLine;
        set
        {
            if (SetProperty(ref _softwareStatusLine, value))
            {
                OnPropertyChanged(nameof(HasSoftwareStatus));
            }
        }
    }

    public bool HasSoftwareStatus => !string.IsNullOrWhiteSpace(_softwareStatusLine);

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string RoleLabel
    {
        get
        {
            if (IsSource)
            {
                return "Loopback source - original playback continues";
            }

            if (IsReference)
            {
                return "Sync reference - other speakers align to this one";
            }

            return string.Empty;
        }
    }

    public string SelectedLabel => IsSelected ? "Routed" : string.Empty;

    public string VolumeText => $"{Volume:0}%";

    public string DelayText => $"{DelayMilliseconds} ms";

    // ---------------- Card badges ----------------

    /// <summary>
    /// Best-effort Bluetooth hint from the endpoint name. Windows does not expose a
    /// reliable transport-type flag, so this is a heuristic label only.
    /// </summary>
    public bool IsBluetooth => BluetoothEndpointHints.LooksLikeBluetooth(Device.FriendlyName);

    public bool IsDefaultDevice => Device.IsDefault;

    public bool ShowSourceBadge => IsSource;

    public bool ShowOutputBadge => IsSelected && !IsSource;

    public bool ShowReferenceBadge => IsReference;

    public bool ShowBluetoothBadge => IsBluetooth;

    public bool ShowDefaultBadge => IsDefaultDevice;

    public void ResetRuntimeStatus()
    {
        RuntimeStatus = "Idle";
        SoftwareStatusLine = string.Empty;
        IsActive = false;
    }
}
