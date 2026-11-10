using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeakerConnect.Audio;

/// <summary>
/// Plays a short in-memory signal through one render endpoint. The source endpoint
/// must keep playing normally; this player is used only for the synchronization test
/// chirp and the live manual burst train. It uses the endpoint's normal shared-mode
/// Windows path, so the routing engine's loopback capture sees the signal exactly as
/// it sees any other system audio.
/// </summary>
public static class EndpointSignalPlayer
{
    private const int OutputLatencyMilliseconds = 100;

    /// <summary>
    /// Plays a stereo float signal and blocks until it has finished or the token is
    /// cancelled. The signal is resampled and channel-mapped to the endpoint format
    /// when necessary.
    /// </summary>
    public static void Play(
        string deviceId,
        float[] interleavedStereo,
        int sampleRate,
        float volume,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(interleavedStereo);

        if (interleavedStereo.Length == 0)
        {
            return;
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);

        if (device.State != DeviceState.Active)
        {
            throw new InvalidOperationException("The playback endpoint is not active.");
        }

        if (device.DataFlow != DataFlow.Render)
        {
            throw new InvalidOperationException("The selected endpoint is not a render device.");
        }

        using var audioClient = device.AudioClient;
        var format = AudioFormatHelper.GetTargetOutputFormat(audioClient.MixFormat);

        ISampleProvider provider = new StaticSignalSampleProvider(
            interleavedStereo,
            sampleRate,
            AudioRingBuffer.Channels);

        if (provider.WaveFormat.SampleRate != format.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, format.SampleRate);
        }

        if (format.Channels == 1)
        {
            provider = new StereoChannelMapSampleProvider(provider, 1);
        }

        if (Math.Abs(volume - 1f) > 1e-6f)
        {
            provider = new VolumeSampleProvider(provider)
            {
                Volume = Math.Clamp(volume, 0f, 1f)
            };
        }

        var waveProvider = new FloatToWaveProvider(provider, format);
        using var output = new WasapiOut(
            device,
            AudioClientShareMode.Shared,
            useEventSync: true,
            OutputLatencyMilliseconds);
        using var stopped = new ManualResetEventSlim(false);

        output.PlaybackStopped += (_, _) => stopped.Set();

        try
        {
            output.Init(waveProvider);
            output.Play();

            var durationMilliseconds = interleavedStereo.Length /
                                       (double)(AudioRingBuffer.Channels * sampleRate) * 1000.0;

            // A finite provider stops itself; the timeout is a safety net for drivers
            // that keep the stream open after the final sample.
            var deadline = Environment.TickCount64 +
                           (long)Math.Ceiling(durationMilliseconds) +
                           OutputLatencyMilliseconds +
                           1_000;

            while (!stopped.IsSet)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }

                stopped.Wait(20);
            }
        }
        finally
        {
            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // The endpoint may have disappeared while the signal was playing.
            }
        }
    }
}
