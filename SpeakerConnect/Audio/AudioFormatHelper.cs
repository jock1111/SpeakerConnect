using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Format inspection and target-format selection. The application keeps its internal
/// pipeline in 32-bit float and converts to the encoding that the destination endpoint
/// mixes in (or to float when the endpoint uses an encoding we do not write directly).
/// </summary>
public static class AudioFormatHelper
{
    /// <summary>
    /// Builds a standard (non-extensible) PCM/IEEE-float format for an output channel.
    /// We only ever produce mono or stereo because the source material is downmixed to
    /// stereo, and Windows' shared-mode audio engine can upmix stereo for surround endpoints.
    /// </summary>
    public static WaveFormat GetTargetOutputFormat(WaveFormat deviceMixFormat)
    {
        ArgumentNullException.ThrowIfNull(deviceMixFormat);

        var standard = GetStandardFormat(deviceMixFormat);
        var channels = Math.Clamp(standard.Channels, 1, 2);
        var sampleRate = standard.SampleRate > 0 ? standard.SampleRate : 48_000;

        if (standard.Encoding == WaveFormatEncoding.IeeeFloat && standard.BitsPerSample == 32)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        if (standard.Encoding == WaveFormatEncoding.Pcm &&
            standard.BitsPerSample is 8 or 16 or 24 or 32)
        {
            return new WaveFormat(sampleRate, standard.BitsPerSample, channels);
        }

        // Unusual driver format (compressed, etc.). Let the Windows audio engine
        // convert float samples to the endpoint's actual mix format.
        return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    /// <summary>
    /// A conservative stereo/mono float format to retry if the endpoint rejects the
    /// preferred target encoding during shared-mode initialization.
    /// </summary>
    public static WaveFormat GetFallbackOutputFormat(WaveFormat deviceMixFormat)
    {
        ArgumentNullException.ThrowIfNull(deviceMixFormat);

        var standard = GetStandardFormat(deviceMixFormat);
        var channels = Math.Clamp(standard.Channels, 1, 2);
        var sampleRate = standard.SampleRate > 0 ? standard.SampleRate : 48_000;
        return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public static bool HasSameEncodingAndShape(WaveFormat left, WaveFormat right)
    {
        return left.SampleRate == right.SampleRate &&
               left.Channels == right.Channels &&
               left.BitsPerSample == right.BitsPerSample &&
               left.Encoding == right.Encoding;
    }

    public static WaveFormat GetStandardFormat(WaveFormat format)
    {
        return format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;
    }

    public static string Describe(WaveFormat format)
    {
        var standard = GetStandardFormat(format);
        var encoding = standard.Encoding switch
        {
            WaveFormatEncoding.Pcm => "PCM",
            WaveFormatEncoding.IeeeFloat => "float",
            _ => standard.Encoding.ToString()
        };

        var kiloHertz = standard.SampleRate / 1000.0;
        return $"{kiloHertz:0.#} kHz, {standard.Channels} ch, {standard.BitsPerSample}-bit {encoding}";
    }
}
