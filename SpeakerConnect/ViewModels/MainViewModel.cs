using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SpeakerConnect.Audio;
using SpeakerConnect.Infrastructure;
using SpeakerConnect.Models;

namespace SpeakerConnect.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AudioDeviceEnumerator _deviceEnumerator;
    private readonly AudioDeviceWatcher _deviceWatcher;
    private readonly AudioRoutingEngine _routingEngine;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<string, string> _channelErrors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceSettingsStore _deviceSettingsStore;
    private readonly VolumeBalanceStore _balanceStore;

    private DeviceViewModel? _sourceDevice;
    private bool _isRunning;
    private bool _isBusy;
    private bool _suppressSelectionHandling;
    private bool _suppressBalanceHandling;
    private bool _disposed;
    private const string SyncMethodDestination =
        "microphone chirp cross-correlation (routed path, relative to anchor)";

    private const string SyncMethodSource =
        "microphone chirp cross-correlation (source path, relative to anchor)";

    private string _statusMessage = "Ready.";
    private string _errorMessage = string.Empty;
    private BufferProfile _selectedBufferProfile = BufferProfile.Default;
    private string _syncDiagnosticsSummary = string.Empty;
    private string _syncStatus =
        "Start routing, then use Auto Sync for a microphone measurement or the manual test " +
        "to compare speakers by ear. Auto Sync measures relative arrival times on the live " +
        "routing path; manual delay remains available when acoustic measurement is not possible.";
    private string _syncDetail = string.Empty;
    private bool _isSyncBusy;
    private bool _isAutoSyncing;
    private DeviceViewModel? _bluetoothSourceHint;
    private DeviceViewModel? _referenceDevice;
    private bool _balanceEnabled;
    private bool _balanceDirty;
    private string _balanceStatus =
        "Position a microphone at your listening position and stop routing, then press " +
        "Calibrate Volume. Short pink-noise bursts are played through one speaker at a " +
        "time; measurements match relative loudness only, not calibrated absolute SPL.";
    private CancellationTokenSource? _calibrationCancellation;
    private CancellationTokenSource? _syncCancellation;
    private readonly UiPreferencesStore _uiPreferences;
    private AppPage _currentPage = AppPage.Dashboard;
    private ThemeMode _theme = ThemeMode.Dark;

    public MainViewModel()
        : this(new AudioDeviceEnumerator(), new DeviceSettingsStore(), new VolumeBalanceStore())
    {
    }

    internal MainViewModel(
        AudioDeviceEnumerator deviceEnumerator,
        DeviceSettingsStore deviceSettingsStore,
        VolumeBalanceStore balanceStore)
        : this(deviceEnumerator, deviceSettingsStore, balanceStore, new UiPreferencesStore())
    {
    }

    internal MainViewModel(
        AudioDeviceEnumerator deviceEnumerator,
        DeviceSettingsStore deviceSettingsStore,
        VolumeBalanceStore balanceStore,
        UiPreferencesStore uiPreferences)
    {
        _uiPreferences = uiPreferences ?? throw new ArgumentNullException(nameof(uiPreferences));
        _theme = _uiPreferences.Theme;
        ThemeManager.Apply(_theme);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _deviceEnumerator = deviceEnumerator ?? throw new ArgumentNullException(nameof(deviceEnumerator));
        _deviceSettingsStore = deviceSettingsStore ?? throw new ArgumentNullException(nameof(deviceSettingsStore));
        _balanceStore = balanceStore ?? throw new ArgumentNullException(nameof(balanceStore));
        _deviceWatcher = new AudioDeviceWatcher();
        _routingEngine = new AudioRoutingEngine(_deviceEnumerator, _deviceWatcher);

        _routingEngine.StateChanged += OnRoutingStateChanged;
        _routingEngine.FaultOccurred += OnRoutingFaultOccurred;
        _routingEngine.StabilityFallback += OnStabilityFallback;
        _routingEngine.MasterVolumeChanged += OnMasterVolumeChanged;
        _routingEngine.MasterMuteChanged += OnMasterMuteChanged;
        _deviceWatcher.DevicesChanged += OnDevicesChanged;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning && !IsBusy);
        RefreshCommand = new RelayCommand(RefreshDevices, () => IsConfigurationEnabled);
        UseBluetoothSourceCommand = new RelayCommand(
            UseBluetoothSource,
            () => IsConfigurationEnabled && BluetoothSourceHint is not null);
        AutoSyncCommand = new AsyncRelayCommand(AutoSyncAsync, CanRunAutoSync);
        ManualSyncCommand = new AsyncRelayCommand(ManualSyncAsync, CanRunManualSync);
        CancelSyncCommand = new RelayCommand(CancelSync, () => IsSyncBusy);
        ResetSyncCommand = new RelayCommand(ResetSync, CanResetSync);
        SaveSyncCommand = new RelayCommand(SaveSync, CanSaveSync);
        SetReferenceCommand = new RelayCommand<DeviceViewModel>(
            device => ReferenceDevice = device,
            device => device is not null &&
                      !device.IsSource &&
                      device.IsSelected &&
                      !IsBusy &&
                      !IsSyncBusy);
        CalibrateVolumeCommand = new AsyncRelayCommand(CalibrateVolumeAsync, CanCalibrateVolume);
        ResetBalanceCommand = new RelayCommand(ResetBalance, CanResetBalance);
        SaveBalanceCommand = new RelayCommand(SaveBalance, () => !IsBusy);

        // Restore saved Smart Volume Balance settings before the device list is
        // built, so reconnected endpoints recover their trims immediately.
        foreach (var pair in _balanceStore.GetSnapshot())
        {
            _routingEngine.SetBalanceGain(pair.Key, pair.Value);
        }

        _balanceEnabled = _balanceStore.Enabled;
        _routingEngine.BalanceEnabled = _balanceEnabled;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();

        RefreshDevices();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    /// <summary>
    /// Routed destinations offered as the manual sync reference. Auto Sync still aligns
    /// to the latest measured path; the reference is the speaker a listener compares
    /// the other routed copies against during the manual test.
    /// </summary>
    public ObservableCollection<DeviceViewModel> SyncReferenceCandidates { get; } = new();

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand UseBluetoothSourceCommand { get; }

    public AsyncRelayCommand AutoSyncCommand { get; }

    public AsyncRelayCommand ManualSyncCommand { get; }

    public RelayCommand CancelSyncCommand { get; }

    public RelayCommand ResetSyncCommand { get; }

    public RelayCommand SaveSyncCommand { get; }

    public RelayCommand<DeviceViewModel> SetReferenceCommand { get; }

    public AsyncRelayCommand CalibrateVolumeCommand { get; }

    public RelayCommand ResetBalanceCommand { get; }

    public RelayCommand SaveBalanceCommand { get; }

    // ---------------- Navigation ----------------

    /// <summary>Sidebar pages shown in the main window.</summary>
    public IReadOnlyList<AppPageItem> Pages { get; } = new[]
    {
        new AppPageItem(AppPage.Dashboard, "Dashboard", "\uE80F"),
        new AppPageItem(AppPage.Synchronization, "Synchronization", "\uE895"),
        new AppPageItem(AppPage.VolumeBalance, "Volume Balance", "\uE767"),
        new AppPageItem(AppPage.Settings, "Settings", "\uE713")
    };

    /// <summary>The currently visible page. Switching pages never touches routing.</summary>
    public AppPage CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    /// <summary>
    /// Compact endpoint usage readout for the dashboard header. Distinguishes
    /// configured (selected) endpoints from endpoints actually rendering audio.
    /// </summary>
    public string EndpointSummary
    {
        get
        {
            var count = (SourceDevice is null ? 0 : 1) + SelectedOutputCount;
            var state = IsRunning ? "playing" : "selected";
            return $"{count} of {MaxTotalAudibleDevices} endpoints {state}";
        }
    }

    // ---------------- Appearance ----------------

    public IReadOnlyList<ThemeMode> ThemeModes { get; } =
        (ThemeMode[])Enum.GetValues(typeof(ThemeMode));

    /// <summary>Selected application theme; applied immediately and persisted.</summary>
    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                ThemeManager.Apply(value);
                _uiPreferences.SaveTheme(value);
            }
        }
    }

    public float MasterVolume
    {
        get => (float)Math.Round(_routingEngine.MasterVolume * 100, 0);
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            _routingEngine.MasterVolume = clamped / 100f;
            OnPropertyChanged();
        }
    }

    public bool MasterMute
    {
        get => _routingEngine.MasterMute;
        set
        {
            _routingEngine.MasterMute = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Automatic application of the saved relative-loudness corrections. Disabling
    /// ramps every correction to unity but keeps the saved calibration.
    /// </summary>
    public bool BalanceEnabled
    {
        get => _balanceEnabled;
        set
        {
            if (_balanceEnabled == value)
            {
                return;
            }

            _balanceEnabled = value;
            _routingEngine.BalanceEnabled = value;
            _balanceStore.SetEnabled(value);

            OnPropertyChanged();
            OnPropertyChanged(nameof(BalanceSummary));
            IsBalanceDirty = false;

            BalanceStatus = value
                ? "Automatic balance enabled. Saved corrections are applied on top of each " +
                  "speaker's manual volume trim and master volume. Source attenuation is applied " +
                  "through its Windows endpoint volume while routing is active."
                : "Automatic balance disabled. Corrected gains are ramped back to unity; the saved " +
                  "calibration is kept for later. The Windows source slider has its full range again.";

            NotifyCommands();
        }
    }

    /// <summary>True when a manual trim differs from what was last saved.</summary>
    public bool IsBalanceDirty
    {
        get => _balanceDirty;
        private set => SetProperty(ref _balanceDirty, value);
    }

    public string BalanceStatus
    {
        get => _balanceStatus;
        private set => SetProperty(ref _balanceStatus, value);
    }

    public string BalanceSummary
    {
        get
        {
            var active = Devices.Where(device => device.IsSource || device.IsSelected).ToList();
            var attenuated = active.Count(device => device.BalanceGainPercent < 99.95);
            var state = _balanceEnabled ? "Automatic balance is on" : "Automatic balance is off";
            return $"{state}. {attenuated} of {active.Count} active speaker(s) have an attenuation " +
                   "trim. \"Source\" is corrected at its Windows endpoint volume; additional outputs " +
                   "use independent software gains.";
        }
    }

    /// <summary>
    /// This limitation is real and must remain visible: the Windows default endpoint is
    /// also the loopback source on most systems, so its endpoint volume is the only way
    /// to attenuate the source speaker independently of the copied destinations.
    /// </summary>
    public string BalanceSourceLimitNote =>
        "Source limitation: when the loopback source is also the Windows default output, " +
        "its calibrated attenuation becomes the maximum the Windows taskbar/keyboard volume " +
        "slider can reach while routing is active. Keyboard volume, the taskbar slider and mute " +
        "still change the whole group, but raising the slider above that calibrated ceiling is " +
        "re-capped to preserve the balance. Stop routing or disable automatic balance to restore " +
        "the full 0-100% Windows range. Additional outputs keep independent software trims.";

    public bool IsBalanceControlsEnabled => !IsBusy;

    public int MaxTotalAudibleDevices => AudioRoutingEngine.MaxTotalAudibleDevices;

    public int MaxAdditionalOutputs => AudioRoutingEngine.MaxAdditionalOutputs;

    // Retained as a compatibility alias; it now returns the four-destination cap.
    public int MaxOutputChannels => MaxAdditionalOutputs;

    public IReadOnlyList<BufferProfile> BufferProfiles => BufferProfile.All;

    public BufferProfile SelectedBufferProfile
    {
        get => _selectedBufferProfile;
        set
        {
            if (value is not null && SetProperty(ref _selectedBufferProfile, value))
            {
                OnPropertyChanged(nameof(BufferProfileSummary));
            }
        }
    }

    /// <summary>
    /// What the latency pipeline is currently configured/running with, and what the
    /// numbers mean. Acoustic latency (hardware/codec delay) is never included.
    /// </summary>
    public string BufferProfileSummary
    {
        get
        {
            var profile = _routingEngine.ActiveBufferProfile is BufferProfile active
                ? active
                : _selectedBufferProfile;
            var stateLabel = _routingEngine.ActiveBufferProfile is not null ? "Active" : "Selected";
            return $"{stateLabel} profile: {profile.DisplayName} — routing buffer {profile.BaseLatencyMs} ms + " +
                   $"WASAPI buffer {profile.WasapiLatencyMs} ms. {profile.Description} " +
                   "Bluetooth codec/hardware latency is NOT included in these numbers.";
        }
    }

    /// <summary>
    /// Measured software-side diagnostics for the running session (or empty when
    /// stopped). Deliberately distinguished from acoustic latency.
    /// </summary>
    public string SyncDiagnosticsSummary
    {
        get => _syncDiagnosticsSummary;
        private set => SetProperty(ref _syncDiagnosticsSummary, value);
    }

    public string SyncStatus
    {
        get => _syncStatus;
        private set => SetProperty(ref _syncStatus, value);
    }

    public string SyncDetail
    {
        get => _syncDetail;
        private set
        {
            if (SetProperty(ref _syncDetail, value))
            {
                OnPropertyChanged(nameof(HasSyncDetail));
            }
        }
    }

    public bool HasSyncDetail => !string.IsNullOrWhiteSpace(SyncDetail);

    /// <summary>True while Auto Sync or the manual live test owns the routing channels.</summary>
    public bool IsSyncBusy
    {
        get => _isSyncBusy;
        private set
        {
            if (SetProperty(ref _isSyncBusy, value))
            {
                OnPropertyChanged(nameof(IsSyncControlsEnabled));
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    /// <summary>
    /// True only during Auto Sync. Delay sliders stay editable during the manual test,
    /// which is the entire point of that workflow.
    /// </summary>
    public bool IsAutoSyncing
    {
        get => _isAutoSyncing;
        private set
        {
            if (SetProperty(ref _isAutoSyncing, value))
            {
                OnPropertyChanged(nameof(IsSyncControlsEnabled));
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    public bool IsSyncControlsEnabled => IsRunning && !IsBusy && !IsSyncBusy;

    /// <summary>
    /// This limitation is architectural, not a missing calibration step: the loopback
    /// source is rendered by Windows and is not part of the application's delay path.
    /// </summary>
    public string SyncSourceLimitNote =>
        "The original loopback source is rendered directly by Windows and cannot be delayed " +
        "or advanced by Speaker Connect. If it is earlier than the latest measured speaker, " +
        "adding delay to the copies cannot make them earlier; Auto Sync reports that deficit " +
        "instead of presenting it as a fix. Choose a source endpoint whose own path is at " +
        "least as slow as the slowest destination, or use a separate virtual audio endpoint " +
        "architecture in which the router owns the source clock. This app does not install " +
        "a virtual audio driver.";

    public DeviceViewModel? BluetoothSourceHint
    {
        get => _bluetoothSourceHint;
        private set
        {
            if (SetProperty(ref _bluetoothSourceHint, value))
            {
                OnPropertyChanged(nameof(BluetoothHintText));
            }
        }
    }

    public bool HasBluetoothHints => BluetoothSourceHint is not null;

    public string BluetoothHintText => BluetoothSourceHint is DeviceViewModel hint
        ? $"Bluetooth-style endpoint detected: {hint.DisplayName}"
        : "No Bluetooth-style endpoint detected (you can still select any source manually).";

    /// <summary>Destination selected as the sync reference; other speakers are calibrated against it.</summary>
    public DeviceViewModel? ReferenceDevice
    {
        get => _referenceDevice;
        set
        {
            if (SetProperty(ref _referenceDevice, value))
            {
                // Only destinations can be the reference; clear the reference flag on all
                // other devices and set it on the new one.
                foreach (var device in Devices)
                {
                    if (!device.IsSource && !ReferenceEquals(device, value))
                    {
                        device.IsReference = false;
                    }
                }

                if (value is not null)
                {
                    value.IsReference = true;
                }

                if (!_suppressSelectionHandling)
                {
                    _deviceSettingsStore.SaveReferenceDevice(value?.Id);
                }

                OnPropertyChanged(nameof(HasReferenceDevice));
                OnPropertyChanged(nameof(ReferenceSummary));
                NotifyCommands();
            }
        }
    }

    public bool HasReferenceDevice => ReferenceDevice is not null;

    public string ReferenceSummary => ReferenceDevice is null
        ? "No manual sync reference selected. Auto Sync uses the latest reliable measured path as the acoustic anchor."
        : $"Manual sync reference: {ReferenceDevice.DisplayName}. Compare the other routed copies against " +
          "this speaker by ear. Auto Sync still aligns to the latest reliable measured path; if this " +
          "reference is earlier than that path, it will also be delayed.";

    public DeviceViewModel? SourceDevice
    {
        get => _sourceDevice;
        set
        {
            if (!SetProperty(ref _sourceDevice, value))
            {
                return;
            }

            var previousSuppression = _suppressSelectionHandling;
            _suppressSelectionHandling = true;

            try
            {
                foreach (var device in Devices)
                {
                    device.IsSource = ReferenceEquals(device, value);
                    if (device.IsSource && device.IsSelected)
                    {
                        device.IsSelected = false;
                    }
                }
            }
            finally
            {
                _suppressSelectionHandling = previousSuppression;
            }

            if (!_suppressSelectionHandling)
            {
                _deviceSettingsStore.SaveSourceDevice(value?.Id);

                if (value is not null)
                {
                    // The source cannot also be a routed destination.
                    _deviceSettingsStore.SaveSelection(value.Id, false);
                }
            }

            // A reference can only be a destination, so it stops being the
            // reference when the same endpoint becomes the loopback source.
            if (value is not null && ReferenceEquals(value, _referenceDevice))
            {
                ReferenceDevice = null;
            }

            OnPropertyChanged(nameof(SourceSummary));
            UpdateRowAvailability();
            RaiseSelectionSummaryChanged();
            NotifyCommands();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsConfigurationEnabled));
                OnPropertyChanged(nameof(EndpointSummary));
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsConfigurationEnabled));
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    public bool IsConfigurationEnabled => !IsRunning && !IsBusy;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int SelectedOutputCount => Devices.Count(device => device.IsSelected && !device.IsSource);

    public string SelectionSummary
    {
        get
        {
            var count = SelectedOutputCount;
            return count == 0
                ? $"No additional outputs selected (maximum {MaxAdditionalOutputs}; " +
                  $"{MaxTotalAudibleDevices} speakers total including the source)."
                : $"{count} additional output(s) selected (maximum {MaxAdditionalOutputs}; " +
                  $"{MaxTotalAudibleDevices} speakers total including the source).";
        }
    }

    public string SourceSummary => SourceDevice is null
        ? "No loopback source selected."
        : $"Capturing system audio from: {SourceDevice.DisplayName}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;

        try
        {
            _calibrationCancellation?.Cancel();
            _calibrationCancellation?.Dispose();
        }
        catch (Exception)
        {
            // Calibration cancellation is best-effort during shutdown.
        }

        _calibrationCancellation = null;

        try
        {
            _syncCancellation?.Cancel();
            _syncCancellation?.Dispose();
        }
        catch (Exception)
        {
            // Synchronization cancellation is best-effort during shutdown.
        }

        _syncCancellation = null;

        _routingEngine.StateChanged -= OnRoutingStateChanged;
        _routingEngine.FaultOccurred -= OnRoutingFaultOccurred;
        _routingEngine.StabilityFallback -= OnStabilityFallback;
        _routingEngine.MasterVolumeChanged -= OnMasterVolumeChanged;
        _routingEngine.MasterMuteChanged -= OnMasterMuteChanged;
        _deviceWatcher.DevicesChanged -= OnDevicesChanged;

        foreach (var device in Devices)
        {
            device.PropertyChanged -= OnDevicePropertyChanged;
        }

        try
        {
            _routingEngine.Dispose();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Audio shutdown failed: {ex.Message}";
        }

        _deviceWatcher.Dispose();

        try
        {
            _deviceSettingsStore.Dispose();
        }
        catch (Exception)
        {
            // Settings flushing is best-effort during shutdown.
        }

        try
        {
            _uiPreferences.Dispose();
        }
        catch (Exception)
        {
            // Preferences flushing is best-effort during shutdown.
        }

        GC.SuppressFinalize(this);
    }

    private async Task StartAsync()
    {
        if (!CanStart())
        {
            return;
        }

        var source = SourceDevice;
        if (source is null)
        {
            return;
        }

        var targets = Devices
            .Where(device => device.IsSelected && !device.IsSource)
            .Select(device => new AudioOutputTarget(
                device.Id,
                (float)(device.Volume / 100.0),
                device.DelayMilliseconds))
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "Select at least one additional output device.";
            return;
        }

        ErrorMessage = string.Empty;
        _channelErrors.Clear();
        IsBusy = true;
        StatusMessage = "Starting audio routing...";

        try
        {
            var request = new AudioRoutingRequest(source.Id, targets, SelectedBufferProfile);
            var result = await Task.Run(() => _routingEngine.Start(request));

            // A channel or capture fault can be raised while Start is returning. The
            // engine state is authoritative; a queued UI event may not have been
            // dispatched yet, so do not mark the session running just because the
            // start call returned Started.
            var engineRunning =
                result.Started && _routingEngine.State == AudioRoutingState.Running;

            ApplyStatuses(result.Channels, engineRunning);

            if (engineRunning)
            {
                IsRunning = true;
                StatusMessage = result.Message;

                if (result.Errors.Count > 0)
                {
                    ErrorMessage = string.Join(Environment.NewLine, result.Errors);
                }
            }
            else
            {
                IsRunning = false;

                if (result.Started)
                {
                    StatusMessage = "Routing stopped during startup.";
                    ErrorMessage = _routingEngine.LastError ?? result.Message;
                }
                else
                {
                    StatusMessage = "Routing did not start.";
                    ErrorMessage = result.Message;
                }
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusMessage = "Routing failed.";
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            UpdateRowAvailability();
            NotifyCommands();
        }
    }

    private async Task StopAsync()
    {
        if (!IsRunning && !IsBusy)
        {
            return;
        }

        // Unblock a running Auto Sync or manual test before the engine disposes the
        // channels it is using.
        CancelSync();
        IsBusy = true;
        StatusMessage = "Stopping audio routing...";

        try
        {
            await Task.Run(() => _routingEngine.Stop());
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Stop failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            IsBusy = false;

            foreach (var device in Devices)
            {
                device.ResetRuntimeStatus();
            }

            _channelErrors.Clear();
            RefreshDevices();
            _deviceSettingsStore.Flush();
            StatusMessage = "Routing stopped. The source device continues its normal Windows playback.";
            NotifyCommands();
        }
    }

    private void RefreshDevices()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<AudioDeviceInfo> discoveredDevices;

        try
        {
            discoveredDevices = _deviceEnumerator.GetActiveRenderDevices();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not enumerate audio devices: {ex.Message}";
            StatusMessage = "Device enumeration failed.";
            return;
        }

        var previousById = Devices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        var previousSourceId = SourceDevice?.Id;
        var savedSettings = _deviceSettingsStore.GetSnapshot();
        var savedBalanceGains = _balanceStore.GetSnapshot();

        foreach (var device in Devices)
        {
            device.PropertyChanged -= OnDevicePropertyChanged;
        }

        Devices.Clear();

        var previousSuppression = _suppressSelectionHandling;
        _suppressSelectionHandling = true;

        try
        {
            foreach (var info in discoveredDevices)
            {
                var row = new DeviceViewModel(info);
                if (previousById.TryGetValue(info.Id, out var previous) && previous is not null)
                {
                    row.Volume = previous.Volume;
                    row.DelayMilliseconds = previous.DelayMilliseconds;
                    row.BalanceGainPercent = previous.BalanceGainPercent;
                }
                else
                {
                    // A reconnecting endpoint recovers the last saved values. The
                    // constructor defaults are used only when nothing was saved, so
                    // a disconnected device's file entry is never overwritten here.
                    if (savedSettings.TryGetValue(info.Id, out var saved))
                    {
                        if (saved.VolumePercent is double savedVolume)
                        {
                            row.Volume = savedVolume;
                        }

                        if (saved.DelayMilliseconds is int savedDelay)
                        {
                            row.DelayMilliseconds = savedDelay;
                        }
                    }

                    if (savedBalanceGains.TryGetValue(info.Id, out var savedBalance))
                    {
                        row.BalanceGainPercent = savedBalance * 100.0;
                        _routingEngine.SetBalanceGain(info.Id, savedBalance);
                    }
                }

                // Restore the last acoustic timing result, if any. The saved values
                // win because they are the durable copy written by Auto Sync; the
                // previous in-memory row is a fallback for an unflushed change.
                if (savedSettings.TryGetValue(info.Id, out var savedSync) &&
                    savedSync.MeasuredOffsetMilliseconds is double savedOffset)
                {
                    row.ApplySyncMeasurement(
                        savedOffset,
                        savedSync.MeasurementConfidencePercent,
                        savedSync.SyncMeasurementMethod,
                        savedSync.SyncMeasuredAtUtc);
                }
                else if (previous is not null &&
                         previous.MeasuredOffsetMilliseconds is double previousOffset)
                {
                    row.ApplySyncMeasurement(
                        previousOffset,
                        previous.MeasurementConfidencePercent,
                        previous.SyncMeasurementMethod,
                        previous.SyncMeasuredAtUtc);
                }

                row.PropertyChanged += OnDevicePropertyChanged;
                Devices.Add(row);
            }

            // The saved source wins over the previous in-memory fallback. A saved
            // id that is currently absent stays in the file untouched; the app uses
            // the Windows default until that endpoint reconnects.
            var desiredSourceId = _deviceSettingsStore.SourceDeviceId ?? previousSourceId;
            var newSource = desiredSourceId is null
                ? null
                : Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, desiredSourceId, StringComparison.OrdinalIgnoreCase));

            newSource ??= Devices.FirstOrDefault(device => device.Device.IsDefault);
            newSource ??= Devices.FirstOrDefault();

            SourceDevice = newSource;

            foreach (var device in Devices)
            {
                var wasSelected = previousById.TryGetValue(device.Id, out var previous)
                    ? previous.IsSelected
                    : savedSettings.TryGetValue(device.Id, out var saved) &&
                      saved.IsSelected == true;

                device.IsSelected =
                    !ReferenceEquals(device, newSource) &&
                    wasSelected;
            }

            foreach (var extra in Devices
                         .Where(device => device.IsSelected)
                         .Skip(MaxAdditionalOutputs)
                         .ToList())
            {
                extra.IsSelected = false;

                // Make the enforced cap durable so an over-cap group is not
                // restored again on every launch.
                _deviceSettingsStore.SaveSelection(extra.Id, false);
            }

            // Restore the sync reference across restarts and reconnects. When the
            // saved endpoint is absent, keep its id in the file for later.
            var desiredReferenceId =
                _referenceDevice?.Id ?? _deviceSettingsStore.ReferenceDeviceId;
            if (desiredReferenceId is not null)
            {
                var restoredReference = Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, desiredReferenceId, StringComparison.OrdinalIgnoreCase));

                ReferenceDevice = restoredReference is not null && !restoredReference.IsSource
                    ? restoredReference
                    : null;
            }
        }
        finally
        {
            _suppressSelectionHandling = previousSuppression;
        }

        RefreshBluetoothHints();
        RefreshSyncReferenceCandidates();

        UpdateRowAvailability();
        RaiseSelectionSummaryChanged();
        OnPropertyChanged(nameof(SourceSummary));
        NotifyCommands();

        if (Devices.Count == 0)
        {
            StatusMessage = "No active Windows output devices were found.";
        }
        else if (!IsRunning)
        {
            StatusMessage = "Ready. Choose a loopback source and up to four additional outputs (five speakers total).";
        }
    }

    private void UpdateRowAvailability()
    {
        var selectedCount = SelectedOutputCount;

        foreach (var device in Devices)
        {
            device.CanSelect = IsConfigurationEnabled &&
                               !device.IsSource &&
                               (device.IsSelected || selectedCount < MaxAdditionalOutputs);
            device.CanAdjustBalance = !IsBusy;
            device.CanAdjustDelay = !IsBusy &&
                                    !IsAutoSyncing &&
                                    device.IsSelected &&
                                    !device.IsSource;
        }

        OnPropertyChanged(nameof(BalanceSummary));
        OnPropertyChanged(nameof(IsBalanceControlsEnabled));
        OnPropertyChanged(nameof(IsSyncControlsEnabled));
    }

    private void OnSelectionChanged(DeviceViewModel device)
    {
        if (_suppressSelectionHandling)
        {
            return;
        }

        // Keep the manual reference list in sync with the routed group. Clearing the
        // active reference here is deliberately not persisted: a temporarily
        // unrouted device keeps its saved reference id for a later reconnect.
        RefreshSyncReferenceCandidates();

        if (device.IsSelected && device.IsSource)
        {
            _suppressSelectionHandling = true;
            device.IsSelected = false;
            _suppressSelectionHandling = false;

            _deviceSettingsStore.SaveSelection(device.Id, false);

            StatusMessage = "The loopback source cannot also be a destination.";
            UpdateRowAvailability();
            RaiseSelectionSummaryChanged();
            NotifyCommands();
            return;
        }

        if (SelectedOutputCount > MaxAdditionalOutputs)
        {
            _suppressSelectionHandling = true;
            device.IsSelected = false;
            _suppressSelectionHandling = false;

            _deviceSettingsStore.SaveSelection(device.Id, false);

            StatusMessage = $"You can route to at most {MaxAdditionalOutputs} additional output devices " +
                            $"({MaxTotalAudibleDevices} speakers total, including the source).";
            UpdateRowAvailability();
            RaiseSelectionSummaryChanged();
            NotifyCommands();
            return;
        }

        // Auto-save the group membership so the same outputs are restored after a
        // restart or when a temporarily disconnected endpoint comes back.
        _deviceSettingsStore.SaveSelection(device.Id, device.IsSelected);

        UpdateRowAvailability();
        RaiseSelectionSummaryChanged();
        NotifyCommands();
    }

    /// <summary>
    /// Rebuilds the candidate list used by the synchronization section. The saved
    /// reference id is intentionally kept when the active reference is temporarily
    /// unrouted, so a reconnect can restore it.
    /// </summary>
    private void RefreshSyncReferenceCandidates()
    {
        SyncReferenceCandidates.Clear();
        foreach (var device in Devices.Where(device => device.IsSelected && !device.IsSource))
        {
            SyncReferenceCandidates.Add(device);
        }

        if (_referenceDevice is not null && !SyncReferenceCandidates.Contains(_referenceDevice))
        {
            var previousSuppression = _suppressSelectionHandling;
            _suppressSelectionHandling = true;
            try
            {
                ReferenceDevice = null;
            }
            finally
            {
                _suppressSelectionHandling = previousSuppression;
            }
        }

        OnPropertyChanged(nameof(ReferenceSummary));
        NotifyCommands();
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DeviceViewModel device || _suppressSelectionHandling)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(DeviceViewModel.IsSelected):
                OnSelectionChanged(device);
                break;

            case nameof(DeviceViewModel.Volume):
                if (IsRunning)
                {
                    _routingEngine.UpdateVolume(device.Id, (float)(device.Volume / 100.0));
                }

                // Volume is persisted even for a device that is currently
                // disconnected later in the session; its entry stays in the file.
                _deviceSettingsStore.SaveVolume(device.Id, device.Volume);
                break;

            case nameof(DeviceViewModel.DelayMilliseconds):
                if (IsRunning)
                {
                    _routingEngine.UpdateDelay(device.Id, device.DelayMilliseconds);
                }

                _deviceSettingsStore.SaveDelay(device.Id, device.DelayMilliseconds);
                break;

            case nameof(DeviceViewModel.BalanceGainPercent):
                if (!_suppressBalanceHandling)
                {
                    OnBalanceGainChanged(device);
                }
                break;
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            ApplyStatuses(_routingEngine.GetChannelStatuses(), running: true);

            var summary = _routingEngine.GetDiagnosticsSummary();
            SyncDiagnosticsSummary =
                $"Software buffers — jitter depth {summary.MaxBufferedMilliseconds:0} ms, " +
                $"underruns {summary.TotalUnderruns}, dropped frames {summary.TotalDroppedFrames}, " +
                $"est. routing latency {summary.MaxEstimatedRoutingLatencyMilliseconds:0} ms. " +
                "These are software measurements only; acoustic latency (endpoint hardware + " +
                "Bluetooth codec delay) cannot be measured by the app.";
            OnPropertyChanged(nameof(BufferProfileSummary));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read routing status: {ex.Message}";
        }
    }

    private void ApplyStatuses(IReadOnlyList<AudioChannelStatus> statuses, bool running)
    {
        var byDeviceId = statuses.ToDictionary(
            status => status.DeviceId,
            StringComparer.OrdinalIgnoreCase);

        foreach (var device in Devices)
        {
            if (device.IsSource)
            {
                device.IsActive = running;
                device.RuntimeStatus = running
                    ? "Capturing loopback; original playback continues"
                    : "Idle";
                continue;
            }

            if (byDeviceId.TryGetValue(device.Id, out var status))
            {
                device.IsActive = running && status.State == AudioChannelState.Playing;
                device.RuntimeStatus = running ? FormatStatus(status) : "Stopped";
                device.SoftwareStatusLine = running
                    ? $"Software: profile {status.ProfileName} | jitter target {status.BaseLatencyMilliseconds} ms | " +
                      $"WASAPI buffer {status.WasapiLatencyMilliseconds} ms | drift corrections tracked separately"
                    : string.Empty;
                continue;
            }

            device.IsActive = false;
            device.SoftwareStatusLine = string.Empty;

            if (!device.IsSelected)
            {
                device.RuntimeStatus = "Idle";
            }
            else if (_channelErrors.TryGetValue(device.Id, out var error))
            {
                device.RuntimeStatus = $"Stopped: {error}";
            }
            else
            {
                device.RuntimeStatus = running ? "Not active" : "Idle";
            }
        }
    }

    private static string FormatStatus(AudioChannelStatus status)
    {
        var state = status.State switch
        {
            AudioChannelState.Playing => "Playing",
            AudioChannelState.Faulted => "Faulted",
            _ => "Stopped"
        };

        var summary =
            $"{state} | buffer {status.BufferedMilliseconds:0} ms | " +
            $"underruns {status.UnderrunCount} | drops {status.DroppedFrameCount} | " +
            $"est. routing {status.EstimatedRoutingLatencyMilliseconds:0} ms (software) | " +
            $"delay {status.DelayMilliseconds} ms";

        return status.Error is null ? summary : $"{summary} | {status.Error}";
    }

    private void OnMasterVolumeChanged(object? sender, float volume)
    {
        PostToUi(() =>
        {
            if (!_disposed)
            {
                OnPropertyChanged(nameof(MasterVolume));
            }
        });
    }

    private void OnMasterMuteChanged(object? sender, bool muted)
    {
        PostToUi(() =>
        {
            if (!_disposed)
            {
                OnPropertyChanged(nameof(MasterMute));
            }
        });
    }

    private void OnRoutingStateChanged(object? sender, RoutingStateChangedEventArgs e)
    {        PostToUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            // Read the engine's current state instead of trusting a queued event:
            // a fast Stop can make an earlier "Running" notification stale.
            var currentState = _routingEngine.State;

            if (currentState == AudioRoutingState.Running)
            {
                if (!IsBusy)
                {
                    IsRunning = true;
                }
            }
            else if (currentState is AudioRoutingState.Stopped or AudioRoutingState.Faulted)
            {
                if (!IsBusy)
                {
                    IsRunning = false;
                }

                ApplyStatuses(Array.Empty<AudioChannelStatus>(), running: false);

                if (currentState == AudioRoutingState.Faulted &&
                    string.IsNullOrWhiteSpace(ErrorMessage) &&
                    !string.IsNullOrWhiteSpace(_routingEngine.LastError))
                {
                    ErrorMessage = _routingEngine.LastError!;
                }

                UpdateRowAvailability();
                NotifyCommands();
            }

            OnPropertyChanged(nameof(BufferProfileSummary));
        });
    }

    private void OnRoutingFaultOccurred(object? sender, AudioRouteFaultEventArgs e)
    {
        PostToUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            ErrorMessage = e.Message;

            if (e.DeviceId is not null)
            {
                _channelErrors[e.DeviceId] = e.Message;

                var row = Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, e.DeviceId, StringComparison.OrdinalIgnoreCase));

                if (row is not null)
                {
                    row.IsActive = false;
                    row.RuntimeStatus = $"Stopped: {e.Message}";
                }
            }

            if (e.IsFatal)
            {
                IsRunning = false;
                StatusMessage = "Routing stopped because of an audio device error.";
                UpdateRowAvailability();
                NotifyCommands();
            }
            else
            {
                StatusMessage = "An output device stopped; the remaining outputs continue.";
            }
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        PostToUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (IsRunning || IsBusy)
            {
                StatusMessage = IsRunning
                    ? "A Windows audio device changed. Stop routing to refresh the device list."
                    : StatusMessage;
                return;
            }

            RefreshDevices();
        });
    }

    private bool CanStart()
    {
        return IsConfigurationEnabled &&
               SourceDevice is not null &&
               Devices.Any(device => device.IsSelected && !device.IsSource);
    }

    private void RaiseSelectionSummaryChanged()
    {
        OnPropertyChanged(nameof(SelectedOutputCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(EndpointSummary));
        OnPropertyChanged(nameof(BalanceSummary));
    }

    private static void NotifyCommands()
    {
        RelayCommand.NotifyCanExecuteChanged();
    }

    private void UseBluetoothSource()
    {
        var hint = BluetoothSourceHint;
        if (hint is null)
        {
            return;
        }

        SourceDevice = hint;
        StatusMessage =
            $"Bluetooth-style source selected: {hint.DisplayName}. Route to the wired device and use " +
            "Auto Sync (or the manual burst test) to compensate the added Bluetooth path latency.";
    }

    private bool CanCalibrateVolume()
    {
        return IsConfigurationEnabled &&
               SourceDevice is not null &&
               Devices.Any(device => device.IsSelected && !device.IsSource);
    }

    private async Task CalibrateVolumeAsync()
    {
        if (!CanCalibrateVolume() || SourceDevice is null)
        {
            return;
        }

        var speakers = new List<BalanceSpeaker>
        {
            new(SourceDevice.Id, SourceDevice.DisplayName, IsSource: true)
        };

        speakers.AddRange(Devices
            .Where(device => device.IsSelected && !device.IsSource)
            .Select(device => new BalanceSpeaker(device.Id, device.DisplayName, IsSource: false)));

        if (speakers.Count == 0)
        {
            BalanceStatus = "Select a source and at least one additional output before calibrating.";
            return;
        }

        var warning =
            "Smart Volume Balance will play about 1.5 seconds of band-limited pink noise through " +
            "each selected speaker, one at a time, at its current Windows volume.\n\n" +
            "Before continuing:\n" +
            "  - Place the microphone at your normal listening position.\n" +
            "  - Keep the room quiet; background noise can make a measurement unreliable.\n" +
            "  - Make sure no routing session is running (the button is disabled while routing).\n" +
            "  - Start at a moderate volume; unexpected loud output is possible.\n\n" +
            "The microphone is not calibrated for absolute SPL. This measures relative loudness " +
            "only and saves attenuation corrections by device id. Continue?";

        var answer = MessageBox.Show(
            warning,
            "Smart Volume Balance calibration",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            BalanceStatus = "Calibration cancelled before any test signal was played.";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = "Smart Volume Balance calibration is running...";
        BalanceStatus = "Preparing the microphone and test signal...";
        IsBusy = true;
        _balanceDirty = false;

        var cancellation = new CancellationTokenSource();
        _calibrationCancellation = cancellation;

        try
        {
            var result = await Task.Run(() => VolumeBalanceCalibrator.Calibrate(
                speakers,
                message => PostToUi(() =>
                {
                    if (!_disposed)
                    {
                        BalanceStatus = message;
                    }
                }),
                cancellation.Token));

            if (_disposed)
            {
                return;
            }

            if (!result.Success)
            {
                BalanceStatus = result.Message;
                StatusMessage = "Smart Volume Balance calibration did not complete.";
                return;
            }

            ApplyCalibrationResult(result);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                BalanceStatus = "Calibration was cancelled.";
                StatusMessage = "Smart Volume Balance calibration cancelled.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                BalanceStatus = $"Calibration failed: {ex.Message}";
                StatusMessage = "Smart Volume Balance calibration failed.";
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_calibrationCancellation, cancellation))
            {
                _calibrationCancellation = null;
            }

            cancellation.Dispose();

            if (!_disposed)
            {
                IsBusy = false;
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    private void ApplyCalibrationResult(VolumeBalanceCalibrationResult result)
    {
        _suppressBalanceHandling = true;
        try
        {
            foreach (var measurement in result.Measurements)
            {
                _routingEngine.SetBalanceGain(measurement.DeviceId, measurement.Gain);
                _balanceStore.SaveGain(measurement.DeviceId, measurement.Gain);

                var row = Devices.FirstOrDefault(device =>
                    string.Equals(device.Id, measurement.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.BalanceGainPercent = measurement.Gain * 100.0;
                }
            }
        }
        finally
        {
            _suppressBalanceHandling = false;
        }

        if (!_balanceEnabled)
        {
            // Applying the measured corrections is the point of calibration; the user
            // can still disable automatic balance afterwards, which keeps the saved data.
            BalanceEnabled = true;
        }
        else
        {
            _routingEngine.BalanceEnabled = true;
        }

        _balanceDirty = false;
        BalanceStatus =
            result.Message + " Corrections were applied automatically and saved by endpoint id. " +
            "Move around the listening area and compare the speakers; use the per-speaker sliders " +
            "for small manual adjustments, then Save Calibration to keep them. Disable automatic " +
            "balance at any time without deleting the measurements.";
        StatusMessage = "Smart Volume Balance calibration applied.";
        OnPropertyChanged(nameof(BalanceSummary));
        OnPropertyChanged(nameof(IsBalanceDirty));
        NotifyCommands();
    }

    private void OnBalanceGainChanged(DeviceViewModel device)
    {
        var gain = (float)(device.BalanceGainPercent / 100.0);
        _routingEngine.SetBalanceGain(device.Id, gain);
        _balanceDirty = true;
        OnPropertyChanged(nameof(IsBalanceDirty));

        BalanceStatus = _balanceEnabled
            ? $"{device.DisplayName}: balance trim {device.BalanceGainText}, applied live. " +
              "Press Save Calibration to keep manual adjustments for the next session."
            : $"{device.DisplayName}: balance trim {device.BalanceGainText} saved for when " +
              "automatic balance is enabled. Press Save Calibration to keep it.";

        OnPropertyChanged(nameof(BalanceSummary));
        NotifyCommands();
    }

    private bool CanResetBalance()
    {
        return !IsBusy &&
               (_routingEngine.GetBalanceGains().Count > 0 ||
                Devices.Any(device => device.BalanceGainPercent < 99.95));
    }

    private void ResetBalance()
    {
        var answer = MessageBox.Show(
            "Clear every saved loudness correction and return all speakers to unity gain? " +
            "Automatic balance will stay enabled, but no speaker will be attenuated. " +
            "This cannot be undone.",
            "Reset Smart Volume Balance",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _suppressBalanceHandling = true;
        try
        {
            foreach (var device in Devices)
            {
                device.BalanceGainPercent = 100;
            }
        }
        finally
        {
            _suppressBalanceHandling = false;
        }

        _routingEngine.ClearBalanceGains();
        _balanceStore.ResetGains();
        _balanceDirty = false;
        BalanceStatus =
            "Calibration reset. Every speaker is back to unity balance gain; the automatic " +
            "application setting was kept.";
        OnPropertyChanged(nameof(IsBalanceDirty));
        OnPropertyChanged(nameof(BalanceSummary));
        NotifyCommands();
    }

    private void SaveBalance()
    {
        var gains = new Dictionary<string, float>(
            _balanceStore.GetSnapshot(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var device in Devices)
        {
            gains[device.Id] = (float)(device.BalanceGainPercent / 100.0);
        }

        _balanceStore.SaveGains(gains);
        _balanceStore.SetEnabled(_balanceEnabled);
        _balanceDirty = false;
        BalanceStatus =
            $"Saved calibration for {Devices.Count} connected endpoint(s). " +
            "Corrections are stored by persistent Windows endpoint id and are restored after " +
            "a restart or reconnect. Disconnected devices' saved gains were preserved.";
        OnPropertyChanged(nameof(IsBalanceDirty));
        OnPropertyChanged(nameof(BalanceSummary));
        NotifyCommands();
    }

    private bool CanRunAutoSync()
    {
        return IsRunning &&
               !IsBusy &&
               !IsSyncBusy &&
               SelectedOutputCount > 0;
    }

    private bool CanRunManualSync() => CanRunAutoSync();

    /// <summary>
    /// Measures every routed destination and the loopback source on the live routing
    /// path, then applies the resulting delays. The engine refuses to run unless routing
    /// is active, because measuring a synthetic path would not represent the real one.
    /// </summary>
    private async Task AutoSyncAsync()
    {
        if (!CanRunAutoSync())
        {
            SyncStatus = "Auto Sync needs running routing and at least one routed destination.";
            return;
        }

        var targets = Devices
            .Where(device => device.IsSelected && !device.IsSource)
            .Select(device => new AudioOutputTarget(
                device.Id,
                Math.Max((float)(device.Volume / 100.0), 0.10f),
                device.DelayMilliseconds))
            .ToList();

        if (targets.Count == 0)
        {
            SyncStatus = "Check at least one additional output before running Auto Sync.";
            return;
        }

        var referenceId = ReferenceDevice?.Id;
        var cancellation = new CancellationTokenSource();
        _syncCancellation = cancellation;
        ErrorMessage = string.Empty;
        SyncDetail = string.Empty;
        SyncStatus =
            "Auto Sync is measuring the source and every routed destination with short chirps. " +
            "Keep the room quiet and leave the speakers and volume controls alone until it finishes.";
        IsAutoSyncing = true;
        IsSyncBusy = true;

        try
        {
            var result = await Task.Run(() => _routingEngine.RunAutoSync(
                targets,
                referenceId,
                cancellation.Token));

            if (_disposed)
            {
                return;
            }

            if (cancellation.IsCancellationRequested ||
                _routingEngine.State != AudioRoutingState.Running)
            {
                SyncStatus = "Auto Sync was cancelled or routing stopped. No delays were changed.";
                return;
            }

            ApplyAutoSyncResult(result);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                SyncStatus = "Auto Sync was cancelled. No delays were changed.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                SyncStatus = $"Auto Sync failed: {ex.Message}";
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_syncCancellation, cancellation))
            {
                _syncCancellation = null;
            }

            cancellation.Dispose();

            if (!_disposed)
            {
                IsAutoSyncing = false;
                IsSyncBusy = false;
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    private void ApplyAutoSyncResult(SyncCalibrationRunResult result)
    {
        if (result.Plan is null || !result.Plan.Success)
        {
            SyncStatus = result.Message;
            SyncDetail = BuildSyncDetail(result.Plan, result.Measurements, result.MeasuredAtUtc, applied: false);
            MarkUnreliableMeasurements(result.Measurements);
            StatusMessage = "Auto Sync did not change any delays.";
            return;
        }

        var plan = result.Plan;

        // Apply the plan before saving metadata. Setting the row value updates the live
        // engine channel (when routing is running) and the durable delay entry.
        foreach (var recommendation in plan.Recommendations.Where(item => !item.IsSource))
        {
            var row = FindDevice(recommendation.DeviceId);
            if (row is not null && row.DelayMilliseconds != recommendation.SuggestedDelayMilliseconds)
            {
                row.DelayMilliseconds = recommendation.SuggestedDelayMilliseconds;
            }
        }

        foreach (var measurement in result.Measurements)
        {
            var row = FindDevice(measurement.DeviceId);
            if (row is null)
            {
                continue;
            }

            if (!measurement.IsReliable)
            {
                row.SetSyncMeasurementFailure(
                    measurement.FailureReason ?? "The measurement was not reliable.");
                continue;
            }

            var inherent = measurement.ArrivalOffsetMilliseconds -
                           (measurement.IsSource ? 0 : measurement.ConfiguredDelayMilliseconds);

            // Only differences between speakers are physically meaningful. The raw
            // correlation offset shares an arbitrary microphone/ring recording origin,
            // so store the measured path relative to the acoustic anchor instead of
            // presenting that origin as an absolute hardware latency.
            var relativeToAnchor = inherent - plan.AnchorInherentLatencyMilliseconds;
            var method = measurement.IsSource ? SyncMethodSource : SyncMethodDestination;

            row.ApplySyncMeasurement(
                relativeToAnchor,
                measurement.ConfidencePercent,
                method,
                result.MeasuredAtUtc);

            // Auto Sync persists successful results immediately so reconnect and
            // restart restore the measured metadata together with the delays.
            _deviceSettingsStore.SaveSyncMeasurement(
                measurement.DeviceId,
                relativeToAnchor,
                measurement.ConfidencePercent,
                method,
                result.MeasuredAtUtc);
        }

        _deviceSettingsStore.Flush();

        SyncStatus = plan.Message;
        SyncDetail = BuildSyncDetail(plan, result.Measurements, result.MeasuredAtUtc, applied: true);
        StatusMessage = plan.SourceIsEarlier
            ? "Auto Sync applied delays, but the source is earlier than the latest measured path."
            : "Auto Sync applied and saved the measured delays.";
        OnPropertyChanged(nameof(ReferenceSummary));
        NotifyCommands();
    }

    private void MarkUnreliableMeasurements(IReadOnlyList<SyncSpeakerMeasurement> measurements)
    {
        foreach (var measurement in measurements.Where(item => !item.IsReliable))
        {
            var row = FindDevice(measurement.DeviceId);
            row?.SetSyncMeasurementFailure(
                measurement.FailureReason ?? "The measurement was not reliable.");
        }
    }

    private string BuildSyncDetail(
        SyncCalibrationPlan? plan,
        IReadOnlyList<SyncSpeakerMeasurement> measurements,
        DateTimeOffset measuredAtUtc,
        bool applied)
    {
        var lines = new List<string>();

        if (plan is not null)
        {
            if (!string.IsNullOrWhiteSpace(plan.AnchorNote))
            {
                lines.Add(plan.AnchorNote);
            }

            if (plan.AcousticAnchorDisplayName is not null)
            {
                lines.Add(
                    $"Acoustic anchor: {plan.AcousticAnchorDisplayName} (0 ms reference). " +
                    $"Overall confidence {plan.OverallConfidencePercent:0}%; maximum rounding " +
                    $"residual {plan.MaxResidualMilliseconds:0.0} ms. Absolute hardware latency " +
                    "is not known; only the measured differences between speakers are used.");
            }

            if (plan.SourceIsEarlier)
            {
                lines.Add(
                    $"Source deficit: the source arrived {plan.SourceDeficitMilliseconds:0} ms earlier " +
                    "than the latest measured path. Adding delay to the already-late routed copies " +
                    "cannot make them earlier. For exact alignment with this source, choose a source " +
                    "endpoint whose own path is at least as slow as the slowest destination (for " +
                    "example a Bluetooth endpoint), or use a virtual audio endpoint architecture in " +
                    "which the router owns the source clock. This app does not install a virtual " +
                    "audio driver.");
            }
        }

        foreach (var measurement in measurements)
        {
            var role = measurement.IsSource ? "source" : "routed copy";
            if (!measurement.IsReliable)
            {
                lines.Add(
                    $"{measurement.DisplayName} ({role}): unreliable — " +
                    (measurement.FailureReason ?? "the correlation confidence was too low."));
                continue;
            }

            var inherent = measurement.ArrivalOffsetMilliseconds -
                           (measurement.IsSource ? 0 : measurement.ConfiguredDelayMilliseconds);
            double? relativeToAnchor = null;
            if (plan?.AcousticAnchorDisplayName is not null)
            {
                relativeToAnchor = inherent - plan.AnchorInherentLatencyMilliseconds;
            }

            var timing = relativeToAnchor switch
            {
                null => $"measured arrival offset {inherent:0} ms (recording origin; no alignment applied)",
                double relative when Math.Abs(relative) < 0.5 => "measured acoustic anchor (0 ms reference)",
                double relative when relative < 0 => $"measured {-relative:0} ms earlier than the anchor",
                double relative => $"measured {relative:0} ms later than the anchor"
            };

            lines.Add(
                $"{measurement.DisplayName} ({role}): {timing}, " +
                $"confidence {measurement.ConfidencePercent:0}%, configured delay " +
                $"{measurement.ConfiguredDelayMilliseconds} ms.");
        }

        lines.Add(applied
            ? "The suggested delays were applied and saved by persistent endpoint id."
            : "No delays were changed.");
        lines.Add($"Measured at {measuredAtUtc.LocalDateTime:g}.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Plays a burst train natively through the loopback source while routing is live.
    /// Every destination receives it through the real loopback/delay path; the user
    /// adjusts delay sliders by ear. This is deliberately not described as a
    /// measurement.
    /// </summary>
    private async Task ManualSyncAsync()
    {
        if (!CanRunManualSync())
        {
            SyncStatus = "The manual test needs running routing and at least one routed destination.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _syncCancellation = cancellation;
        ErrorMessage = string.Empty;
        SyncDetail = string.Empty;

        var referenceText = ReferenceDevice is null
            ? "no manual reference selected"
            : $"manual reference {ReferenceDevice.DisplayName}";
        SyncStatus =
            $"Manual sync test is playing a burst train through the loopback source ({referenceText}). " +
            "Adjust the Delay sliders while it plays until the copies sound like one speaker. " +
            "Delay values are saved automatically; the manual test is not an acoustic measurement.";
        IsSyncBusy = true;

        try
        {
            await Task.Run(() => _routingEngine.PlayManualSyncTest(cancellation.Token));

            if (!_disposed)
            {
                SyncStatus =
                    "Manual sync test finished. Repeat it after any delay change and compare the " +
                    "speakers from the listening position. The configured delays are manual values, " +
                    "not measured acoustic latencies.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                SyncStatus = "Manual sync test cancelled. Configured delays were kept.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                SyncStatus = $"Manual sync test failed: {ex.Message}";
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_syncCancellation, cancellation))
            {
                _syncCancellation = null;
            }

            cancellation.Dispose();

            if (!_disposed)
            {
                IsSyncBusy = false;
                UpdateRowAvailability();
                NotifyCommands();
            }
        }
    }

    private void CancelSync()
    {
        var cancellation = _syncCancellation;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
            SyncStatus = "Cancelling the synchronization test...";
        }
        catch (ObjectDisposedException)
        {
            // The test finished between the read and the cancel.
        }
    }

    private bool CanResetSync()
    {
        return !IsBusy &&
               !IsSyncBusy &&
               Devices.Any(device =>
                   device.DelayMilliseconds > 0 ||
                   device.MeasuredOffsetMilliseconds is not null ||
                   !string.IsNullOrWhiteSpace(device.SyncMeasurementMethod) ||
                   device.HasSyncMeasurement);
    }

    private void ResetSync()
    {
        var answer = MessageBox.Show(
            "Clear every configured delay and all saved acoustic timing results? " +
            "The manual sync reference selection is kept. This cannot be undone.",
            "Reset synchronization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var previousSuppression = _suppressSelectionHandling;
        _suppressSelectionHandling = true;

        try
        {
            foreach (var device in Devices)
            {
                if (device.DelayMilliseconds != 0)
                {
                    device.DelayMilliseconds = 0;
                }

                device.ClearSyncMeasurement();

                // The suppression above bypasses OnDevicePropertyChanged, so apply the
                // zero delay to a live session explicitly.
                if (IsRunning)
                {
                    _routingEngine.UpdateDelay(device.Id, 0);
                }

                _deviceSettingsStore.RemoveDelayAndSync(device.Id);
            }
        }
        finally
        {
            _suppressSelectionHandling = previousSuppression;
        }

        _deviceSettingsStore.Flush();
        SyncDetail = string.Empty;
        SyncStatus =
            "Synchronization reset: all delays and acoustic timing results were cleared. " +
            "The manual reference selection was kept. Start routing again to re-measure.";
        StatusMessage = "Synchronization delays and measurements reset.";
        UpdateRowAvailability();
        NotifyCommands();
    }

    private bool CanSaveSync()
    {
        return !IsBusy &&
               !IsSyncBusy &&
               Devices.Count > 0;
    }

    /// <summary>
    /// Persists the current manual delays plus whatever acoustic metadata is currently
    /// displayed. Manual delays remain manual: they are never relabeled as measured.
    /// </summary>
    private void SaveSync()
    {
        foreach (var device in Devices)
        {
            _deviceSettingsStore.SaveDelay(device.Id, device.DelayMilliseconds);

            if (device.MeasuredOffsetMilliseconds is double measured &&
                device.MeasurementConfidencePercent is double confidence)
            {
                var method = device.SyncMeasurementMethod ??
                             (device.IsSource ? SyncMethodSource : SyncMethodDestination);
                _deviceSettingsStore.SaveSyncMeasurement(
                    device.Id,
                    measured,
                    confidence,
                    method,
                    device.SyncMeasuredAtUtc ?? DateTimeOffset.UtcNow);
            }
        }

        _deviceSettingsStore.Flush();
        SyncStatus =
            $"Saved synchronization settings for {Devices.Count} connected endpoint(s). " +
            "Delays and any acoustic measurement metadata are stored by persistent Windows " +
            "endpoint id and restored after a restart or reconnect. Manual delays are not " +
            "described as acoustic measurements.";
        StatusMessage = "Synchronization calibration saved by endpoint id.";
        NotifyCommands();
    }

    private DeviceViewModel? FindDevice(string deviceId)
    {
        return Devices.FirstOrDefault(device =>
            string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private void OnStabilityFallback(object? sender, StabilityFallbackEventArgs e)
    {
        PostToUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            StatusMessage =
                $"\"{e.DeviceName}\" could not keep up at the {e.FromProfile.DisplayName} buffer profile " +
                $"({e.Underruns} underruns, {e.DroppedFrames} dropped frames in the last few seconds); " +
                $"it was rebuilt with the {e.ToProfile.DisplayName} profile.";
            OnPropertyChanged(nameof(BufferProfileSummary));
        });
    }

    private void RefreshBluetoothHints()
    {
        var hint = Devices.FirstOrDefault(device =>
            !device.IsSource &&
            BluetoothEndpointHints.LooksLikeBluetooth(device.FriendlyName));

        BluetoothSourceHint = hint;
        OnPropertyChanged(nameof(HasBluetoothHints));
    }

    private void PostToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
    }
}
