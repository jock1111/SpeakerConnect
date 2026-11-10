using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// A finite, in-memory stereo float signal. Used by the sync source player to render
/// a chirp or burst train through a real Windows endpoint without writing a file.
/// Read returns 0 after the signal, which tells NAudio's output to stop cleanly.
/// </summary>
public sealed class StaticSignalSampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public StaticSignalSampleProvider(float[] interleavedSamples, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(interleavedSamples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (interleavedSamples.Length % channels != 0)
        {
            throw new ArgumentException(
                "The sample count must be a whole number of frames.",
                nameof(interleavedSamples));
        }

        _samples = interleavedSamples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var remaining = _samples.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toCopy = Math.Min(count, remaining);
        Array.Copy(_samples, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }
}
