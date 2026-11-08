using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;

namespace SpeakerConnect.Audio;

/// <summary>
/// Single logical master-volume state for the whole routing group.
///
/// It listens to Windows volume notifications on the default render endpoint
/// (keyboard media keys, taskbar slider, mute) through NAudio's
/// <see cref="AudioEndpointVolume.OnVolumeNotification"/> event, applies the new
/// level to every destination as a short ramped gain, and keeps the source
/// endpoint in sync when the source is not the default endpoint.
///
/// A unique <see cref="AudioEndpointVolume.NotificationGuid"/> is used for writes
/// made by this app, so the callbacks caused by our own writes are ignored. This
/// prevents recursive notification loops. All endpoint writes are best-effort:
/// a driver that rejects a volume write leaves the in-app gain path working.
///
/// WASAPI loopback capture normally copies the render stream *before* the endpoint
/// volume stage. That is why the destination gain is always applied: the loopback
/// source speaker is attenuated by Windows itself, while the copied stream feeding
/// the other speakers is not. The diagnostics signal mode includes a probe that
/// measures this on the real hardware and reports whether the machine behaves
/// pre-volume (expected) or post-volume (would double-attenuate).
///
/// Threading: every COM object (<see cref="MMDevice"/>,
/// <see cref="AudioEndpointVolume"/>, <see cref="MMDeviceEnumerator"/>) is created,
/// used, and disposed on one dedicated MTA thread. Windows Core Audio COM objects
/// are apartment-affine; using them from another thread can fail with
/// <c>E_NOINTERFACE</c> when the RCW is marshalled (this was the cause of an
/// application crash when the fallback poll used the UI-thread device). MTA is used
/// rather than STA because volume notifications are delivered through a COM
/// callback: an STA would need a message pump to receive them, while MTA callbacks
/// arrive on RPC threads. The notification callback only touches plain data and
/// posts work back to the COM thread.
/// </summary>
public sealed class MasterVolumeManager : IDisposable
{
    private const int DefaultRampMilliseconds = 20;
    private const int FallbackPollMilliseconds = 1_000;
    private const long SuppressWindowMilliseconds = 300;
    private const int WorkerJoinMilliseconds = 2_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, float> _deviceVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _balanceGains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BalanceGainRamp> _balanceRamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<float>> _subscribers = new();
    private readonly BlockingCollection<Action> _work = new();
    private readonly Guid _notificationContext = Guid.NewGuid();
    private readonly bool _manageSystemVolume;
    private readonly int _rampMilliseconds;
    private readonly Thread? _worker;
    private readonly ManualResetEventSlim? _initialized;

    // Worker-thread-only state. Never touch these from any other thread.
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _defaultDevice;
    private AudioEndpointVolume? _defaultEndpoint;
    private MMDevice? _sourceDevice;
    private AudioEndpointVolume? _sourceEndpoint;
    private string? _defaultDeviceId;
    private volatile string? _sourceDeviceId;
    private volatile bool _sourceIsDefault;

    private System.Threading.Timer? _fallbackTimer;
    private volatile bool _disposed;
    private volatile bool _systemAvailable;
    private long _externalNotificationCount;

    // Logical state, protected by _gate and readable from any thread.
    private float _masterVolume = 1f;
    private bool _masterMute;
    private bool _balanceEnabled;
    private float _rampStartGain = 1f;
    private float _targetGain = 1f;
    private long _rampStartTicks;
    private bool _ramping;

    // Last endpoint value observed or written by this app. Protected by _gate.
    private long _suppressUntilTicks;
    private float _lastEndpointVolume = 1f;
    private bool _lastEndpointMute;

    public event EventHandler<float>? MasterVolumeChanged;
    public event EventHandler<bool>? MasterMuteChanged;

    /// <param name="manageSystemVolume">
    /// When false the manager does not read or write Windows endpoints. Used by
    /// unit tests so they never change the user's system volume.
    /// </param>
    /// <param name="rampMilliseconds">Gain ramp length; tests may lengthen it.</param>
    public MasterVolumeManager(bool manageSystemVolume = true, int rampMilliseconds = DefaultRampMilliseconds)
    {
        _manageSystemVolume = manageSystemVolume;
        _rampMilliseconds = Math.Max(1, rampMilliseconds);

        if (!manageSystemVolume)
        {
            return;
        }

        _initialized = new ManualResetEventSlim(false);
        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpeakerConnect volume endpoint"
        };
        worker.SetApartmentState(ApartmentState.MTA);
        _worker = worker;
        var started = false;

        try
        {
            worker.Start();
            started = true;

            // Initialize eagerly so the logical master mirrors the Windows level
            // from the first moment. All COM work happens on the worker thread.
            Post(InitializeEndpoints);

            // Do not block the UI forever if a driver hangs; the worker retries on
            // the fallback poll and the in-app gain path keeps working either way.
            _initialized.Wait(WorkerJoinMilliseconds);

            _fallbackTimer = new System.Threading.Timer(
                OnFallbackPoll,
                null,
                FallbackPollMilliseconds,
                FallbackPollMilliseconds);
        }
        catch (Exception)
        {
            // Endpoint integration is optional. Never let a Core Audio failure
            // prevent the application from starting; the in-app gain path still
            // controls every routed speaker.
            if (!started)
            {
                _worker = null;
            }

            _systemAvailable = false;
        }
    }

    /// <summary>Logical master volume (0-1). Mute is tracked separately.</summary>
    public float MasterVolume
    {
        get
        {
            lock (_gate)
            {
                return _masterVolume;
            }
        }
        set
        {
            if (!UpdateLogicalState(value, null))
            {
                return;
            }

            Post(PushEndpointsForAppChange);
            RaiseChanged();
        }
    }

    /// <summary>Logical master mute.</summary>
    public bool MasterMute
    {
        get
        {
            lock (_gate)
            {
                return _masterMute;
            }
        }
        set
        {
            if (!UpdateLogicalState(null, value))
            {
                return;
            }

            Post(PushEndpointsForAppChange);
            RaiseChanged();
        }
    }

    /// <summary>Logical volume with mute taken into account (not ramp-interpolated).</summary>
    public float EffectiveMasterVolume
    {
        get
        {
            lock (_gate)
            {
                return _masterMute ? 0f : _masterVolume;
            }
        }
    }

    /// <summary>
    /// When enabled, each device's saved balance gain is applied on top of its manual
    /// trim. The source endpoint gain is applied through its Windows endpoint volume
    /// while routing is active. Disabling ramps every gain back to unity but keeps the
    /// saved calibration so it can be re-enabled without measuring again.
    /// </summary>
    public bool BalanceEnabled
    {
        get
        {
            lock (_gate)
            {
                return _balanceEnabled;
            }
        }
        set
        {
            bool changed;
            lock (_gate)
            {
                if (_disposed || _balanceEnabled == value)
                {
                    return;
                }

                _balanceEnabled = value;
                var ids = new HashSet<string>(_balanceGains.Keys, StringComparer.OrdinalIgnoreCase);
                ids.UnionWith(_balanceRamps.Keys);

                foreach (var deviceId in ids)
                {
                    var target = value && _balanceGains.TryGetValue(deviceId, out var gain)
                        ? Math.Clamp(gain, 0.01f, 1f)
                        : 1f;
                    SetBalanceRampTargetLocked(deviceId, target);
                }

                changed = true;
            }

            if (changed)
            {
                // Enabling can cap the source/default endpoint; disabling restores
                // the normal Windows slider range.
                Post(PushEndpointsForAppChange);
            }
        }
    }

    /// <summary>Stores a calibrated balance gain (0.01-1) for one persistent endpoint id.</summary>
    public void SetDeviceBalanceGain(string deviceId, float gain)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var clamped = Math.Clamp(gain, 0.01f, 1f);
        var changed = false;

        lock (_gate)
        {
            changed = !_balanceGains.TryGetValue(deviceId, out var existing) ||
                      Math.Abs(existing - clamped) > 0.0001f;
            _balanceGains[deviceId] = clamped;

            if (_balanceEnabled)
            {
                SetBalanceRampTargetLocked(deviceId, clamped);
            }
            else if (_balanceRamps.TryGetValue(deviceId, out var ramp) && ramp.Ramping)
            {
                // Automatic application is off; the audible gain must stay at unity.
                SetBalanceRampTargetLocked(deviceId, 1f);
            }
        }

        if (changed)
        {
            Post(PushSourceEndpointForBalanceChange);
        }
    }

    /// <summary>Saved balance gain for a device (1 = no attenuation).</summary>
    public float GetDeviceBalanceGain(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return 1f;
        }

        lock (_gate)
        {
            return _balanceGains.TryGetValue(deviceId, out var gain) ? gain : 1f;
        }
    }

    /// <summary>Current ramp-interpolated balance gain for a device (diagnostics/tests).</summary>
    public float GetRampedBalanceGain(string deviceId)
    {
        lock (_gate)
        {
            return GetCurrentBalanceGainLocked(deviceId);
        }
    }

    /// <summary>Snapshot of the saved per-device gains, keyed by endpoint id.</summary>
    public IReadOnlyDictionary<string, float> GetBalanceGains()
    {
        lock (_gate)
        {
            return new Dictionary<string, float>(_balanceGains, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Effective gain currently mapped onto the bound source endpoint (1 when unavailable).</summary>
    public float SourceBalanceGain
    {
        get
        {
            lock (_gate)
            {
                return GetSourceBalanceGainLocked();
            }
        }
    }

    /// <summary>
    /// Removes every saved balance gain and ramps active corrections to unity. The
    /// BalanceEnabled preference itself is left unchanged.
    /// </summary>
    public void ClearBalanceGains()
    {
        lock (_gate)
        {
            _balanceGains.Clear();
            foreach (var deviceId in _balanceRamps.Keys.ToList())
            {
                SetBalanceRampTargetLocked(deviceId, 1f);
            }
        }

        Post(PushEndpointsForAppChange);
    }

    /// <summary>True when Windows endpoint notifications are being received.</summary>
    public bool IsSystemVolumeAvailable => _manageSystemVolume && _systemAvailable;

    /// <summary>
    /// Diagnostics counter: number of external Windows volume/mute notifications
    /// received (writes made by this app are not counted). Used by the test suite
    /// to prove the COM callback path is live, rather than merely proving that the
    /// fallback poll eventually noticed a change.
    /// </summary>
    public long ReceivedExternalNotificationCount => Interlocked.Read(ref _externalNotificationCount);

    /// <summary>
    /// Binds the loopback source endpoint to the group volume. If the source is a
    /// different device than the current Windows default, its own endpoint volume is
    /// mirrored so that speaker follows the group too.
    /// </summary>
    public void SetSourceDevice(string sourceDeviceId)
    {
        if (!_manageSystemVolume || string.IsNullOrEmpty(sourceDeviceId))
        {
            return;
        }

        Post(() => BindSourceDevice(sourceDeviceId));
    }

    /// <summary>
    /// Releases the source endpoint binding at the end of a routing session so idle
    /// Windows volume changes do not write to a device that is no longer part of the
    /// group. The default-endpoint subscription stays alive.
    /// </summary>
    public void ClearSourceDevice()
    {
        if (!_manageSystemVolume)
        {
            return;
        }

        Post(ClearSourceDeviceCore);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var timer = _fallbackTimer;
        _fallbackTimer = null;
        timer?.Dispose();

        if (_manageSystemVolume)
        {
            // Queue the shutdown behind any in-flight work, then let the worker
            // drain and exit. The queue is completed even if disposal happens on
            // the worker thread, but then there is nobody to join.
            try
            {
                _work.Add(ShutdownEndpoints);
            }
            catch (Exception)
            {
                // Already completed; nothing left to release.
            }

            try
            {
                _work.CompleteAdding();
            }
            catch (Exception)
            {
                // Idempotent shutdown.
            }

        }

        var workerStopped = true;
        if (_worker is not null && Thread.CurrentThread != _worker)
        {
            try
            {
                workerStopped = _worker.Join(WorkerJoinMilliseconds);
            }
            catch (Exception)
            {
                // Best-effort shutdown.
            }
        }

        // Only dispose the queue once the worker has stopped consuming it. If a
        // driver hangs a COM call, leaking the queue is safer than disposing it
        // underneath the worker thread.
        if (workerStopped || _worker is null)
        {
            try
            {
                _work.Dispose();
            }
            catch (Exception)
            {
                // Best-effort shutdown.
            }

            _initialized?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Registers a per-device trim (0-1) applied on top of master volume.</summary>
    public void SetDeviceVolume(string deviceId, float volume)
    {
        lock (_gate)
        {
            _deviceVolumes[deviceId] = Math.Clamp(volume, 0f, 1f);
        }
    }

    /// <summary>Effective volume for a device: trim * balance gain * master, mute applied.</summary>
    public float GetEffectiveVolume(string deviceId)
    {
        lock (_gate)
        {
            var baseVolume = _deviceVolumes.TryGetValue(deviceId, out var v) ? v : 1f;
            return baseVolume * GetCurrentBalanceGainLocked(deviceId) *
                   (_masterMute ? 0f : _masterVolume);
        }
    }

    /// <summary>Render-thread entry point: trim * ramped balance gain * current ramped master.</summary>
    public float GetRampedVolume(string deviceId)
    {
        lock (_gate)
        {
            var baseVolume = _deviceVolumes.TryGetValue(deviceId, out var v) ? v : 1f;
            return baseVolume * GetCurrentBalanceGainLocked(deviceId) * GetCurrentGainLocked();
        }
    }

    public void Subscribe(Action<float> onVolumeChanged)
    {
        lock (_gate)
        {
            if (!_subscribers.Contains(onVolumeChanged))
            {
                _subscribers.Add(onVolumeChanged);
            }
        }
    }

    public void Unsubscribe(Action<float> onVolumeChanged)
    {
        lock (_gate)
        {
            _subscribers.Remove(onVolumeChanged);
        }
    }

    private void Post(Action action)
    {
        if (_disposed || !_manageSystemVolume || _worker is null)
        {
            return;
        }

        try
        {
            _work.Add(action);
        }
        catch (ObjectDisposedException)
        {
            // The manager was disposed concurrently.
        }
        catch (InvalidOperationException)
        {
            // The queue was completed during shutdown.
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // A COM/endpoint failure must never terminate the application.
                    // The in-app gain path and the fallback poll keep working.
                }
            }
        }
        catch (Exception)
        {
            // The queue can be disposed by shutdown while this thread is still
            // unwinding a slow COM call. Exiting quietly is the correct outcome.
        }
    }

    /// <summary>
    /// Applies a new logical state. Returns true when anything changed. The gain
    /// ramp always starts from the *current interpolated* gain, so rapid successive
    /// changes (volume keys held down, slider dragging) never jump.
    /// </summary>
    private bool UpdateLogicalState(float? volume, bool? mute)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var newVolume = Math.Clamp(volume ?? _masterVolume, 0f, 1f);
            var newMute = mute ?? _masterMute;

            if (Math.Abs(newVolume - _masterVolume) < 0.0001f && newMute == _masterMute)
            {
                return false;
            }

            var currentGain = GetCurrentGainLocked();
            _masterVolume = newVolume;
            _masterMute = newMute;
            _rampStartGain = currentGain;
            _targetGain = newMute ? 0f : newVolume;
            _rampStartTicks = Environment.TickCount64;
            _ramping = true;
            return true;
        }
    }

    private float GetCurrentGainLocked()
    {
        if (!_ramping)
        {
            return _targetGain;
        }

        var elapsed = Environment.TickCount64 - _rampStartTicks;
        var progress = Math.Clamp((float)elapsed / _rampMilliseconds, 0f, 1f);
        if (progress >= 1f)
        {
            _ramping = false;
            return _targetGain;
        }

        return _rampStartGain + (_targetGain - _rampStartGain) * progress;
    }

    private float GetCurrentBalanceGainLocked(string deviceId)
    {
        if (_balanceRamps.TryGetValue(deviceId, out var ramp))
        {
            return AdvanceBalanceRampLocked(ramp);
        }

        if (_balanceEnabled && _balanceGains.TryGetValue(deviceId, out var gain))
        {
            return Math.Clamp(gain, 0.01f, 1f);
        }

        return 1f;
    }

    private float AdvanceBalanceRampLocked(BalanceGainRamp ramp)
    {
        if (!ramp.Ramping)
        {
            return ramp.TargetGain;
        }

        var elapsed = Environment.TickCount64 - ramp.StartTicks;
        var progress = Math.Clamp((float)elapsed / _rampMilliseconds, 0f, 1f);
        if (progress >= 1f)
        {
            ramp.Ramping = false;
            ramp.StartGain = ramp.TargetGain;
            return ramp.TargetGain;
        }

        return ramp.StartGain + (ramp.TargetGain - ramp.StartGain) * progress;
    }

    /// <summary>
    /// Retargets one balance gain from its current interpolated value. Retargeting
    /// mid-ramp (slider being dragged) therefore never makes the gain jump.
    /// </summary>
    private void SetBalanceRampTargetLocked(string deviceId, float target)
    {
        target = Math.Clamp(target, 0.01f, 1f);

        if (!_balanceRamps.TryGetValue(deviceId, out var ramp))
        {
            var start = GetCurrentBalanceGainLocked(deviceId);
            _balanceRamps[deviceId] = new BalanceGainRamp
            {
                StartGain = start,
                TargetGain = target,
                StartTicks = Environment.TickCount64,
                Ramping = Math.Abs(start - target) > 0.0001f
            };
            return;
        }

        var current = AdvanceBalanceRampLocked(ramp);
        ramp.StartGain = current;
        ramp.TargetGain = target;
        ramp.StartTicks = Environment.TickCount64;
        ramp.Ramping = Math.Abs(current - target) > 0.0001f;
    }

    /// <summary>
    /// Balance gain that must be applied to the loopback source's Windows endpoint
    /// volume. The loopback tap is before endpoint volume, so this affects only the
    /// source speaker, not the copies sent to the other outputs.
    /// </summary>
    private float GetSourceBalanceGainLocked()
    {
        if (!_balanceEnabled || _sourceDeviceId is null)
        {
            return 1f;
        }

        return _balanceGains.TryGetValue(_sourceDeviceId, out var gain)
            ? Math.Clamp(gain, 0.01f, 1f)
            : 1f;
    }

    /// <summary>
    /// Physical volume that should be written to the Windows default endpoint for a
    /// logical master value. When that default endpoint is also the routed source,
    /// its volume is the source balance gain multiplied by the logical master.
    /// </summary>
    private float GetDefaultEndpointPhysicalVolumeLocked(float logicalVolume)
    {
        if (_sourceIsDefault && _balanceEnabled)
        {
            return Math.Clamp(logicalVolume * GetSourceBalanceGainLocked(), 0f, 1f);
        }

        return Math.Clamp(logicalVolume, 0f, 1f);
    }

    private float GetSourceEndpointPhysicalVolumeLocked(float logicalVolume)
    {
        return Math.Clamp(logicalVolume * GetSourceBalanceGainLocked(), 0f, 1f);
    }

    private void RaiseChanged()
    {
        float effective;
        bool muted;
        lock (_gate)
        {
            effective = _masterMute ? 0f : _masterVolume;
            muted = _masterMute;
        }

        try
        {
            MasterVolumeChanged?.Invoke(this, effective);
        }
        catch (Exception)
        {
            // UI/subscriber failures must not break volume management.
        }

        try
        {
            MasterMuteChanged?.Invoke(this, muted);
        }
        catch (Exception)
        {
            // UI/subscriber failures must not break volume management.
        }

        List<Action<float>> subscribers;
        lock (_gate)
        {
            subscribers = _subscribers.Count == 0
                ? new List<Action<float>>()
                : new List<Action<float>>(_subscribers);
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(effective);
            }
            catch (Exception)
            {
                // Subscriber exceptions must never break volume management.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Worker-thread methods. Every COM call below runs on the dedicated STA
    // thread, so no RCW is ever marshalled between apartments.
    // ---------------------------------------------------------------------

    private void InitializeEndpoints()
    {
        if (_disposed || _enumerator is not null)
        {
            _initialized?.Set();
            return;
        }

        try
        {
            _enumerator = new MMDeviceEnumerator();
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var endpoint = device.AudioEndpointVolume;
            AttachDefaultDevice(device, endpoint);

            float volume;
            bool mute;
            lock (_gate)
            {
                volume = _lastEndpointVolume;
                mute = _lastEndpointMute;
            }

            // Adopt the Windows level so the in-app slider mirrors the system
            // slider from the first moment.
            if (UpdateLogicalState(volume, mute))
            {
                RaiseChanged();
            }
        }
        catch (Exception)
        {
            // System-volume integration is optional; the in-app slider still
            // controls destination gain through GetRampedVolume. Detaching keeps
            // any partially created objects from leaking.
            DetachDefaultDevice();
            _systemAvailable = false;
        }
        finally
        {
            _initialized?.Set();
        }
    }

    private void BindSourceDevice(string sourceDeviceId)
    {
        if (_disposed)
        {
            return;
        }

        if (_enumerator is null)
        {
            InitializeEndpoints();
        }

        if (_enumerator is null)
        {
            return;
        }

        MMDevice? device = null;
        AudioEndpointVolume? endpoint = null;

        try
        {
            device = _enumerator.GetDevice(sourceDeviceId);
            endpoint = device.AudioEndpointVolume;
            endpoint.NotificationGuid = _notificationContext;
        }
        catch (Exception)
        {
            DisposeQuietly(endpoint);
            DisposeQuietly(device);
            return;
        }

        var previousDevice = _sourceDevice;
        var previousEndpoint = _sourceEndpoint;

        _sourceDevice = device;
        _sourceEndpoint = endpoint;

        float volume;
        float sourceVolume;
        bool mute;
        lock (_gate)
        {
            _sourceDeviceId = sourceDeviceId;
            _sourceIsDefault = _defaultDeviceId is not null &&
                string.Equals(_defaultDeviceId, sourceDeviceId, StringComparison.OrdinalIgnoreCase);
            volume = _masterVolume;
            mute = _masterMute;
            sourceVolume = GetSourceEndpointPhysicalVolumeLocked(volume);

            if (_sourceIsDefault)
            {
                // The source is the default endpoint; the physical slider now holds
                // the calibrated ceiling (logical master * source balance gain).
                _lastEndpointVolume = sourceVolume;
                _lastEndpointMute = mute;
                _suppressUntilTicks = Environment.TickCount64 + SuppressWindowMilliseconds;
            }
        }

        // Keep the source speaker in step with the group volume. When the source
        // is the default endpoint, this applies its calibrated balance ceiling.
        TrySetEndpoint(endpoint, sourceVolume, mute);

        DisposeQuietly(previousEndpoint);
        DisposeQuietly(previousDevice);
    }

    private void ClearSourceDeviceCore()
    {
        var device = _sourceDevice;
        var endpoint = _sourceEndpoint;
        var wasDefault = _sourceIsDefault;

        // Restore the source endpoint's normal Windows range. While balance was
        // active, a default source could have been capped at master * source gain.
        if (endpoint is not null)
        {
            float volume;
            bool mute;
            lock (_gate)
            {
                volume = _masterVolume;
                mute = _masterMute;
                if (wasDefault)
                {
                    _lastEndpointVolume = volume;
                    _lastEndpointMute = mute;
                    _suppressUntilTicks = Environment.TickCount64 + SuppressWindowMilliseconds;
                }
            }

            TrySetEndpoint(endpoint, volume, mute);
        }

        _sourceDevice = null;
        _sourceEndpoint = null;
        _sourceDeviceId = null;
        _sourceIsDefault = false;

        DisposeQuietly(endpoint);
        DisposeQuietly(device);
    }

    private void PushEndpointsForAppChange()
    {
        PushEndpoints(sourceOnly: false);
    }

    /// <summary>
    /// <paramref name="sourceOnly"/> = true when the default endpoint already holds
    /// the new value (external Windows change), so only the source endpoint needs a
    /// mirror write. False = push the logical value to both endpoints (in-app change).
    /// </summary>
    private void PushEndpoints(bool sourceOnly)
    {
        if (!_manageSystemVolume || _disposed)
        {
            return;
        }

        float volume;
        bool mute;
        float defaultVolume;
        float sourceVolume;
        bool sourceIsDefault;
        lock (_gate)
        {
            _suppressUntilTicks = Environment.TickCount64 + SuppressWindowMilliseconds;
            volume = _masterVolume;
            mute = _masterMute;
            sourceIsDefault = _sourceIsDefault;
            defaultVolume = GetDefaultEndpointPhysicalVolumeLocked(volume);
            sourceVolume = GetSourceEndpointPhysicalVolumeLocked(volume);

            if (!sourceOnly)
            {
                _lastEndpointVolume = defaultVolume;
                _lastEndpointMute = mute;
            }
        }

        if (!sourceOnly)
        {
            TrySetEndpoint(_defaultEndpoint, defaultVolume, mute);
        }

        if (!sourceIsDefault)
        {
            TrySetEndpoint(_sourceEndpoint, sourceVolume, mute);
        }
    }

    /// <summary>
    /// Re-applies the source endpoint volume after a balance gain change made while
    /// routing is active. Destination gains are read by their audio threads, so only
    /// the source (whose correction lives in Windows endpoint volume) needs a write.
    /// </summary>
    private void PushSourceEndpointForBalanceChange()
    {
        if (!_manageSystemVolume || _disposed)
        {
            return;
        }

        float volume;
        bool mute;
        float defaultVolume;
        float sourceVolume;
        bool sourceIsDefault;
        lock (_gate)
        {
            _suppressUntilTicks = Environment.TickCount64 + SuppressWindowMilliseconds;
            volume = _masterVolume;
            mute = _masterMute;
            sourceIsDefault = _sourceIsDefault;
            defaultVolume = GetDefaultEndpointPhysicalVolumeLocked(volume);
            sourceVolume = GetSourceEndpointPhysicalVolumeLocked(volume);

            if (sourceIsDefault)
            {
                _lastEndpointVolume = defaultVolume;
                _lastEndpointMute = mute;
            }
        }

        if (sourceIsDefault)
        {
            TrySetEndpoint(_defaultEndpoint, defaultVolume, mute);
        }
        else
        {
            TrySetEndpoint(_sourceEndpoint, sourceVolume, mute);
        }
    }

    private static void TrySetEndpoint(AudioEndpointVolume? endpoint, float volume, bool mute)
    {
        if (endpoint is null)
        {
            return;
        }

        try
        {
            endpoint.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
        }
        catch (Exception)
        {
            // Best-effort; the in-app gain path still works.
        }

        try
        {
            endpoint.Mute = mute;
        }
        catch (Exception)
        {
            // Best-effort; the in-app gain path still works.
        }
    }

    private void AttachDefaultDevice(MMDevice device, AudioEndpointVolume endpoint)
    {
        endpoint.NotificationGuid = _notificationContext;
        endpoint.OnVolumeNotification += OnSystemVolumeNotification;

        _defaultDevice = device;
        _defaultEndpoint = endpoint;
        _defaultDeviceId = SafeDeviceId(device);
        _sourceIsDefault = _sourceDeviceId is not null && _defaultDeviceId is not null &&
            string.Equals(_sourceDeviceId, _defaultDeviceId, StringComparison.OrdinalIgnoreCase);
        _lastEndpointVolume = SafeVolume(endpoint);
        _lastEndpointMute = SafeMute(endpoint);
        _systemAvailable = true;
    }

    private void DetachDefaultDevice()
    {
        var endpoint = _defaultEndpoint;
        var device = _defaultDevice;

        _defaultEndpoint = null;
        _defaultDevice = null;
        _defaultDeviceId = null;
        _systemAvailable = false;

        if (endpoint is not null)
        {
            try
            {
                endpoint.OnVolumeNotification -= OnSystemVolumeNotification;
            }
            catch (Exception)
            {
                // Ignore detach failures.
            }

            DisposeQuietly(endpoint);
        }

        DisposeQuietly(device);
    }

    private static string? SafeDeviceId(MMDevice device)
    {
        try
        {
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static float SafeVolume(AudioEndpointVolume endpoint)
    {
        try
        {
            return endpoint.MasterVolumeLevelScalar;
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    private static bool SafeMute(AudioEndpointVolume endpoint)
    {
        try
        {
            return endpoint.Mute;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Ignore COM cleanup failures.
        }
    }

    private void ShutdownEndpoints()
    {
        DetachDefaultDevice();
        ClearSourceDeviceCore();

        DisposeQuietly(_enumerator);
        _enumerator = null;
    }

    /// <summary>
    /// Windows volume-key / taskbar notification. The callback context tells us
    /// whether the change was made by this app (our GUID) or by Windows/another
    /// application (any other GUID). Our own writes are ignored, preventing loops.
    /// This runs on a COM callback thread, so it must not touch any COM object.
    /// </summary>
    private void OnSystemVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed)
        {
            return;
        }

        if (data.EventContext == _notificationContext)
        {
            return;
        }

        Interlocked.Increment(ref _externalNotificationCount);

        // Route the observation through the worker so it is serialized with the
        // endpoint writes this app makes. A notification can otherwise arrive after
        // a newer in-app change and overwrite it with a stale value.
        Post(() => ApplyExternalNotification(data.MasterVolume, data.Muted));
    }

    /// <summary>
    /// Applies a Windows volume notification on the worker thread. The endpoint is
    /// re-read first: if another write (ours or another application's) has already
    /// superseded the notification, the observed value no longer matches and the
    /// stale notification is ignored.
    /// </summary>
    private void ApplyExternalNotification(float volume, bool mute)
    {
        if (_disposed || _defaultEndpoint is null)
        {
            return;
        }

        float current;
        bool currentMute;
        try
        {
            current = _defaultEndpoint.MasterVolumeLevelScalar;
            currentMute = _defaultEndpoint.Mute;
        }
        catch (Exception)
        {
            return;
        }

        if (Math.Abs(current - volume) > 0.005f || currentMute != mute)
        {
            return;
        }

        float lastVolume;
        bool lastMute;
        lock (_gate)
        {
            lastVolume = _lastEndpointVolume;
            lastMute = _lastEndpointMute;
        }

        // A notification that matches the value this app last wrote (or last
        // observed) is an echo of our own endpoint write. Windows does not always
        // carry the app's notification context, so match on the value instead.
        if (Math.Abs(volume - lastVolume) < 0.001f && mute == lastMute)
        {
            return;
        }

        ApplyExternalState(volume, mute);
    }

    private void ApplyExternalState(float volume, bool mute)
    {
        var endpointVolume = Math.Clamp(volume, 0f, 1f);
        float logicalVolume;
        float sourceGain = 1f;
        var needsRewrite = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastEndpointVolume = endpointVolume;
            _lastEndpointMute = mute;

            if (_sourceIsDefault && _balanceEnabled)
            {
                sourceGain = GetSourceBalanceGainLocked();
                if (sourceGain < 0.9999f)
                {
                    // The default endpoint is the loopback source. Its physical
                    // volume is logical master * source balance gain, so map the
                    // observed slider value back to the logical master.
                    var idealLogical = endpointVolume / sourceGain;
                    logicalVolume = Math.Clamp(idealLogical, 0f, 1f);

                    // Above the calibrated ceiling the physical slider cannot be
                    // allowed to rise without breaking the balance. The endpoint is
                    // rewritten to the ceiling below; the UI documents this cap.
                    needsRewrite = idealLogical > 1.0001f;
                }
                else
                {
                    logicalVolume = endpointVolume;
                }
            }
            else
            {
                logicalVolume = endpointVolume;
            }
        }

        var changed = UpdateLogicalState(logicalVolume, mute);

        if (needsRewrite)
        {
            lock (_gate)
            {
                _lastEndpointVolume = Math.Clamp(logicalVolume * sourceGain, 0f, 1f);
                _lastEndpointMute = mute;
            }

            Post(PushEndpointsForAppChange);
        }
        else if (changed)
        {
            // The default endpoint already reflects the new level; mirror it to the
            // loopback source when that is a different endpoint.
            Post(PushEndpointsForExternalChange);
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void PushEndpointsForExternalChange()
    {
        PushEndpoints(sourceOnly: true);
    }

    private void OnFallbackPoll(object? state)
    {
        if (_disposed || !_manageSystemVolume)
        {
            return;
        }

        // The timer callback itself does no COM work; it just wakes the STA worker.
        Post(PollEndpoints);
    }

    /// <summary>
    /// Slow safety net. Handles default-device switches (the notification
    /// subscription follows the old endpoint) and picks up a volume change if a
    /// driver failed to raise the notification event. Runs on the worker thread.
    /// </summary>
    private void PollEndpoints()
    {
        if (_disposed)
        {
            return;
        }

        if (_enumerator is null)
        {
            InitializeEndpoints();
            return;
        }

        MMDevice? currentDefault = null;

        try
        {
            currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var currentDefaultId = SafeDeviceId(currentDefault);

            var defaultChanged = _defaultDevice is null ||
                currentDefaultId is null ||
                !string.Equals(_defaultDeviceId, currentDefaultId, StringComparison.OrdinalIgnoreCase);

            if (defaultChanged)
            {
                AudioEndpointVolume? endpoint = null;
                try
                {
                    endpoint = currentDefault.AudioEndpointVolume;
                }
                catch (Exception)
                {
                    DisposeQuietly(endpoint);
                    return;
                }

                DetachDefaultDevice();
                AttachDefaultDevice(currentDefault, endpoint);
                currentDefault = null; // ownership moved to the manager

                float volume;
                bool mute;
                lock (_gate)
                {
                    volume = _lastEndpointVolume;
                    mute = _lastEndpointMute;
                }

                // Adopt the new default device's level as the group level.
                ApplyExternalState(volume, mute);
                return;
            }

            if (_defaultEndpoint is null)
            {
                return;
            }

            float observedVolume;
            bool observedMute;
            try
            {
                observedVolume = _defaultEndpoint.MasterVolumeLevelScalar;
                observedMute = _defaultEndpoint.Mute;
            }
            catch (Exception)
            {
                return;
            }

            long suppressUntil;
            float lastVolume;
            bool lastMute;
            lock (_gate)
            {
                suppressUntil = _suppressUntilTicks;
                lastVolume = _lastEndpointVolume;
                lastMute = _lastEndpointMute;
            }

            if (Environment.TickCount64 < suppressUntil)
            {
                return;
            }

            if (Math.Abs(observedVolume - lastVolume) < 0.001f && observedMute == lastMute)
            {
                return;
            }

            ApplyExternalState(observedVolume, observedMute);
        }
        catch (Exception)
        {
            // A transient COM failure on the poll must never escape the worker.
        }
        finally
        {
            DisposeQuietly(currentDefault);
        }
    }

    /// <summary>One per-device balance gain ramp state, protected by the manager gate.</summary>
    private sealed class BalanceGainRamp
    {
        public float StartGain { get; set; } = 1f;

        public float TargetGain { get; set; } = 1f;

        public long StartTicks { get; set; }

        public bool Ramping { get; set; }
    }
}
