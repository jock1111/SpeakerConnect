using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Continuous default-microphone recorder for synchronization measurements.
/// Audio is stored as mono float frames with a sample count that can be sampled
/// while the test sequence is running. The absolute recording start time is never
/// used as a timing reference; it cancels when arrival times are compared.
/// </summary>
public sealed class MicrophoneRecorder : IDisposable
{
    private const int MaximumSeconds = 90;

    private readonly object _gate = new();
    private readonly List<float> _mono = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private WaveFormatToFloatConverter? _converter;
    private float[] _work = Array.Empty<float>();
    private bool _recording;
    private bool _disposed;
    private int _sampleRate;

    public int SampleRate
    {
        get
        {
            lock (_gate)
            {
                return _sampleRate;
            }
        }
    }

    public long FramesRecorded
    {
        get
        {
            lock (_gate)
            {
                return _mono.Count;
            }
        }
    }

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

    public bool Start(out string error)
    {
        error = string.Empty;
        lock (_gate)
        {
            if (_disposed)
            {
                error = "The microphone recorder has already been disposed.";
                return false;
            }

            if (_recording)
            {
                return true;
            }
        }

        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            WasapiCapture? capture = null;

            try
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                if (device.State != DeviceState.Active)
                {
                    error = "The default microphone is not active. Select/connect a microphone and retry.";
                    device.Dispose();
                    enumerator.Dispose();
                    return false;
                }

                capture = new WasapiCapture(device);
                var converter = new WaveFormatToFloatConverter(capture.WaveFormat);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;

                lock (_gate)
                {
                    _enumerator = enumerator;
                    _device = device;
                    _capture = capture;
                    _converter = converter;
                    _work = Array.Empty<float>();
                    _mono.Clear();
                    _sampleRate = converter.SourceFormat.SampleRate;
                    _recording = true;
                }

                capture.StartRecording();
                return true;
            }
            catch
            {
                capture?.Dispose();
                device?.Dispose();
                enumerator.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            error = $"Could not start the microphone: {ex.Message}";
            lock (_gate)
            {
                _recording = false;
                _capture = null;
                _converter = null;
                _sampleRate = 0;
            }

            return false;
        }
    }

    /// <summary>Returns a copy of the recorded mono samples and its sample rate.</summary>
    public (float[] Samples, int SampleRate) Snapshot()
    {
        lock (_gate)
        {
            return (_mono.ToArray(), _sampleRate);
        }
    }

    public float[] SnapshotRange(long startFrame, long endFrame)
    {
        lock (_gate)
        {
            var start = (int)Math.Clamp(startFrame, 0, _mono.Count);
            var end = (int)Math.Clamp(endFrame, start, _mono.Count);
            var result = new float[end - start];
            _mono.CopyTo(start, result, 0, result.Length);
            return result;
        }
    }

    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        MMDeviceEnumerator? enumerator;

        lock (_gate)
        {
            if (!_recording && _capture is null)
            {
                return;
            }

            _recording = false;
            capture = _capture;
            device = _device;
            enumerator = _enumerator;
            _capture = null;
            _device = null;
            _enumerator = null;
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
                // The endpoint may already be gone.
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception)
            {
                // Cleanup is best-effort.
            }
        }

        device?.Dispose();
        enumerator?.Dispose();
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
            var samples = _work;
            var frames = converter.Convert(e.Buffer, e.BytesRecorded, ref samples);
            _work = samples;
            if (frames <= 0)
            {
                return;
            }

            var sampleRate = converter.SourceFormat.SampleRate;
            var maximumFrames = Math.Max(1, sampleRate * MaximumSeconds);
            lock (_gate)
            {
                if (!_recording || _mono.Count >= maximumFrames)
                {
                    return;
                }

                var channels = AudioRingBuffer.Channels;
                var framesToCopy = Math.Min(frames, maximumFrames - _mono.Count);
                for (var frame = 0; frame < framesToCopy; frame++)
                {
                    var index = frame * channels;
                    _mono.Add((samples[index] + samples[index + 1]) * 0.5f);
                }
            }
        }
        catch (Exception)
        {
            // A malformed capture block is ignored; correlation quality decides
            // whether the overall measurement is usable.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            _recording = false;
        }
    }
}
