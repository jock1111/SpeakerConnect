using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Maps the canonical stereo float stream onto a mono or stereo output format.
/// Stereo input is passed through, mono output is a 50/50 downmix.
/// </summary>
public sealed class StereoChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _targetChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public StereoChannelMapSampleProvider(ISampleProvider source, int targetChannels)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        if (source.WaveFormat.Channels != 2)
        {
            throw new ArgumentException("The source must be stereo.", nameof(source));
        }

        if (targetChannels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(targetChannels));
        }

        _targetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / _targetChannels;
        var sourceSamplesNeeded = framesRequested * 2;

        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        var sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        var framesRead = sourceSamplesRead / 2;

        if (_targetChannels == 2)
        {
            Array.Copy(_sourceBuffer, 0, buffer, offset, framesRead * 2);
            return framesRead * 2;
        }

        for (var frame = 0; frame < framesRead; frame++)
        {
            buffer[offset + frame] =
                (_sourceBuffer[frame * 2] + _sourceBuffer[frame * 2 + 1]) * 0.5f;
        }

        return framesRead;
    }
}
