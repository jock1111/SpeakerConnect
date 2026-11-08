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
            CleanupResources();
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

        _lifecycle.Wait();
        try
        {
            _disposed = true;
            SetState(AudioRoutingState.Stopping);
            CleanupResources();
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
                generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            AudioRoutingState state;
            AudioOutputChannel? previous;
            AudioRingBuffer? ringBuffer;
            float volume;
            int delayMilliseconds;

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
                    escalatedProfile.BaseLatencyMs,
                    escalatedProfile.WasapiLatencyMs,
                    volume,
                    delayMilliseconds,
                    escalatedProfile.DriftWindowMs,
                    _masterVolume);

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

    private const float CalibrationImpulseVolume = 0.4f;
    private const double CalibrationPrefixSeconds = 0.20;
    private const double CalibrationBurstSeconds = 0.25;
    private const double CalibrationTailSeconds = 0.85;

    /// <summary>
    /// Renders one short calibration burst to every requested destination, one device
    /// at a time, while routing is stopped. The burst is generated here (not taken
    /// from the loopback source), so the user can compare when each speaker reproduces
    /// it and adjust the per-device delay sliders until the bursts line up.
    ///
    /// This is a manual aid only: it does not measure or correct acoustic latency.
    /// </summary>
    public IReadOnlyList<string> PlayCalibrationImpulse(IReadOnlyList<AudioOutputTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one output target is required.", nameof(targets));
        }

        var errors = new List<string>();

        _lifecycle.Wait();
        try
        {
            AudioRoutingState state;
            lock (_gate)
            {
                state = _state;
            }

            if (state is not AudioRoutingState.Stopped)
            {
                throw new InvalidOperationException(
                    "Calibration clicks are only available while routing is stopped.");
            }

            foreach (var target in targets)
            {
                try
                {
                    PlayCalibrationImpulse(target);
                }
                catch (Exception ex)
                {
                    errors.Add($"Calibration click on \"{target.DeviceId}\" failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        return errors;
    }

    private void PlayCalibrationImpulse(AudioOutputTarget target)
    {
        MMDevice? device = null;
        AudioOutputChannel? channel = null;
        Task? writer = null;

        try
        {
            device = OpenRenderDevice(target.DeviceId);

            using var audioClient = device.AudioClient;
            var targetFormat = AudioFormatHelper.GetTargetOutputFormat(audioClient.MixFormat);
            var ring = new AudioRingBuffer(targetFormat.SampleRate, TimeSpan.FromSeconds(3));
            var sampleRate = targetFormat.SampleRate;
            writer = Task.Run(() => StreamCalibrationContent(ring, sampleRate));

            channel = new AudioOutputChannel(
                device,
                ring,
                targetFormat,
                BufferProfile.Stable.BaseLatencyMs,
                BufferProfile.Stable.WasapiLatencyMs,
                CalibrationImpulseVolume,
                0,
                BufferProfile.Stable.DriftWindowMs);

            // The channel owns the device from here on.
            device = null;

            channel.Start();
            Thread.Sleep((int)((CalibrationPrefixSeconds + CalibrationBurstSeconds) * 1000) + 150);
            channel.Stop();
        }
        finally
        {
            try
            {
                channel?.Dispose();
            }
            catch (Exception)
            {
                // Continue with writer/device cleanup.
            }

            try
            {
                device?.Dispose();
            }
            catch (Exception)
            {
                // The device may already be gone.
            }

            try
            {
                writer?.Wait(2_000);
            }
            catch (Exception)
            {
                // The stream writer finishing late is harmless; the ring is local.
            }
        }
    }

    private static void StreamCalibrationContent(AudioRingBuffer ring, int sampleRate)
    {
        try
        {
            var prefixFrames = (int)Math.Round(sampleRate * CalibrationPrefixSeconds);
            var burstFrames = (int)Math.Round(sampleRate * CalibrationBurstSeconds);
            var tailFrames = (int)Math.Round(sampleRate * CalibrationTailSeconds);
            var totalFrames = prefixFrames + burstFrames + tailFrames;

            var content = new float[totalFrames * AudioRingBuffer.Channels];
            var burst = AudioCalibrationTone.CreateBurst(sampleRate, CalibrationBurstSeconds);
            Buffer.BlockCopy(
                burst,
                0,
                content,
                prefixFrames * AudioRingBuffer.Channels * sizeof(float),
                burst.Length * sizeof(float));

            var blockFrames = (int)Math.Round(sampleRate * AudioCalibrationTone.BlockSeconds);
            if (blockFrames <= 0 || prefixFrames % blockFrames != 0 || burstFrames % blockFrames != 0)
            {
                // Fall back to writing the whole clip at once; the jitter buffer will
                // trim to its target lag and play the tail of the content.
                ring.Write(content);
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var writtenFrames = 0;
            while (writtenFrames < totalFrames)
            {
                var frames = Math.Min(blockFrames, totalFrames - writtenFrames);
                ring.Write(content.AsSpan(
                    writtenFrames * AudioRingBuffer.Channels,
                    frames * AudioRingBuffer.Channels));
                writtenFrames += frames;

                var deadlineMilliseconds = writtenFrames * 1000.0 / sampleRate;
                while (stopwatch.Elapsed.TotalMilliseconds + 2 < deadlineMilliseconds)
                {
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception)
        {
            // A failed calibration stream only means no click; never crash the engine.
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
