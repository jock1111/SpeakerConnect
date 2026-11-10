using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeakerConnect.Models;

namespace SpeakerConnect.Audio;

public sealed class AudioChannelFaultedEventArgs : EventArgs
{
    public AudioChannelFaultedEventArgs(
        string deviceId,
        string deviceName,
        string message,
        Exception? exception)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Message = message;
        Exception = exception;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public string Message { get; }

    public Exception? Exception { get; }
}

/// <summary>
/// One destination endpoint. The channel owns the pull pipeline:
/// jitter buffer -> optional resampler -> software gain -> optional mono downmix ->
/// sample-format writer -> WasapiOut. Each channel has its own reader cursor on the
/// shared loopback ring buffer, which is what makes independent delays possible.
/// </summary>
public sealed class AudioOutputChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDevice _device;
    private readonly JitterBufferSampleProvider _jitterBuffer;
    private readonly GainSampleProvider _gain;
    private readonly SmoothGainSampleProvider _testGain;
    private readonly MasterVolumeManager? _masterVolume;
    private WasapiOut? _output;
    private volatile bool _expectedStop;
    private volatile bool _disposed;
    private int _state = (int)AudioChannelState.Stopped;
    private int _delayMilliseconds;
    private int _effectiveBaseLatencyMilliseconds;
    private float _volume;
    private volatile string? _error;
    private int _wasapiLatencyMilliseconds;

    public AudioOutputChannel(
        MMDevice device,
        AudioRingBuffer buffer,
        WaveFormat targetFormat,
        int baseLatencyMilliseconds,
        int wasapiLatencyMilliseconds,
        float volume,
        int delayMilliseconds,
        double driftWindowMilliseconds = 50,
        MasterVolumeManager? masterVolume = null,
        string? profileName = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(targetFormat);

        if (targetFormat.Channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(targetFormat), "Only mono or stereo output is supported.");
        }

        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        TargetFormat = targetFormat;
        _effectiveBaseLatencyMilliseconds = Math.Max(0, baseLatencyMilliseconds);
        RequestedWasapiLatencyMilliseconds = Math.Clamp(wasapiLatencyMilliseconds, 20, 200);
        ProfileName = profileName ?? BufferProfile.All
            .FirstOrDefault(profile => profile.BaseLatencyMs == BaseLatencyMilliseconds &&
                                       profile.WasapiLatencyMs == RequestedWasapiLatencyMilliseconds)
            ?.DisplayName ?? "Custom";

        _delayMilliseconds = Math.Clamp(delayMilliseconds, 0, 5_000);
        _volume = Math.Clamp(volume, 0f, 1f);
        _masterVolume = masterVolume;

        try
        {
            _jitterBuffer = new JitterBufferSampleProvider(
                buffer,
                BaseLatencyMilliseconds,
                _delayMilliseconds,
                driftWindowMilliseconds);

            ISampleProvider pipeline = _jitterBuffer;

            if (buffer.SampleRate != targetFormat.SampleRate)
            {
                pipeline = new WdlResamplingSampleProvider(pipeline, targetFormat.SampleRate);
            }

            // Gain combines per-device volume with master volume. When a master
            // volume manager is supplied, its ramped value is read on every pull
            // for smooth, click-free transitions.
            if (_masterVolume is not null)
            {
                _gain = new GainSampleProvider(pipeline, () => _masterVolume.GetRampedVolume(DeviceId));
            }
            else
            {
                _gain = new GainSampleProvider(pipeline) { Gain = _volume };
            }

            pipeline = _gain;

            // Test isolation gain is normally 1. During Auto Sync it mutes every
            // destination except the one being measured; the ramp avoids clicks.
            _testGain = new SmoothGainSampleProvider(pipeline);
            pipeline = _testGain;

            if (targetFormat.Channels == 1)
            {
                pipeline = new StereoChannelMapSampleProvider(pipeline, 1);
            }

            var waveProvider = new FloatToWaveProvider(pipeline, targetFormat);
            var (output, appliedLatencyMs) = CreateWasapiOut(device, waveProvider, RequestedWasapiLatencyMilliseconds);
            _output = output;
            _wasapiLatencyMilliseconds = appliedLatencyMs;
            _output.PlaybackStopped += OnPlaybackStopped;
        }
        catch
        {
            // Ownership only transfers after successful construction; the caller
            // remains responsible for disposing the MMDevice on failure.
            _output?.Dispose();
            _output = null;
            throw;
        }
    }

    private static (WasapiOut Output, int AppliedLatencyMs) CreateWasapiOut(
        MMDevice device,
        IWaveProvider waveProvider,
        int requestedLatencyMs)
    {
        Exception? firstFailure = null;
        var latencies = BufferProfile.GetWasapiLatencyCandidates(requestedLatencyMs);

        // Some Bluetooth and virtual endpoints reject event-driven shared-mode
        // initialization (or very small buffers) even though their mix format is
        // supported. Try event-driven at the requested size, then progressively more
        // permissive sizes and modes, before failing the whole destination.
        var attempts = new List<(bool UseEventSync, int LatencyMs)>();
        foreach (var latency in latencies)
        {
            attempts.Add((true, latency));
        }

        foreach (var latency in latencies)
        {
            attempts.Add((false, latency));
        }

        foreach (var (useEventSync, latencyMs) in attempts)
        {
            WasapiOut? output = null;
            try
            {
                output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync, latencyMs);
                output.Init(waveProvider);
                return (output, latencyMs);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;

                try
                {
                    output?.Dispose();
                }
                catch (Exception)
                {
                    // Preserve the initialization failure that caused the retry.
                }
            }
        }

        throw new InvalidOperationException(
            $"The endpoint rejected every WASAPI buffer/mode combination " +
            $"({string.Join(", ", latencies)} ms). Last error: {firstFailure?.Message}",
            firstFailure);
    }

    public event EventHandler<AudioChannelFaultedEventArgs>? Faulted;

    public string DeviceId { get; }

    public string DeviceName { get; }

    public WaveFormat TargetFormat { get; }

    /// <summary>Effective shared jitter-buffer lag configured for this channel.</summary>
    public int BaseLatencyMilliseconds => Volatile.Read(ref _effectiveBaseLatencyMilliseconds);

    /// <summary>
    /// Raises this channel's shared base latency when another destination had to
    /// fall back to a more robust profile. Keeping one common base latency across
    /// destinations prevents a profile change from introducing a new echo.
    /// </summary>
    public void SetBaseLatencyMilliseconds(int baseLatencyMilliseconds)
    {
        var clamped = Math.Max(0, baseLatencyMilliseconds);
        Volatile.Write(ref _effectiveBaseLatencyMilliseconds, clamped);
        _jitterBuffer.SetBaseLatencyMilliseconds(clamped);
    }

    /// <summary>
    /// Test-only isolation multiplier. 1 = normal playback, 0 = silent. The value
    /// is ramped inside the audio pipeline, so Auto Sync can mute other speakers
    /// without an audible click.
    /// </summary>
    public float TestGain
    {
        get => _testGain.Gain;
        set => _testGain.SetGain(value);
    }

    /// <summary>Display name of the buffer profile this channel was started with.</summary>
    public string ProfileName { get; }

    /// <summary>WASAPI buffer size requested for this channel.</summary>
    public int RequestedWasapiLatencyMilliseconds { get; }

    /// <summary>WASAPI buffer size the endpoint actually accepted.</summary>
    public int WasapiLatencyMilliseconds => Volatile.Read(ref _wasapiLatencyMilliseconds);

    public int DelayMilliseconds
    {
        get => Volatile.Read(ref _delayMilliseconds);
        set
        {
            var clamped = Math.Clamp(value, 0, 5_000);
            Volatile.Write(ref _delayMilliseconds, clamped);
            _jitterBuffer.DelayMilliseconds = clamped;
        }
    }

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            Volatile.Write(ref _volume, clamped);

            // When master volume is dynamic, the gain provider reads master volume
            // on every pull; per-device volume is combined inside the ramp callback.
            if (_masterVolume is null)
            {
                _gain.Gain = clamped;
            }
            else
            {
                _masterVolume.SetDeviceVolume(DeviceId, clamped);
            }
        }
    }

    public void Start()
    {
        WasapiOut output;

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioOutputChannel));
            }

            if (Volatile.Read(ref _state) == (int)AudioChannelState.Playing)
            {
                return;
            }

            if (_output is null)
            {
                throw new InvalidOperationException("The output channel has already been disposed.");
            }

            output = _output;
            _expectedStop = false;
            Volatile.Write(ref _state, (int)AudioChannelState.Playing);
        }

        try
        {
            output.Play();
        }
        catch (Exception ex)
        {
            _error = $"Playback could not start: {ex.Message}";
            Volatile.Write(ref _state, (int)AudioChannelState.Faulted);
            throw;
        }
    }

    /// <summary>
    /// Sets the shared capture-stream frame that this destination should treat as its
    /// first sample. Called before the capture starts, when the ring is still empty.
    /// </summary>
    public void StartAtFrame(long startFrame)
    {
        _jitterBuffer.StartAtFrame(startFrame);
    }

    public void Stop()
    {
        WasapiOut? output;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _expectedStop = true;
            output = _output;
            Volatile.Write(ref _state, (int)AudioChannelState.Stopped);
        }

        if (output is not null)
        {
            try
            {
                output.Stop();
            }
            catch (Exception ex)
            {
                _error = $"Stop failed: {ex.Message}";
            }
        }
    }

    public AudioChannelStatus GetStatus()
    {
        return new AudioChannelStatus(
            DeviceId,
            DeviceName,
            (AudioChannelState)Volatile.Read(ref _state),
            Volume,
            DelayMilliseconds,
            _jitterBuffer.BufferedMilliseconds,
            _jitterBuffer.UnderrunCount,
            _jitterBuffer.DroppedFrameCount,
            _error,
            ProfileName,
            BaseLatencyMilliseconds,
            WasapiLatencyMilliseconds);
    }

    public void Dispose()
    {
        WasapiOut? output;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _expectedStop = true;
            Volatile.Write(ref _state, (int)AudioChannelState.Stopped);
            output = _output;
            _output = null;
        }

        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // The endpoint may already have been removed.
            }

            try
            {
                output.Dispose();
            }
            catch (Exception)
            {
                // Cleanup must continue so the device handle is released.
            }
        }

        _device.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_expectedStop || _disposed)
        {
            return;
        }

        var message = e.Exception is null
            ? "Playback stopped unexpectedly. The endpoint may have been disconnected."
            : $"Playback stopped: {e.Exception.Message}";

        _error = message;
        Volatile.Write(ref _state, (int)AudioChannelState.Faulted);

        Faulted?.Invoke(
            this,
            new AudioChannelFaultedEventArgs(DeviceId, DeviceName, message, e.Exception));
    }
}
