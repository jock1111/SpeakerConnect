using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpeakerConnect.Audio;

public sealed class AudioSamplesEventArgs : EventArgs
{
    private readonly float[] _samples;

    public AudioSamplesEventArgs(float[] samples, int frameCount)
    {
        _samples = samples;
        FrameCount = frameCount;
    }

    /// <summary>
    /// Interleaved stereo float samples. The memory is only valid for the duration of
    /// the event handler; consumers must copy it if they need to retain it.
    /// </summary>
    public ReadOnlyMemory<float> Samples => _samples.AsMemory(0, FrameCount * AudioRingBuffer.Channels);

    public int FrameCount { get; }
}

public sealed class LoopbackCaptureFaultEventArgs : EventArgs
{
    public LoopbackCaptureFaultEventArgs(string message, Exception? exception)
    {
        Message = message;
        Exception = exception;
    }

    public string Message { get; }

    public Exception? Exception { get; }
}

/// <summary>
/// Owns the single WASAPI loopback capture stream. Loopback captures the mix that
/// Windows is rendering to the selected output endpoint; that endpoint continues to
/// play normally. The captured stream is converted to stereo float once and fanned
/// out by the router.
/// </summary>
public sealed class LoopbackCaptureService : IDisposable
{
    private readonly object _gate = new();
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private WaveFormatToFloatConverter? _converter;
    private float[] _sampleBuffer = Array.Empty<float>();
    private bool _prepared;
    private bool _recording;
    private bool _disposed;
    private volatile bool _expectedStop;
    private string _deviceName = string.Empty;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public event EventHandler<LoopbackCaptureFaultEventArgs>? Faulted;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _recording;
            }
        }
    }

    public string SourceDeviceName
    {
        get
        {
            lock (_gate)
            {
                return _deviceName;
            }
        }
    }

    /// <summary>
    /// Opens the render endpoint and prepares a loopback capture stream. The returned
    /// format is the endpoint mix format that will be delivered by <see cref="SamplesAvailable"/>.
    /// </summary>
    public WaveFormat Prepare(string sourceDeviceId)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceId))
        {
            throw new ArgumentException("A source render device is required.", nameof(sourceDeviceId));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LoopbackCaptureService));
            }
        }

        Stop();

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(sourceDeviceId);
        WasapiLoopbackCapture? capture = null;

        try
        {
            if (device.State != DeviceState.Active)
            {
                throw new InvalidOperationException("The loopback source device is not active.");
            }

            if (device.DataFlow != DataFlow.Render)
            {
                throw new InvalidOperationException("The loopback source must be a render (output) device.");
            }

            var deviceName = device.FriendlyName;
            capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            var converter = new WaveFormatToFloatConverter(format);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            lock (_gate)
            {
                _device = device;
                _capture = capture;
                _converter = converter;
                _deviceName = deviceName;
                _sampleBuffer = Array.Empty<float>();
                _expectedStop = false;
                _prepared = true;
            }

            return format;
        }
        catch
        {
            capture?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Start()
    {
        WasapiLoopbackCapture capture;

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LoopbackCaptureService));
            }

            if (!_prepared || _capture is null)
            {
                throw new InvalidOperationException("Loopback capture has not been prepared.");
            }

            if (_recording)
            {
                return;
            }

            capture = _capture;
            _expectedStop = false;
            _recording = true;
        }

        try
        {
            capture.StartRecording();
        }
        catch
        {
            lock (_gate)
            {
                _recording = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Stops and joins the capture thread. Safe to call more than once.
    /// </summary>
    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        MMDevice? device;

        lock (_gate)
        {
            capture = _capture;
            device = _device;
            _expectedStop = true;
            _recording = false;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                // The device may already be gone; Dispose below still releases resources.
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception)
            {
                // Do not let a failing endpoint prevent the router from cleaning up.
            }
        }

        device?.Dispose();

        lock (_gate)
        {
            _capture = null;
            _device = null;
            _converter = null;
            _prepared = false;
            _sampleBuffer = Array.Empty<float>();
            _deviceName = string.Empty;
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

        Stop();
        GC.SuppressFinalize(this);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var converter = _converter;
        if (converter is null)
        {
            return;
        }

        try
        {
            var samples = _sampleBuffer;
            var frames = converter.Convert(e.Buffer, e.BytesRecorded, ref samples);
            _sampleBuffer = samples;

            if (frames > 0)
            {
                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(samples, frames));
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(
                this,
                new LoopbackCaptureFaultEventArgs(
                    $"Failed to process loopback audio: {ex.Message}",
                    ex));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_expectedStop)
        {
            return;
        }

        lock (_gate)
        {
            _recording = false;
        }

        var message = e.Exception is null
            ? "WASAPI loopback capture stopped unexpectedly."
            : $"WASAPI loopback capture stopped: {e.Exception.Message}";

        Faulted?.Invoke(this, new LoopbackCaptureFaultEventArgs(message, e.Exception));
    }
}
