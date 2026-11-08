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
    private int _delayMilliseconds;
    private string _runtimeStatus = "Idle";
    private string _softwareStatusLine = string.Empty;
    private bool _isActive;

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

    public int DelayMilliseconds
    {
        get => _delayMilliseconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 5_000);
            if (SetProperty(ref _delayMilliseconds, clamped))
            {
                OnPropertyChanged(nameof(DelayText));
            }
        }
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

    public void ResetRuntimeStatus()
    {
        RuntimeStatus = "Idle";
        SoftwareStatusLine = string.Empty;
        IsActive = false;
    }
}
