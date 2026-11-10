using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// A click-free multiplier used to isolate one speaker during a timing test.
/// Normal playback leaves the gain at 1. Test isolation ramps to 0 or 1 over a few
/// milliseconds, so muting the other speakers never introduces a pop.
/// </summary>
public sealed class SmoothGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _rampFrames;
    private float _currentGain = 1f;
    private float _targetGain = 1f;
    private int _rampFramesRemaining;

    public SmoothGainSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _rampFrames = Math.Max(1, (int)Math.Round(source.WaveFormat.SampleRate * 0.012));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Gain => Volatile.Read(ref _targetGain);

    public void SetGain(float gain)
    {
        var clamped = Math.Clamp(gain, 0f, 1f);
        Volatile.Write(ref _targetGain, clamped);
        Interlocked.Exchange(ref _rampFramesRemaining, _rampFrames);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        if (channels <= 0 || samplesRead <= 0)
        {
            return samplesRead;
        }

        var target = Volatile.Read(ref _targetGain);
        var frames = samplesRead / channels;

        if (Math.Abs(_currentGain - target) < 1e-6f && Volatile.Read(ref _rampFramesRemaining) <= 0)
        {
            if (Math.Abs(_currentGain - 1f) < 1e-6f)
            {
                return samplesRead;
            }

            for (var index = 0; index < samplesRead; index++)
            {
                buffer[offset + index] *= _currentGain;
            }

            return samplesRead;
        }

        var remaining = Volatile.Read(ref _rampFramesRemaining);
        var step = remaining > 0
            ? (target - _currentGain) / remaining
            : target - _currentGain;

        for (var frame = 0; frame < frames; frame++)
        {
            if (remaining > 0)
            {
                _currentGain += step;
                remaining--;
                if (remaining == 0)
                {
                    _currentGain = target;
                }
            }
            else
            {
                _currentGain = target;
            }

            var gain = Math.Clamp(_currentGain, 0f, 1f);
            var index = offset + frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                buffer[index + channel] *= gain;
            }
        }

        Interlocked.Exchange(ref _rampFramesRemaining, remaining);
        if (remaining == 0)
        {
            _currentGain = target;
        }

        return samplesRead;
    }
}
