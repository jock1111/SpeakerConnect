using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpeakerConnect.Models;

namespace SpeakerConnect.Audio;

public sealed class RoutingStateChangedEventArgs : EventArgs
{
    public RoutingStateChangedEventArgs(AudioRoutingState state)
    {
        State = state;
    }

    public AudioRoutingState State { get; }
}

public sealed class AudioRouteFaultEventArgs : EventArgs
{
    public AudioRouteFaultEventArgs(string? deviceId, string message, bool isFatal)
    {
        DeviceId = deviceId;
        Message = message;
        IsFatal = isFatal;
    }

    public string? DeviceId { get; }

    public string Message { get; }

    public bool IsFatal { get; }
}

/// <summary>
/// Informational notification that one destination was rebuilt with a more robust
/// buffer profile after repeated underruns/dropped frames.
/// </summary>
public sealed class StabilityFallbackEventArgs : EventArgs
{
    public StabilityFallbackEventArgs(
        string deviceId,
        string deviceName,
        BufferProfile fromProfile,
        BufferProfile toProfile,
        long underruns,
        long droppedFrames)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        FromProfile = fromProfile;
        ToProfile = toProfile;
        Underruns = underruns;
        DroppedFrames = droppedFrames;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public BufferProfile FromProfile { get; }

    public BufferProfile ToProfile { get; }

    public long Underruns { get; }

    public long DroppedFrames { get; }
}

/// <summary>
/// Coordinates one loopback capture with up to four additional output channels.
/// The source endpoint plus those four destinations are five audible endpoints total;
/// Windows renders the source independently and it is not part of the output fan-out.
///
/// Feedback prevention:
///  * a device cannot be both the loopback source and a destination;
///  * only one capture instance can exist at a time;
///  * the engine serializes start/stop operations and reuses one capture stream.
///
/// The selected source endpoint's original playback is performed by Windows and is
/// never stopped or muted by this application.
/// </summary>
public sealed class AudioRoutingEngine : IDisposable
{
    /// <summary>Maximum audible endpoints in a session, including the source.</summary>
    public const int MaxTotalAudibleDevices = 5;

    /// <summary>Maximum destinations receiving a copy; the source occupies one slot.</summary>
    public const int MaxAdditionalOutputs = MaxTotalAudibleDevices - 1;

    // Retained as a compatibility alias for callers built against the previous cap.
    public const int MaxOutputChannels = MaxAdditionalOutputs;

    private static readonly TimeSpan RingBufferCapacity = TimeSpan.FromSeconds(12);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly AudioDeviceEnumerator _deviceEnumerator;
    private readonly AudioDeviceWatcher _deviceWatcher;
    private readonly Dictionary<string, AudioOutputChannel> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    private LoopbackCaptureService? _capture;
    private AudioRingBuffer? _ringBuffer;
    private string? _sourceDeviceId;
    private string? _sourceDeviceName;
    private AudioRoutingState _state = AudioRoutingState.Stopped;
    private string? _lastError;
    private long _sessionGeneration;
    private volatile bool _disposed;
    private volatile BufferProfile? _activeProfile;
    private readonly StabilityMonitor _stabilityMonitor = new();
    private MasterVolumeManager? _masterVolume;
    private CancellationTokenSource? _syncCancellation;
    private volatile bool _syncTestActive;

    public AudioRoutingEngine(AudioDeviceEnumerator deviceEnumerator, AudioDeviceWatcher deviceWatcher)
    {
        _deviceEnumerator = deviceEnumerator ?? throw new ArgumentNullException(nameof(deviceEnumerator));
        _deviceWatcher = deviceWatcher ?? throw new ArgumentNullException(nameof(deviceWatcher));
        _deviceWatcher.DeviceRemoved += OnDeviceRemoved;

        // One master-volume manager for the whole engine lifetime so the logical
        // level survives Start/Stop and mirrors Windows even while idle.
        _masterVolume = new MasterVolumeManager();
        _masterVolume.MasterVolumeChanged += OnMasterVolumeChanged;
        _masterVolume.MasterMuteChanged += OnMasterMuteChanged;
    }

    public event EventHandler<RoutingStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioRouteFaultEventArgs>? FaultOccurred;

    /// <summary>
    /// Raised when the logical master volume changes, including changes made with
    /// Windows volume keys or the taskbar slider.
    /// </summary>
    public event EventHandler<float>? MasterVolumeChanged;

    /// <summary>
    /// Raised when the logical master mute changes, including Windows mute changes.
    /// </summary>
    public event EventHandler<bool>? MasterMuteChanged;

    /// <summary>
    /// Raised when the automatic stability fallback rebuilt one destination with a
    /// more robust buffer profile. This is informational, not a fault.
    /// </summary>
    public event EventHandler<StabilityFallbackEventArgs>? StabilityFallback;

    public AudioRoutingState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public string? SourceDeviceName
    {
        get
        {
            lock (_gate)
            {
                return _sourceDeviceName;
            }
        }
    }

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Profile actually used by the current/last session, or null before the first start.</summary>
    public BufferProfile? ActiveBufferProfile => _activeProfile;

    /// <summary>Master volume level (0-1) applied as a multiplier on all destination channels.</summary>
    public float MasterVolume
    {
        get => _masterVolume?.MasterVolume ?? 1f;
        set
        {
            if (_masterVolume is not null)
            {
                _masterVolume.MasterVolume = value;
            }
        }
    }

    /// <summary>Master mute state applied to all destination channels.</summary>
    public bool MasterMute
    {
        get => _masterVolume?.MasterMute ?? false;
        set
        {
            if (_masterVolume is not null)
            {
                _masterVolume.MasterMute = value;
            }
        }
    }

    /// <summary>
    /// Enables or disables automatic Smart Volume Balance application. Saved gains
    /// are retained while disabled, and re-enabling ramps them back in.
    /// </summary>
    public bool BalanceEnabled
    {
        get => _masterVolume?.BalanceEnabled ?? false;
        set
        {
            if (_masterVolume is not null)
            {
                _masterVolume.BalanceEnabled = value;
            }
        }
    }

    /// <summary>Stores a calibrated attenuation gain for one persistent endpoint id.</summary>
    public void SetBalanceGain(string deviceId, float gain)
    {
        _masterVolume?.SetDeviceBalanceGain(deviceId, gain);
    }

    /// <summary>Saved balance gain for a device (1 = no attenuation).</summary>
    public float GetBalanceGain(string deviceId)
    {
        return _masterVolume?.GetDeviceBalanceGain(deviceId) ?? 1f;
    }

    /// <summary>Saved balance gains keyed by persistent endpoint id.</summary>
    public IReadOnlyDictionary<string, float> GetBalanceGains()
    {
        return _masterVolume?.GetBalanceGains() ??
               new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Removes every saved balance gain and ramps active corrections to unity.</summary>
    public void ClearBalanceGains()
    {
        _masterVolume?.ClearBalanceGains();
    }

    /// <summary>
    /// Engine-wide roll-up for the UI: measured software buffer depth, underruns,
    /// dropped frames, and the software-side routing-latency estimate. This never
    /// includes acoustic latency (hardware/codec delay is not measurable here).
    /// </summary>
    public RoutingDiagnosticsSummary GetDiagnosticsSummary()
    {
        var statuses = GetChannelStatuses();
        var state = State;
        if (statuses.Count == 0)
        {
            return new RoutingDiagnosticsSummary(
                state,
                0,
                _activeProfile?.DisplayName ?? BufferProfile.Default.DisplayName,
                0,
                0,
                0,
                0);
        }

        return new RoutingDiagnosticsSummary(
            state,
            statuses.Count,
            statuses[0].ProfileName,
            statuses.Max(status => status.BufferedMilliseconds),
            statuses.Max(status => status.EstimatedRoutingLatencyMilliseconds),
            statuses.Sum(status => status.UnderrunCount),
            statuses.Sum(status => status.DroppedFrameCount));
    }

    public IReadOnlyList<AudioChannelStatus> GetChannelStatuses()
    {
        List<AudioOutputChannel> channels;
        lock (_gate)
        {
            channels = _channels.Values.ToList();
        }

        return channels.Select(channel => channel.GetStatus()).ToList();
    }

    public RoutingStartResult Start(AudioRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioRoutingEngine));
        }

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return new RoutingStartResult(
                false,
                validationError,
                Array.Empty<AudioChannelStatus>(),
                new[] { validationError });
        }

        CancelSyncTest();
        _syncGate.Wait();
        _syncGate.Release();

        _lifecycle.Wait();
        try
        {
            lock (_gate)
            {
                if (_state is AudioRoutingState.Running or
                    AudioRoutingState.Starting or
                    AudioRoutingState.Stopping)
                {
                    return new RoutingStartResult(
                        false,
                        "Audio routing is already active. Stop it before starting again.",
                        GetChannelStatuses(),
                        Array.Empty<string>());
                }
            }

            SetState(AudioRoutingState.Starting);
            Volatile.Write(ref _lastError, null);

            var result = StartCore(request);
            if (!result.Started)
            {
                CleanupResources();
                SetState(AudioRoutingState.Faulted);
                Volatile.Write(ref _lastError, result.Message);
                RaiseFault(null, result.Message, true);
            }
            else
            {
                SetState(AudioRoutingState.Running);
                var monitorGeneration = Volatile.Read(ref _sessionGeneration);
                _ = Task.Run(() => MonitorStabilityLoop(monitorGeneration));
            }

            return result with { Channels = GetChannelStatuses() };
        }
        catch (Exception ex)
        {
            CleanupResources();
            SetState(AudioRoutingState.Faulted);
            Volatile.Write(ref _lastError, ex.Message);
            RaiseFault(null, $"Audio routing failed: {ex.Message}", true);

            return new RoutingStartResult(
                false,
                $"Audio routing failed: {ex.Message}",
                Array.Empty<AudioChannelStatus>(),
                new[] { ex.Message });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Stop()
    {
        CancelSyncTest();

        _lifecycle.Wait();
        try
        {
            AudioRoutingState state;
            lock (_gate)
            {
                state = _state;
            }

            if (state is AudioRoutingState.Stopped or AudioRoutingState.Stopping)
            {
                return;
            }

            SetState(AudioRoutingState.Stopping);

            // A running sync test uses the live channels and ring. Wait for it to
            // observe cancellation before the channels are disposed.
            _syncGate.Wait();
            try
            {
                CleanupResources();
            }
            finally
            {
                _syncGate.Release();
            }

            SetState(AudioRoutingState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void UpdateVolume(string deviceId, float volume)
    {
        var channel = FindChannel(deviceId);
        if (channel is not null)
        {
            channel.Volume = volume;
        }
    }

    public void UpdateDelay(string deviceId, int delayMilliseconds)
    {
        var channel = FindChannel(deviceId);
        if (channel is not null)
        {
            channel.DelayMilliseconds = delayMilliseconds;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _deviceWatcher.DeviceRemoved -= OnDeviceRemoved;

        CancelSyncTest();

        _lifecycle.Wait();
        try
        {
            _disposed = true;
            SetState(AudioRoutingState.Stopping);
            _syncGate.Wait();
            try
            {
                CleanupResources();
            }
            finally
            {
                _syncGate.Release();
            }

            SetState(AudioRoutingState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }

        if (_masterVolume is not null)
        {
            _masterVolume.MasterVolumeChanged -= OnMasterVolumeChanged;
            _masterVolume.MasterMuteChanged -= OnMasterMuteChanged;
            _masterVolume.Dispose();
            _masterVolume = null;
        }

        // The semaphore is intentionally not disposed: a fault callback scheduled
        // immediately before shutdown may still be waiting on it.
        GC.SuppressFinalize(this);
    }

    private RoutingStartResult StartCore(AudioRoutingRequest request)
    {
        var errors = new List<string>();
        var generation = Interlocked.Increment(ref _sessionGeneration);
        var profile = request.Profile ?? BufferProfile.Default;
        _activeProfile = profile;
        _stabilityMonitor.ResetAll();

        var activeDevices = _deviceEnumerator.GetActiveRenderDevices();
        var activeById = activeDevices.ToDictionary(
            device => device.Id,
            StringComparer.OrdinalIgnoreCase);

        if (!activeById.TryGetValue(request.SourceDeviceId, out var sourceInfo))
        {
            return Failed($"The loopback source device is no longer active: {request.SourceDeviceId}");
        }

        var capture = new LoopbackCaptureService();
        WaveFormat captureFormat;

        try
        {
            captureFormat = capture.Prepare(request.SourceDeviceId);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            return Failed($"Could not open the loopback source: {ex.Message}");
        }

        var ringBuffer = new AudioRingBuffer(captureFormat.SampleRate, RingBufferCapacity);

        // Bind the loopback source endpoint to the shared logical master volume
        // and register the per-device trims from this routing request.
        _masterVolume?.SetSourceDevice(request.SourceDeviceId);

        foreach (var target in request.Outputs)
        {
            _masterVolume?.SetDeviceVolume(target.DeviceId, target.Volume);
        }

        lock (_gate)
        {
            _capture = capture;
            _ringBuffer = ringBuffer;
            _sourceDeviceId = request.SourceDeviceId;
            _sourceDeviceName = capture.SourceDeviceName;
        }

        capture.SamplesAvailable += OnSamplesAvailable;
        capture.Faulted += OnCaptureFaulted;

        foreach (var target in request.Outputs)
        {
            if (!activeById.TryGetValue(target.DeviceId, out var destinationInfo))
            {
                errors.Add($"Output device is no longer active: {target.DeviceId}");
                continue;
            }

            MMDevice? device = null;
            AudioOutputChannel? channel = null;

            try
            {
                device = OpenRenderDevice(target.DeviceId);

                using var audioClient = device.AudioClient;
                var mixFormat = audioClient.MixFormat;
                var targetFormat = AudioFormatHelper.GetTargetOutputFormat(mixFormat);

                try
                {
                    channel = new AudioOutputChannel(
                        device,
                        ringBuffer,
                        targetFormat,
                        profile.BaseLatencyMs,
                        profile.WasapiLatencyMs,
                        target.Volume,
                        target.DelayMilliseconds,
                        profile.DriftWindowMs,
                        _masterVolume);
                }
                catch (Exception primaryException)
                {
                    // A few Bluetooth/HDMI drivers advertise a mix format but reject
                    // that exact client encoding. Retry with the most compatible
                    // stereo/mono float format through the shared-mode engine.
                    var fallbackFormat = AudioFormatHelper.GetFallbackOutputFormat(mixFormat);
                    if (AudioFormatHelper.HasSameEncodingAndShape(targetFormat, fallbackFormat))
                    {
                        throw new InvalidOperationException(
                            $"The endpoint rejected its preferred output format: {primaryException.Message}",
                            primaryException);
                    }

                    try
                    {
                        channel = new AudioOutputChannel(
                            device,
                            ringBuffer,
                            fallbackFormat,
                            profile.BaseLatencyMs,
                            profile.WasapiLatencyMs,
                            target.Volume,
                            target.DelayMilliseconds,
                            profile.DriftWindowMs,
                            _masterVolume);
                    }
                    catch (Exception fallbackException)
                    {
                        throw new InvalidOperationException(
                            $"Preferred format failed ({primaryException.Message}); " +
                            $"fallback format also failed ({fallbackException.Message}).",
                            fallbackException);
                    }
                }

                // Ownership of device and channel has transferred to the engine.
                device = null;

                channel.Faulted += OnChannelFaulted;

                lock (_gate)
                {
                    _channels.Add(channel.DeviceId, channel);
                }

                channel = null;
            }
            catch (Exception ex)
            {
                device?.Dispose();
                channel?.Dispose();
                errors.Add($"{destinationInfo.FriendlyName}: {ex.Message}");
            }
        }

        List<AudioOutputChannel> channels;
        lock (_gate)
        {
            channels = _channels.Values.ToList();
        }

        // All destination readers start at the same capture-stream frame. Without this
        // anchor, each render thread primes on a different wake-up and the copies can
        // acquire a small, device-dependent offset before any user delay is applied.
        var sessionStartFrame = ringBuffer.WriteFrameCount;
        foreach (var channel in channels)
        {
            channel.StartAtFrame(sessionStartFrame);
        }

        foreach (var channel in channels)
        {
            try
            {
                channel.Start();
            }
            catch (Exception ex)
            {
                errors.Add($"{channel.DeviceName}: playback could not start: {ex.Message}");
                RemoveAndDisposeChannel(channel, unsubscribed: false);
            }
        }

        int activeCount;
        lock (_gate)
        {
            activeCount = _channels.Count;
        }

        if (activeCount == 0)
        {
            capture.SamplesAvailable -= OnSamplesAvailable;
            capture.Faulted -= OnCaptureFaulted;
            capture.Dispose();

            lock (_gate)
            {
                _capture = null;
                _ringBuffer = null;
                _sourceDeviceId = null;
                _sourceDeviceName = null;
            }

            var message = errors.Count == 0
                ? "No output devices could be started."
                : $"No output devices could be started. {string.Join(" ", errors)}";

            return new RoutingStartResult(false, message, Array.Empty<AudioChannelStatus>(), errors);
        }

        // Give event-driven WasapiOut threads time to enter their read loops before
        // capture begins. This is startup-only; it does not add ongoing latency.
        Thread.Sleep(profile.WarmupMs);

        try
        {
            capture.Start();
        }
        catch (Exception ex)
        {
            errors.Add($"Loopback capture failed to start: {ex.Message}");
            return new RoutingStartResult(false, string.Join(" ", errors), Array.Empty<AudioChannelStatus>(), errors);
        }

        // A playback failure can be raised asynchronously while the capture starts.
        lock (_gate)
        {
            activeCount = _channels.Count;
        }

        if (activeCount == 0)
        {
            errors.Add("All output devices stopped during startup.");
            return new RoutingStartResult(false, string.Join(" ", errors), Array.Empty<AudioChannelStatus>(), errors);
        }

        var summary = errors.Count == 0
            ? $"Loopback capture from \"{sourceInfo.FriendlyName}\" is playing on {activeCount} additional output(s)."
            : $"Routing started on {activeCount} output(s) with {errors.Count} device error(s): {string.Join(" ", errors)}";

        return new RoutingStartResult(
            true,
            summary,
            GetChannelStatuses(),
            errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Watches per-destination underrun/dropped-frame counters while routing is
    /// running. Destinations that keep starving at their current buffer profile are
    /// rebuilt with the next more robust profile (Low latency → Balanced → Stable).
    /// The rebuild keeps the same shared ring buffer, per-device volume/delay, and
    /// re-anchors the reader at the current capture frame.
    /// </summary>
    private async Task MonitorStabilityLoop(long generation)
    {
        while (true)
        {
            await Task.Delay(StabilityMonitor.TickPeriodMilliseconds).ConfigureAwait(true);

            if (_disposed || generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            AudioRoutingState state;
            lock (_gate)
            {
                state = _state;
            }

            if (state != AudioRoutingState.Running)
            {
                return;
            }

            if (_syncTestActive)
            {
                // Buffer-profile rebuilds would reset test isolation and move the
                // content timing the measurement is relying on. Resume monitoring
                // after the test releases the channels.
                continue;
            }

            IReadOnlyList<AudioChannelStatus> statuses;
            try
            {
                statuses = GetChannelStatuses();
            }
            catch (Exception)
            {
                // A teardown may race the snapshot; the next tick re-checks state.
                return;
            }

            foreach (var status in statuses)
            {
                if (status.State != AudioChannelState.Playing)
                {
                    continue;
                }

                if (!_stabilityMonitor.Evaluate(status.DeviceId, status.UnderrunCount, status.DroppedFrameCount))
                {
                    continue;
                }

                var currentProfile = BufferProfile.All.FirstOrDefault(candidate =>
                    candidate.BaseLatencyMs == status.BaseLatencyMilliseconds);
                var escalated = currentProfile?.Escalate();
                if (currentProfile is null || escalated is null)
                {
                    // Already the most robust profile; leave the channel alone.
                    continue;
                }

                try
                {
                    await RebuildChannelWithProfile(
                        status.DeviceId,
                        escalated,
                        currentProfile,
                        generation).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Volatile.Write(
                        ref _lastError,
                        $"Stability fallback failed for \"{status.DeviceName}\": {ex.Message}");
                }
            }
        }
    }

    private async Task RebuildChannelWithProfile(
        string deviceId,
        BufferProfile escalatedProfile,
        BufferProfile fromProfile,
        long generation)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed ||
                _syncTestActive ||
                generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            AudioRoutingState state;
            AudioOutputChannel? previous;
            AudioRingBuffer? ringBuffer;
            float volume;
            int delayMilliseconds;
            int sharedBaseLatencyMilliseconds = escalatedProfile.BaseLatencyMs;
            List<AudioOutputChannel> otherChannels;

            lock (_gate)
            {
                state = _state;
                ringBuffer = _ringBuffer;
                if (state != AudioRoutingState.Running ||
                    ringBuffer is null ||
                    !_channels.TryGetValue(deviceId, out previous) ||
                    previous is null)
                {
                    return;
                }

                volume = previous.Volume;
                delayMilliseconds = previous.DelayMilliseconds;
                otherChannels = _channels.Values
                    .Where(channel => !ReferenceEquals(channel, previous))
                    .ToList();

                foreach (var channel in otherChannels)
                {
                    sharedBaseLatencyMilliseconds = Math.Max(
                        sharedBaseLatencyMilliseconds,
                        channel.BaseLatencyMilliseconds);
                }
            }

            var status = previous.GetStatus();
            AudioOutputChannel? replacement = null;
            MMDevice? replacementDevice = null;

            try
            {
                replacementDevice = OpenRenderDevice(deviceId);

                using var audioClient = replacementDevice.AudioClient;
                var mixFormat = audioClient.MixFormat;
                var targetFormat = AudioFormatHelper.GetTargetOutputFormat(mixFormat);

                replacement = new AudioOutputChannel(
                    replacementDevice,
                    ringBuffer,
                    targetFormat,
                    sharedBaseLatencyMilliseconds,
                    escalatedProfile.WasapiLatencyMs,
                    volume,
                    delayMilliseconds,
                    escalatedProfile.DriftWindowMs,
                    _masterVolume);

                // Preserve the shared base latency across every destination. If one
                // endpoint needs a deeper buffer, all readers use the same deeper lag;
                // otherwise the fallback itself would create a new echo.
                foreach (var channel in otherChannels)
                {
                    if (channel.BaseLatencyMilliseconds < sharedBaseLatencyMilliseconds)
                    {
                        channel.SetBaseLatencyMilliseconds(sharedBaseLatencyMilliseconds);
                    }
                }

                replacement.TestGain = previous.TestGain;

                // The capture stream has moved on since the previous reader stopped;
                // anchor the new reader at the newest frame so it never replays audio.
                replacement.StartAtFrame(ringBuffer.WriteFrameCount);
                replacement.Faulted += OnChannelFaulted;
                replacement.Start();
            }
            catch (Exception)
            {
                replacement?.Dispose();
                replacement = null;
                replacementDevice?.Dispose();
                replacementDevice = null;

                // Keep the old channel running; stability monitoring can try again
                // (or the session continues degraded rather than losing an output).
                Volatile.Write(
                    ref _lastError,
                    $"Stability fallback for \"{status.DeviceName}\" failed; " +
                    "the previous profile stays active.");
                return;
            }

            lock (_gate)
            {
                if (_disposed || generation != Volatile.Read(ref _sessionGeneration))
                {
                    return;
                }

                _channels[deviceId] = replacement;
            }

            // Only now that the replacement is registered can the previous channel go.
            _stabilityMonitor.Reset(deviceId);
            previous.Faulted -= OnChannelFaulted;
            previous.Stop();
            previous.Dispose();

            StabilityFallback?.Invoke(
                this,
                new StabilityFallbackEventArgs(
                    deviceId,
                    status.DeviceName,
                    fromProfile,
                    escalatedProfile,
                    status.UnderrunCount,
                    status.DroppedFrameCount));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private const int SyncTrialCount = 3;
    private const double SyncScheduleLeadSeconds = 0.35;

    // The expected arrival time only covers the schedule lead, jitter lag, and
    // configured delay. WASAPI endpoint buffering (up to ~200 ms), Bluetooth codec
    // latency, acoustic travel, and microphone capture latency all push the real
    // arrival later, so the search window and capture tail carry extra headroom.
    // The "before" margin stays modest so a previous trial's chirp cannot leak into
    // the next search window; trials are spaced far enough apart that this holds.
    private const double SyncCaptureTailSeconds = 0.90;
    private const double SyncMicSearchBeforeSeconds = 0.50;
    private const double SyncMicSearchAfterSeconds = 1.10;
    private const float SyncTestSignalVolume = 0.55f;

    /// <summary>
    /// Measures the real arrival times of every routed destination plus the loopback
    /// source, then computes delays that align all destination copies to the latest
    /// reliable path. Routing must be running: the measurement deliberately uses the
    /// live ring buffer, output pipelines, configured delays, and buffer profile.
    ///
    /// A linear chirp is mixed into the shared content stream at a known future frame
    /// for each destination. Only that destination's test gain is open, so the
    /// microphone records one path at a time. The source chirp is played natively
    /// through the source endpoint and detected in the same loopback content stream.
    /// All arrival times are compared against their known content times, so the
    /// arbitrary microphone recording origin and capture scheduling latency cancel.
    /// </summary>
    public SyncCalibrationRunResult RunAutoSync(
        IReadOnlyList<AudioOutputTarget> targets,
        string? referenceDeviceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            return SyncCalibrationRunResult.Failed(
                "Auto Sync needs at least one routed destination in addition to the source.");
        }

        if (!_syncGate.Wait(0))
        {
            return SyncCalibrationRunResult.Failed("A synchronization test is already running.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref _syncCancellation, linkedCancellation, null) is not null)
        {
            _syncGate.Release();
            return SyncCalibrationRunResult.Failed("A synchronization test is already running.");
        }

        try
        {
            AudioRingBuffer? ringBuffer;
            string? sourceDeviceId;
            string? sourceDeviceName;
            List<AudioOutputChannel> channels;

            lock (_gate)
            {
                ringBuffer = _ringBuffer;
                sourceDeviceId = _sourceDeviceId;
                sourceDeviceName = _sourceDeviceName;
                channels = _channels.Values.ToList();
            }

            if (_state != AudioRoutingState.Running || ringBuffer is null ||
                string.IsNullOrWhiteSpace(sourceDeviceId))
            {
                return SyncCalibrationRunResult.Failed(
                    "Start routing before running Auto Sync; the measurement must use the live routing path.");
            }

            var channelById = channels.ToDictionary(
                channel => channel.DeviceId,
                StringComparer.OrdinalIgnoreCase);
            var targetsById = new Dictionary<string, AudioOutputTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                if (!channelById.ContainsKey(target.DeviceId))
                {
                    return SyncCalibrationRunResult.Failed(
                        $"The destination \"{target.DeviceId}\" is not active in the running session.");
                }

                targetsById[target.DeviceId] = target;
            }

            using var microphone = new MicrophoneRecorder();
            if (!microphone.Start(out var microphoneError))
            {
                return SyncCalibrationRunResult.Failed(microphoneError);
            }

            _syncTestActive = true;
            var measurements = new List<SyncSpeakerMeasurement>();
            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                // Destinations first. Each destination is drained while muted before it
                // is opened, so delayed copies of earlier test chirps cannot leak into
                // this measurement window.
                foreach (var target in targets)
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    EnsureSyncSession(ringBuffer, sourceDeviceId, channelById, target.DeviceId);

                    var channel = channelById[target.DeviceId];
                    var configuredDelay = channel.DelayMilliseconds;
                    var baseLatency = channel.BaseLatencyMilliseconds;

                    SetTestGains(channelById.Values, activeDeviceId: null);
                    DrainPipeline(channel, linkedCancellation.Token);
                    EnsureSyncSession(ringBuffer, sourceDeviceId, channelById, target.DeviceId);

                    SetTestGains(channelById.Values, target.DeviceId);
                    WaitForMilliseconds(80, linkedCancellation.Token);

                    var trials = MeasureDestinationTrials(
                        ringBuffer,
                        microphone,
                        channel,
                        configuredDelay,
                        baseLatency,
                        linkedCancellation.Token);
                    var summary = SyncCalibrationMath.SummarizeTrials(trials);

                    measurements.Add(new SyncSpeakerMeasurement(
                        channel.DeviceId,
                        channel.DeviceName,
                        IsSource: false,
                        configuredDelay,
                        summary.ArrivalOffsetMilliseconds,
                        summary.ConfidencePercent,
                        summary.Reliable,
                        summary.FailureReason));

                    // Keep the destination muted again before the next one is opened.
                    SetTestGains(channelById.Values, activeDeviceId: null);
                }

                // Source last. All destination test gains stay closed, so the microphone
                // hears only the native source playback.
                linkedCancellation.Token.ThrowIfCancellationRequested();
                SetTestGains(channelById.Values, activeDeviceId: null);
                DrainAllPipelines(channelById.Values, linkedCancellation.Token);
                EnsureSyncSession(ringBuffer, sourceDeviceId, channelById, requiredDeviceId: null);

                var sourceTrials = MeasureSourceTrials(
                    ringBuffer,
                    microphone,
                    sourceDeviceId,
                    linkedCancellation.Token);
                var sourceSummary = SyncCalibrationMath.SummarizeTrials(sourceTrials);

                measurements.Insert(0, new SyncSpeakerMeasurement(
                    sourceDeviceId,
                    sourceDeviceName ?? "Loopback source",
                    IsSource: true,
                    ConfiguredDelayMilliseconds: 0,
                    sourceSummary.ArrivalOffsetMilliseconds,
                    sourceSummary.ConfidencePercent,
                    sourceSummary.Reliable,
                    sourceSummary.FailureReason));

                // Let the last source chirp leave every destination pipeline while the
                // test gains are closed. Otherwise restoring gains could reveal a late
                // chirp after calibration has finished.
                DrainAllPipelines(channelById.Values, linkedCancellation.Token);

                var plan = SyncCalibrationMath.Plan(measurements, referenceDeviceId);
                return new SyncCalibrationRunResult(
                    plan.Success,
                    plan.Message,
                    plan,
                    measurements,
                    startedAt);
            }
            catch (OperationCanceledException)
            {
                return SyncCalibrationRunResult.Failed(
                    "Auto Sync was cancelled. No delays were changed.");
            }
            catch (Exception ex)
            {
                return SyncCalibrationRunResult.Failed(
                    $"Auto Sync failed before a complete measurement was available: {ex.Message}");
            }
            finally
            {
                _syncTestActive = false;
                RestoreTestGains(channelById.Values);
                microphone.Stop();
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _syncCancellation, null, linkedCancellation);
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Plays a short burst train natively through the loopback source while routing is
    /// running. Every destination receives that same signal through the real shared
    /// ring buffer with its configured delay and buffer profile. The user listens for
    /// flam/echo and adjusts the per-device delays live; this is a listening aid, not a
    /// measurement.
    /// </summary>
    public void PlayManualSyncTest(CancellationToken cancellationToken)
    {
        if (!_syncGate.Wait(0))
        {
            throw new InvalidOperationException("A synchronization test is already running.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref _syncCancellation, linkedCancellation, null) is not null)
        {
            _syncGate.Release();
            throw new InvalidOperationException("A synchronization test is already running.");
        }

        try
        {
            string? sourceDeviceId;
            List<AudioOutputChannel> channels;

            lock (_gate)
            {
                sourceDeviceId = _sourceDeviceId;
                channels = _channels.Values.ToList();
            }

            if (_state != AudioRoutingState.Running || string.IsNullOrWhiteSpace(sourceDeviceId) ||
                channels.Count == 0)
            {
                throw new InvalidOperationException(
                    "Start routing before playing the live sync test.");
            }

            _syncTestActive = true;
            RestoreTestGains(channels);

            var sampleRate = 48_000;
            var burstTrain = SyncTestSignal.CreateManualBurstTrain(
                sampleRate,
                bursts: 12,
                intervalSeconds: 0.7);

            EndpointSignalPlayer.Play(
                sourceDeviceId,
                burstTrain,
                sampleRate,
                SyncTestSignalVolume,
                linkedCancellation.Token);
        }
        finally
        {
            _syncTestActive = false;
            Interlocked.CompareExchange(ref _syncCancellation, null, linkedCancellation);
            _syncGate.Release();
        }
    }

    /// <summary>Cancels a running Auto Sync or manual sync test. Safe to call from any thread.</summary>
    public void CancelSyncTest()
    {
        try
        {
            _syncCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The test finished between the read and the cancel.
        }
    }

    private List<SyncTimingTrial> MeasureDestinationTrials(
        AudioRingBuffer ringBuffer,
        MicrophoneRecorder microphone,
        AudioOutputChannel channel,
        int configuredDelayMilliseconds,
        int baseLatencyMilliseconds,
        CancellationToken cancellationToken)
    {
        var chirp = SyncTestSignal.CreateChirp(ringBuffer.SampleRate);
        var leadFrames = (int)Math.Round(ringBuffer.SampleRate * SyncScheduleLeadSeconds);
        var trials = new List<SyncTimingTrial>(SyncTrialCount);

        for (var trial = 0; trial < SyncTrialCount; trial++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (channel.DelayMilliseconds != configuredDelayMilliseconds ||
                channel.BaseLatencyMilliseconds != baseLatencyMilliseconds)
            {
                throw new InvalidOperationException(
                    "The device delay or buffer profile changed during Auto Sync. No delays were changed.");
            }

            var microphoneStart = microphone.FramesRecorded;
            var ringStart = ringBuffer.WriteFrameCount;
            var scheduleFrame = ringStart + leadFrames;

            ringBuffer.ScheduleSignal(scheduleFrame, chirp);

            var expectedSeconds =
                SyncScheduleLeadSeconds +
                (configuredDelayMilliseconds + baseLatencyMilliseconds) / 1000.0;
            var waitMilliseconds =
                (int)Math.Ceiling(expectedSeconds * 1000.0) +
                (int)Math.Round(SyncMicSearchAfterSeconds * 1000.0) +
                250;
            WaitForMilliseconds(waitMilliseconds, cancellationToken);

            var microphoneEnd = microphone.FramesRecorded;
            var searchStart = microphoneStart +
                              Math.Max(
                                  0,
                                  (long)Math.Round(
                                      (expectedSeconds - SyncMicSearchBeforeSeconds) *
                                      microphone.SampleRate));
            var searchLength = (int)Math.Round(
                (SyncMicSearchBeforeSeconds +
                 SyncMicSearchAfterSeconds +
                 SyncTestSignal.ChirpSeconds) *
                microphone.SampleRate);

            var result = FindMicrophoneChirp(
                microphone,
                searchStart,
                searchLength,
                microphoneEnd,
                cancellationToken);

            if (!result.Found)
            {
                trials.Add(new SyncTimingTrial(
                    0,
                    result.ConfidencePercent,
                    Found: false,
                    result.Correlation,
                    result.PeakToSidelobeRatio,
                    result.CoherentSignalToNoiseDb));
                continue;
            }

            var arrivalSeconds =
                (searchStart + result.LagSeconds * microphone.SampleRate) /
                microphone.SampleRate;
            var arrivalOffsetSeconds = arrivalSeconds - scheduleFrame / (double)ringBuffer.SampleRate;

            trials.Add(new SyncTimingTrial(
                arrivalOffsetSeconds,
                result.ConfidencePercent,
                Found: true,
                result.Correlation,
                result.PeakToSidelobeRatio,
                result.CoherentSignalToNoiseDb));
        }

        return trials;
    }

    private List<SyncTimingTrial> MeasureSourceTrials(
        AudioRingBuffer ringBuffer,
        MicrophoneRecorder microphone,
        string sourceDeviceId,
        CancellationToken cancellationToken)
    {
        var chirp = SyncTestSignal.CreateChirp(ringBuffer.SampleRate);
        var trials = new List<SyncTimingTrial>(SyncTrialCount);

        for (var trial = 0; trial < SyncTrialCount; trial++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var microphoneStart = microphone.FramesRecorded;
            var ringStart = ringBuffer.WriteFrameCount;

            EndpointSignalPlayer.Play(
                sourceDeviceId,
                chirp,
                ringBuffer.SampleRate,
                SyncTestSignalVolume,
                cancellationToken);

            WaitForMilliseconds(
                (int)Math.Round(SyncCaptureTailSeconds * 1000.0),
                cancellationToken);

            var ringEnd = ringBuffer.WriteFrameCount;
            var ringFrames = (int)Math.Min(int.MaxValue, ringEnd - ringStart);
            if (ringFrames <= 0)
            {
                trials.Add(new SyncTimingTrial(0, 0, false, 0, 0, 0));
                continue;
            }

            var ringSamples = new float[ringFrames * AudioRingBuffer.Channels];
            if (!ringBuffer.TryCopyFrames(ringStart, ringFrames, ringSamples))
            {
                trials.Add(new SyncTimingTrial(0, 0, false, 0, 0, 0));
                continue;
            }

            var ringMono = SyncTestSignal.ToMono(ringSamples);
            var ringResult = SyncSignalAnalyzer.FindChirp(
                ringMono,
                ringBuffer.SampleRate,
                0,
                ringMono.Length);

            if (!ringResult.Found)
            {
                trials.Add(new SyncTimingTrial(
                    0,
                    ringResult.ConfidencePercent,
                    Found: false,
                    ringResult.Correlation,
                    ringResult.PeakToSidelobeRatio,
                    ringResult.CoherentSignalToNoiseDb));
                continue;
            }

            var sourceFrame = ringStart +
                              (long)Math.Round(ringResult.LagSeconds * ringBuffer.SampleRate);
            var expectedSeconds = (sourceFrame - ringStart) / (double)ringBuffer.SampleRate;
            var microphoneEnd = microphone.FramesRecorded;
            var searchStart = microphoneStart +
                              Math.Max(
                                  0,
                                  (long)Math.Round(
                                      (expectedSeconds - SyncMicSearchBeforeSeconds) *
                                      microphone.SampleRate));
            var searchLength = (int)Math.Round(
                (SyncMicSearchBeforeSeconds +
                 SyncMicSearchAfterSeconds +
                 SyncTestSignal.ChirpSeconds) *
                microphone.SampleRate);

            var microphoneResult = FindMicrophoneChirp(
                microphone,
                searchStart,
                searchLength,
                microphoneEnd,
                cancellationToken);

            if (!microphoneResult.Found)
            {
                trials.Add(new SyncTimingTrial(
                    0,
                    microphoneResult.ConfidencePercent,
                    Found: false,
                    microphoneResult.Correlation,
                    microphoneResult.PeakToSidelobeRatio,
                    microphoneResult.CoherentSignalToNoiseDb));
                continue;
            }

            var arrivalSeconds =
                (searchStart + microphoneResult.LagSeconds * microphone.SampleRate) /
                microphone.SampleRate;
            var arrivalOffsetSeconds = arrivalSeconds - sourceFrame / (double)ringBuffer.SampleRate;

            trials.Add(new SyncTimingTrial(
                arrivalOffsetSeconds,
                microphoneResult.ConfidencePercent,
                Found: true,
                microphoneResult.Correlation,
                microphoneResult.PeakToSidelobeRatio,
                microphoneResult.CoherentSignalToNoiseDb));
        }

        return trials;
    }

    private static SyncCorrelationResult FindMicrophoneChirp(
        MicrophoneRecorder microphone,
        long searchStartFrame,
        int searchLengthFrames,
        long microphoneEndFrame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sampleRate = microphone.SampleRate;
        if (sampleRate <= 0)
        {
            return SyncCorrelationResult.Failed("The microphone recording has no sample rate.");
        }

        var start = (int)Math.Clamp(searchStartFrame, 0, microphoneEndFrame);
        var end = (int)Math.Clamp(
            searchStartFrame + searchLengthFrames,
            start,
            microphoneEndFrame);
        var minimumFrames = (int)Math.Round(sampleRate * SyncTestSignal.ChirpSeconds);

        if (end - start < minimumFrames)
        {
            return SyncCorrelationResult.Failed(
                "The microphone did not record a complete test chirp window.");
        }

        var samples = microphone.SnapshotRange(start, end);
        var result = SyncSignalAnalyzer.FindChirp(samples, sampleRate, 0, samples.Length);
        if (!result.Found)
        {
            return result;
        }

        // Return the lag relative to the window start, in seconds, without losing the
        // sub-sample information the correlator already computed.
        return result;
    }

    private void EnsureSyncSession(
        AudioRingBuffer expectedRing,
        string expectedSourceDeviceId,
        IReadOnlyDictionary<string, AudioOutputChannel> expectedChannels,
        string? requiredDeviceId)
    {
        AudioRoutingState state;
        AudioRingBuffer? ring;
        string? sourceId;
        bool channelPresent;

        lock (_gate)
        {
            state = _state;
            ring = _ringBuffer;
            sourceId = _sourceDeviceId;
            channelPresent = requiredDeviceId is null ||
                             (_channels.TryGetValue(requiredDeviceId, out var current) &&
                              expectedChannels.TryGetValue(requiredDeviceId, out var expected) &&
                              ReferenceEquals(current, expected));
        }

        if (state != AudioRoutingState.Running ||
            !ReferenceEquals(ring, expectedRing) ||
            !string.Equals(sourceId, expectedSourceDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Routing stopped or changed during Auto Sync. No delays were changed.");
        }

        if (!channelPresent)
        {
            throw new InvalidOperationException(
                "A measured speaker disconnected or was rebuilt during Auto Sync. No delays were changed.");
        }
    }

    private static void SetTestGains(IEnumerable<AudioOutputChannel> channels, string? activeDeviceId)
    {
        foreach (var channel in channels)
        {
            try
            {
                channel.TestGain =
                    activeDeviceId is not null &&
                    string.Equals(channel.DeviceId, activeDeviceId, StringComparison.OrdinalIgnoreCase)
                        ? 1f
                        : 0f;
            }
            catch (Exception)
            {
                // A channel that was disposed concurrently is handled by the session check.
            }
        }
    }

    private static void RestoreTestGains(IEnumerable<AudioOutputChannel> channels)
    {
        foreach (var channel in channels)
        {
            try
            {
                channel.TestGain = 1f;
            }
            catch (Exception)
            {
                // Cleanup is best-effort; a disposed channel cannot produce audio.
            }
        }
    }

    private static void DrainPipeline(AudioOutputChannel channel, CancellationToken cancellationToken)
    {
        var waitMilliseconds =
            channel.BaseLatencyMilliseconds +
            channel.DelayMilliseconds +
            500;
        WaitForMilliseconds(waitMilliseconds, cancellationToken);
    }

    private static void DrainAllPipelines(
        IEnumerable<AudioOutputChannel> channels,
        CancellationToken cancellationToken)
    {
        var maximum = 0;
        foreach (var channel in channels)
        {
            maximum = Math.Max(
                maximum,
                channel.BaseLatencyMilliseconds + channel.DelayMilliseconds);
        }

        WaitForMilliseconds(maximum + 500, cancellationToken);
    }

    private static void WaitForMilliseconds(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private string? ValidateRequest(AudioRoutingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceDeviceId))
        {
            return "Choose a loopback source device.";
        }

        if (request.Outputs is null || request.Outputs.Count == 0)
        {
            return "Select at least one additional output device.";
        }

        if (request.Outputs.Count > MaxAdditionalOutputs)
        {
            return $"At most {MaxAdditionalOutputs} additional output devices can be routed " +
                   $"({MaxTotalAudibleDevices} audible speakers total, including the loopback source).";
        }

        var destinationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in request.Outputs)
        {
            if (string.IsNullOrWhiteSpace(target.DeviceId))
            {
                return "An output device is missing its identifier.";
            }

            if (string.Equals(target.DeviceId, request.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return "The loopback source cannot also be a destination. " +
                       "The source already plays through Windows, and routing it back to itself would create a feedback loop.";
            }

            if (!destinationIds.Add(target.DeviceId))
            {
                return "The same output device was selected more than once.";
            }

            if (!float.IsFinite(target.Volume) || target.Volume is < 0f or > 1f)
            {
                return "An output volume is outside the valid 0–100% range.";
            }

            if (target.DelayMilliseconds is < 0 or > 5_000)
            {
                return "An output delay is outside the valid 0–5000 ms range.";
            }
        }

        return null;
    }

    private static MMDevice OpenRenderDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);

        try
        {
            if (device.State != DeviceState.Active)
            {
                throw new InvalidOperationException("The output device is not active.");
            }

            if (device.DataFlow != DataFlow.Render)
            {
                throw new InvalidOperationException("The selected device is not a render endpoint.");
            }

            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private void OnSamplesAvailable(object? sender, AudioSamplesEventArgs e)
    {
        // Ignore a callback that was already in flight when a previous session was
        // torn down; its samples must not be written into a new session's ring buffer.
        var capture = Volatile.Read(ref _capture);
        if (capture is null || !ReferenceEquals(sender, capture))
        {
            return;
        }

        var ringBuffer = Volatile.Read(ref _ringBuffer);
        ringBuffer?.Write(e.Samples.Span);
    }

    private void OnCaptureFaulted(object? sender, LoopbackCaptureFaultEventArgs e)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        var message = e.Message;
        _ = Task.Run(() => StopWithFault(message, deviceId: null, generation));
    }

    private void OnChannelFaulted(object? sender, AudioChannelFaultedEventArgs e)
    {
        var failedChannel = sender as AudioOutputChannel;
        var generation = Volatile.Read(ref _sessionGeneration);
        var deviceId = e.DeviceId;
        var message = e.Message;
        _ = Task.Run(() => HandleChannelFault(failedChannel, deviceId, message, generation));
    }

    private void OnDeviceRemoved(object? sender, AudioDeviceRemovedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var generation = Volatile.Read(ref _sessionGeneration);
        bool sourceAffected;
        bool destinationAffected;
        string? sourceName;

        lock (_gate)
        {
            sourceAffected =
                _state is AudioRoutingState.Starting or AudioRoutingState.Running &&
                string.Equals(_sourceDeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase);

            destinationAffected = _channels.ContainsKey(e.DeviceId);
            sourceName = _sourceDeviceName;
        }

        if (sourceAffected)
        {
            var message = $"The loopback source device \"{sourceName}\" was disconnected.";
            _ = Task.Run(() => StopWithFault(message, e.DeviceId, generation));
        }
        else if (destinationAffected)
        {
            var message = "The output device was disconnected.";
            var deviceId = e.DeviceId;
            _ = Task.Run(() => HandleChannelFault(null, deviceId, message, generation));
        }
    }

    private void HandleChannelFault(
        AudioOutputChannel? failedChannel,
        string deviceId,
        string message,
        long generation)
    {
        if (generation != Volatile.Read(ref _sessionGeneration))
        {
            // A delayed fault from a previous routing session; never remove a channel
            // that belongs to the session that is currently running.
            return;
        }

        AudioOutputChannel? channel;

        lock (_gate)
        {
            if (generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            if (_state is AudioRoutingState.Stopped or
                AudioRoutingState.Stopping or
                AudioRoutingState.Faulted)
            {
                return;
            }

            if (failedChannel is not null &&
                (!_channels.TryGetValue(deviceId, out var current) ||
                 !ReferenceEquals(current, failedChannel)))
            {
                return;
            }

            _channels.Remove(deviceId, out channel);
        }

        if (channel is null)
        {
            return;
        }

        channel.Faulted -= OnChannelFaulted;
        channel.Dispose();

        bool anyChannelsRemain;
        lock (_gate)
        {
            anyChannelsRemain = _channels.Count > 0;
        }

        Volatile.Write(ref _lastError, message);

        if (!anyChannelsRemain)
        {
            StopWithFault(message, deviceId, generation);
            return;
        }

        RaiseFault(deviceId, message, isFatal: false);
    }

    private void StopWithFault(string message, string? deviceId, long? generation = null)
    {
        if (_disposed)
        {
            return;
        }

        if (generation is not null && generation != Volatile.Read(ref _sessionGeneration))
        {
            return;
        }

        var stopped = false;

        _lifecycle.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            if (generation is not null && generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            AudioRoutingState state;
            lock (_gate)
            {
                state = _state;
            }

            if (state is AudioRoutingState.Stopped or
                AudioRoutingState.Stopping or
                AudioRoutingState.Faulted)
            {
                return;
            }

            Volatile.Write(ref _lastError, message);
            SetState(AudioRoutingState.Stopping);
            CleanupResources();
            SetState(AudioRoutingState.Faulted);
            stopped = true;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (stopped)
        {
            RaiseFault(deviceId, message, isFatal: true);
        }
    }

    private void RemoveAndDisposeChannel(AudioOutputChannel channel, bool unsubscribed)
    {
        lock (_gate)
        {
            _channels.Remove(channel.DeviceId);
        }

        if (!unsubscribed)
        {
            channel.Faulted -= OnChannelFaulted;
        }

        channel.Dispose();
    }

    private void CleanupResources()
    {
        LoopbackCaptureService? capture;
        List<AudioOutputChannel> channels;

        lock (_gate)
        {
            capture = _capture;
            _capture = null;

            channels = _channels.Values.ToList();
            _channels.Clear();

            _ringBuffer = null;
            _sourceDeviceId = null;
            _sourceDeviceName = null;
        }

        if (capture is not null)
        {
            capture.SamplesAvailable -= OnSamplesAvailable;
            capture.Faulted -= OnCaptureFaulted;

            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, $"Loopback cleanup failed: {ex.Message}");
            }
        }

        foreach (var channel in channels)
        {
            channel.Faulted -= OnChannelFaulted;

            try
            {
                channel.Stop();
                channel.Dispose();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, $"Output cleanup failed: {ex.Message}");
            }
        }

        _masterVolume?.ClearSourceDevice();
    }

    private void SetState(AudioRoutingState state)
    {
        AudioRoutingState previous;

        lock (_gate)
        {
            previous = _state;
            _state = state;
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, new RoutingStateChangedEventArgs(state));
        }
    }

    private void OnMasterVolumeChanged(object? sender, float volume)
    {
        MasterVolumeChanged?.Invoke(this, volume);
    }

    private void OnMasterMuteChanged(object? sender, bool muted)
    {
        MasterMuteChanged?.Invoke(this, muted);
    }

    private void RaiseFault(string? deviceId, string message, bool isFatal)
    {
        FaultOccurred?.Invoke(this, new AudioRouteFaultEventArgs(deviceId, message, isFatal));
    }

    private AudioOutputChannel? FindChannel(string deviceId)
    {
        lock (_gate)
        {
            return _channels.TryGetValue(deviceId, out var channel) ? channel : null;
        }
    }

    private static RoutingStartResult Failed(string message)
    {
        return new RoutingStartResult(
            false,
            message,
            Array.Empty<AudioChannelStatus>(),
            new[] { message });
    }
}
