using System.Buffers.Binary;
using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Converts float samples to the exact PCM/IEEE-float encoding expected by a WASAPI
/// output stream. The source provider and the target format must have the same sample
/// rate and channel count; resampling and channel mapping happen earlier in the chain.
/// </summary>
public sealed class FloatToWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveFormatEncoding _encoding;
    private float[] _samples = Array.Empty<float>();

    public FloatToWaveProvider(ISampleProvider source, WaveFormat targetFormat)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = targetFormat ?? throw new ArgumentNullException(nameof(targetFormat));

        if (source.WaveFormat.SampleRate != targetFormat.SampleRate ||
            source.WaveFormat.Channels != targetFormat.Channels)
        {
            throw new ArgumentException("Source and target formats must have the same rate and channels.");
        }

        _encoding = targetFormat.Encoding;
        var supported = _encoding switch
        {
            WaveFormatEncoding.IeeeFloat => targetFormat.BitsPerSample == 32,
            WaveFormatEncoding.Pcm => targetFormat.BitsPerSample is 8 or 16 or 24 or 32,
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException($"Unsupported output sample format: {targetFormat}");
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var framesRequested = count / WaveFormat.BlockAlign;
        if (framesRequested <= 0)
        {
            return 0;
        }

        var samplesRequested = framesRequested * WaveFormat.Channels;
        if (_samples.Length < samplesRequested)
        {
            _samples = new float[samplesRequested];
        }

        var samplesRead = _source.Read(_samples, 0, samplesRequested);
        var framesRead = samplesRead / WaveFormat.Channels;
        if (framesRead <= 0)
        {
            // This is a live stream: returning zero makes WasapiOut treat the
            // stream as finished and stop its playback thread. Emit silence instead.
            Array.Clear(buffer, offset, count);
            return count;
        }

        var byteCount = framesRead * WaveFormat.BlockAlign;
        var destination = buffer.AsSpan(offset, count);
        var sampleCount = framesRead * WaveFormat.Channels;

        switch (_encoding)
        {
            case WaveFormatEncoding.IeeeFloat when WaveFormat.BitsPerSample == 32:
            {
                var writeSpan = destination.Slice(0, byteCount);
                for (var index = 0; index < sampleCount; index++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(
                        writeSpan.Slice(index * 4, 4),
                        Math.Clamp(_samples[index], -1f, 1f));
                }

                break;
            }

            case WaveFormatEncoding.Pcm when WaveFormat.BitsPerSample == 8:
            {
                var writeSpan = destination.Slice(0, byteCount);
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = Math.Clamp(_samples[index], -1f, 1f);
                    var value = (int)Math.Round(sample * 127f) + 128;
                    writeSpan[index] = (byte)Math.Clamp(value, 0, 255);
                }

                break;
            }

            case WaveFormatEncoding.Pcm when WaveFormat.BitsPerSample == 16:
            {
                var writeSpan = destination.Slice(0, byteCount);
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = Math.Clamp(_samples[index], -1f, 1f);
                    var value = (short)Math.Round(sample * 32767f);
                    BinaryPrimitives.WriteInt16LittleEndian(writeSpan.Slice(index * 2, 2), value);
                }

                break;
            }

            case WaveFormatEncoding.Pcm when WaveFormat.BitsPerSample == 24:
            {
                var writeSpan = destination.Slice(0, byteCount);
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = Math.Clamp(_samples[index], -1f, 1f);
                    var value = (int)Math.Round(sample * 8_388_607f);
                    var sampleOffset = index * 3;
                    writeSpan[sampleOffset] = (byte)value;
                    writeSpan[sampleOffset + 1] = (byte)(value >> 8);
                    writeSpan[sampleOffset + 2] = (byte)(value >> 16);
                }

                break;
            }

            case WaveFormatEncoding.Pcm when WaveFormat.BitsPerSample == 32:
            {
                var writeSpan = destination.Slice(0, byteCount);
                for (var index = 0; index < sampleCount; index++)
                {
                    var sample = Math.Clamp(_samples[index], -1f, 1f);
                    var value = (int)Math.Round(sample * 2_147_483_647.0);
                    BinaryPrimitives.WriteInt32LittleEndian(writeSpan.Slice(index * 4, 4), value);
                }

                break;
            }

            default:
                throw new NotSupportedException($"Unsupported output format: {WaveFormat}");
        }

        // Pad any remaining bytes with silence so WasapiOut always receives a full
        // buffer. A short read can starve the render thread and cause a pop or
        // crackle on the next buffer boundary.
        var remainingBytes = count - byteCount;
        if (remainingBytes > 0)
        {
            Array.Clear(buffer, offset + byteCount, remainingBytes);
        }

        return count;
    }
}
