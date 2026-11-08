namespace SpeakerConnect.Audio;

/// <summary>
/// Generates the short sync-calibration burst used by the manual alignment workflow.
/// The burst is a band-friendly sine with fast fade in/out to avoid clicks; it is
/// rendered directly to each selected destination (not through the loopback source)
/// so the user can compare when each speaker reproduces it.
/// </summary>
public static class AudioCalibrationTone
{
    public const double FrequencyHz = 1200;
    public const double DefaultAmplitude = 0.22;
    public const double BlockSeconds = 0.01;
    public const double FadeSeconds = 0.006;

    public static float[] CreateBurst(
        int sampleRate,
        double durationSeconds,
        double amplitude = DefaultAmplitude)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        var frames = (int)Math.Round(sampleRate * durationSeconds);
        var fadeFrames = Math.Max(1, (int)Math.Round(sampleRate * FadeSeconds));
        var data = new float[frames * AudioRingBuffer.Channels];

        for (var frame = 0; frame < frames; frame++)
        {
            double envelope;
            if (frame < fadeFrames)
            {
                envelope = frame / (double)fadeFrames;
            }
            else if (frame >= frames - fadeFrames)
            {
                envelope = (frames - frame) / (double)fadeFrames;
            }
            else
            {
                envelope = 1.0;
            }

            var sample = (float)(
                Math.Sin(2 * Math.PI * FrequencyHz * frame / sampleRate) *
                Math.Clamp(amplitude, 0, 1) *
                Math.Clamp(envelope, 0, 1));

            data[frame * AudioRingBuffer.Channels] = sample;
            data[frame * AudioRingBuffer.Channels + 1] = sample;
        }

        return data;
    }
}
