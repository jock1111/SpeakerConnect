using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Converts a WASAPI loopback block (which uses the endpoint mix format) into the
/// application's canonical interleaved stereo 32-bit float representation.
/// PCM 8/16/24/32 and IEEE float 32 are supported. Multichannel sources use the
/// first two channels (front left/right); mono sources are duplicated to stereo.
/// </summary>
public sealed class WaveFormatToFloatConverter
{
    private readonly WaveFormat _format;
    private readonly int _inputChannels;

    public WaveFormatToFloatConverter(WaveFormat captureFormat)
    {
        ArgumentNullException.ThrowIfNull(captureFormat);

        _format = AudioFormatHelper.GetStandardFormat(captureFormat);
        _inputChannels = _format.Channels;

        if (_inputChannels < 1)
        {
            throw new NotSupportedException("The capture endpoint reported an invalid channel count.");
        }

        var supported = _format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => _format.BitsPerSample == 32,
            WaveFormatEncoding.Pcm => _format.BitsPerSample is 8 or 16 or 24 or 32,
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"Unsupported loopback sample format: {captureFormat}. " +
                "Only PCM 8/16/24/32 and 32-bit IEEE float are supported.");
        }
    }

    public WaveFormat SourceFormat => _format;

    public int InputChannels => _inputChannels;

    /// <summary>
    /// Converts bytes from a capture event. <paramref name="output"/> is resized when
    /// needed and contains <c>returnedFrames * 2</c> interleaved float samples.
    /// </summary>
    public int Convert(byte[] input, int bytesRecorded, ref float[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (bytesRecorded <= 0)
        {
            return 0;
        }

        var blockAlign = _format.BlockAlign;
        if (blockAlign <= 0)
        {
            return 0;
        }

        var frames = Math.Min(bytesRecorded / blockAlign, input.Length / blockAlign);
        if (frames <= 0)
        {
            return 0;
        }

        var requiredSamples = frames * AudioRingBuffer.Channels;
        if (output.Length < requiredSamples)
        {
            output = new float[requiredSamples];
        }

        var bytes = input.AsSpan(0, frames * blockAlign);
        var destination = output.AsSpan(0, requiredSamples);
        var destinationIndex = 0;

        switch (_format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when _format.BitsPerSample == 32:
            {
                var source = MemoryMarshal.Cast<byte, float>(bytes);
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceIndex = frame * _inputChannels;
                    destination[destinationIndex++] = source[sourceIndex];
                    destination[destinationIndex++] = _inputChannels >= 2
                        ? source[sourceIndex + 1]
                        : source[sourceIndex];
                }

                break;
            }

            case WaveFormatEncoding.Pcm when _format.BitsPerSample == 16:
            {
                var source = MemoryMarshal.Cast<byte, short>(bytes);
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceIndex = frame * _inputChannels;
                    destination[destinationIndex++] = source[sourceIndex] / 32768f;
                    destination[destinationIndex++] = (_inputChannels >= 2
                        ? source[sourceIndex + 1]
                        : source[sourceIndex]) / 32768f;
                }

                break;
            }

            case WaveFormatEncoding.Pcm when _format.BitsPerSample == 24:
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceIndex = frame * _inputChannels * 3;
                    var left = ReadPcm24(bytes, sourceIndex);
                    var right = _inputChannels >= 2
                        ? ReadPcm24(bytes, sourceIndex + 3)
                        : left;
                    destination[destinationIndex++] = left / 8388608f;
                    destination[destinationIndex++] = right / 8388608f;
                }

                break;
            }

            case WaveFormatEncoding.Pcm when _format.BitsPerSample == 32:
            {
                var source = MemoryMarshal.Cast<byte, int>(bytes);
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceIndex = frame * _inputChannels;
                    destination[destinationIndex++] = source[sourceIndex] / 2147483648f;
                    destination[destinationIndex++] = (_inputChannels >= 2
                        ? source[sourceIndex + 1]
                        : source[sourceIndex]) / 2147483648f;
                }

                break;
            }

            case WaveFormatEncoding.Pcm when _format.BitsPerSample == 8:
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var sourceIndex = frame * _inputChannels;
                    destination[destinationIndex++] = (bytes[sourceIndex] - 128) / 128f;
                    destination[destinationIndex++] = ((_inputChannels >= 2
                        ? bytes[sourceIndex + 1]
                        : bytes[sourceIndex]) - 128) / 128f;
                }

                break;
            }

            default:
                throw new NotSupportedException($"Unsupported capture format: {_format}");
        }

        return frames;
    }

    private static int ReadPcm24(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        if ((value & 0x0080_0000) != 0)
        {
            value |= unchecked((int)0xFF00_0000);
        }

        return value;
    }
}
