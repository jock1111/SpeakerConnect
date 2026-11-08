using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Per-output software gain. Multiplies only the samples actually read. The gain can
/// be a fixed value or a dynamic source (for example, master volume with ramp), read
/// on every pull so volume changes are applied without locking the audio thread.
/// </summary>
public sealed class GainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Func<float>? _dynamicGain;
    private float _fixedGain = 1f;

    public GainSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Creates a gain provider whose value is read from <paramref name="getGain"/> on every pull.</summary>
    public GainSampleProvider(ISampleProvider source, Func<float> getGain)
        : this(source)
    {
        _dynamicGain = getGain ?? throw new ArgumentNullException(nameof(getGain));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Gain
    {
        get => Volatile.Read(ref _fixedGain);
        set => Volatile.Write(ref _fixedGain, Math.Clamp(value, 0f, 2f));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        var gain = _dynamicGain is not null ? _dynamicGain() : Gain;

        if (Math.Abs(gain - 1f) < 0.0001f)
        {
            return samplesRead;
        }

        for (var index = 0; index < samplesRead; index++)
        {
            buffer[offset + index] *= gain;
        }

        return samplesRead;
    }
}
