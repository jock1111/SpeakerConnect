using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>One speaker included in a Smart Volume Balance calibration run.</summary>
public sealed record BalanceSpeaker(string DeviceId, string DisplayName, bool IsSource);

/// <summary>
/// One relative loudness measurement. Levels are microphone-relative, not calibrated
/// absolute SPL. EndpointVolume is recorded because a speaker's acoustic output is
/// proportional to both its hardware efficiency and its Windows endpoint volume.
/// </summary>
public sealed record BalanceMeasurement(
    string DeviceId,
    string DisplayName,
    bool IsSource,
    double EndpointVolume,
    double ToneRms,
    double NoiseRms,
    double SignalToNoiseDb,
    double NormalizedLevel,
    float Gain)
{
    public double GainDb => VolumeBalanceMath.GainToDecibels(Gain);
}

public sealed record VolumeBalanceCalibrationResult(
    bool Success,
    string Message,
    IReadOnlyList<BalanceMeasurement> Measurements)
{
    public IReadOnlyDictionary<string, float> Gains =>
        Measurements.ToDictionary(
            measurement => measurement.DeviceId,
            measurement => measurement.Gain,
            StringComparer.OrdinalIgnoreCase);
}

public readonly record struct BalanceLevelInput(
    string DeviceId,
    double ToneRms,
    double EndpointVolume,
    bool IsSource);

/// <summary>
/// Pure math for relative loudness matching. The quietest measured speaker defines
/// the target acoustic level; all louder speakers are attenuated to match it.
///
/// A destination's correction is software gain applied to the loopback copy, so it
/// only needs the measured RMS ratio. The loopback source is different: its
/// correction is applied by lowering its Windows endpoint volume, which is already
/// part of the measurement. Its gain is therefore normalized by the endpoint volume
/// that was active while the measurement was taken. Both cases are attenuation-only
/// (gain &lt;= 1), so calibration can never boost or clip a signal.
/// </summary>
public static class VolumeBalanceMath
{
    public const double MinimumToneRms = 0.0003;
    public const double MinimumSignalToNoiseDb = 10.0;
    public const float MinimumGain = 0.05f;

    public static IReadOnlyDictionary<string, float> ComputeGains(
        IEnumerable<BalanceLevelInput> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var list = levels.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(levels));
        }

        foreach (var level in list)
        {
            if (!double.IsFinite(level.ToneRms) || level.ToneRms <= 0)
            {
                throw new ArgumentException($"Invalid level for device '{level.DeviceId}'.", nameof(levels));
            }

            if (!double.IsFinite(level.EndpointVolume) ||
                level.EndpointVolume < 0.02 ||
                level.EndpointVolume > 1.0001)
            {
                throw new ArgumentException(
                    $"Endpoint volume for device '{level.DeviceId}' is out of range.",
                    nameof(levels));
            }
        }

        var target = list.Min(level => level.ToneRms);
        var gains = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in list)
        {
            // Destinations are corrected after the loopback tap. The source has to
            // be corrected at the endpoint, where the measured level already
            // includes the endpoint-volume setting. Multiplying by that setting
            // converts the desired acoustic ratio into a full-range endpoint gain.
            var gain = level.IsSource
                ? target * level.EndpointVolume / level.ToneRms
                : target / level.ToneRms;

            gains[level.DeviceId] = (float)Math.Clamp(gain, MinimumGain, 1.0);
        }

        return gains;
    }

    public static double GainToDecibels(float gain)
    {
        return gain <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(gain);
    }

    public static bool IsReliable(double toneRms, double noiseRms)
    {
        if (!double.IsFinite(toneRms) || !double.IsFinite(noiseRms) || toneRms < MinimumToneRms)
        {
            return false;
        }

        var noise = Math.Max(noiseRms, 1e-9);
        var snr = 20.0 * Math.Log10(toneRms / noise);
        return snr >= MinimumSignalToNoiseDb;
    }
}

/// <summary>
/// Microphone-assisted relative loudness calibration. Plays identical band-limited
/// pink noise through one endpoint at a time, records the default microphone, and
/// derives attenuation-only gains relative to the quietest measured speaker. It
/// cannot and does not claim calibrated absolute SPL.
/// </summary>
public static class VolumeBalanceCalibrator
{
    private const double PreRollSeconds = 0.40;
    private const double ToneSeconds = 1.50;
    private const double PostRollSeconds = 0.35;
    private const double BlockSeconds = 0.05;
    private const int NoiseBlocks = 6;
    private const int ToneWindowBlocks = 6;

    public static bool TryGetMicrophoneName(out string name)
    {
        name = string.Empty;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var microphone = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Multimedia);
            name = microphone.FriendlyName;
            return microphone.State == DeviceState.Active;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static VolumeBalanceCalibrationResult Calibrate(
        IReadOnlyList<BalanceSpeaker> speakers,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        if (speakers.Count == 0)
        {
            return new VolumeBalanceCalibrationResult(false, "No speakers were selected.", Array.Empty<BalanceMeasurement>());
        }

        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();

            try
            {
                using var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                if (microphone.State != DeviceState.Active)
                {
                    return Fail("The default microphone is not active. Select/connect a microphone and retry.");
                }
            }
            catch (Exception)
            {
                return Fail("No default microphone is available. Connect a microphone and retry.");
            }

            var pending = new List<(BalanceSpeaker Speaker, double EndpointVolume, double ToneRms, double NoiseRms, double Snr, double Normalized)>();

            foreach (var speaker in speakers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Invoke($"Measuring {speaker.DisplayName}...");
                var measurement = MeasureSpeaker(enumerator, speaker);
                if (!measurement.Success)
                {
                    return Fail($"{speaker.DisplayName}: {measurement.Error}");
                }

                pending.Add((
                    speaker,
                    measurement.EndpointVolume,
                    measurement.ToneRms,
                    measurement.NoiseRms,
                    measurement.SignalToNoiseDb,
                    measurement.ToneRms / measurement.EndpointVolume));
            }

            var gains = VolumeBalanceMath.ComputeGains(
                pending.Select(item => new BalanceLevelInput(
                    item.Speaker.DeviceId,
                    item.ToneRms,
                    item.EndpointVolume,
                    item.Speaker.IsSource)));

            var measurements = new List<BalanceMeasurement>();
            foreach (var item in pending)
            {
                measurements.Add(new BalanceMeasurement(
                    item.Speaker.DeviceId,
                    item.Speaker.DisplayName,
                    item.Speaker.IsSource,
                    item.EndpointVolume,
                    item.ToneRms,
                    item.NoiseRms,
                    item.Snr,
                    item.Normalized,
                    gains[item.Speaker.DeviceId]));
            }

            var attenuated = measurements.Count(measurement => measurement.Gain < 0.995f);
            var message =
                $"Calibrated {measurements.Count} speaker(s); {attenuated} needed attenuation. " +
                "Levels are relative microphone measurements, not calibrated absolute SPL.";
            progress?.Invoke(message);
            return new VolumeBalanceCalibrationResult(true, message, measurements);
        }
        catch (OperationCanceledException)
        {
            return Fail("Calibration was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail($"Calibration failed: {ex.Message}");
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static VolumeBalanceCalibrationResult Fail(string message)
    {
        return new VolumeBalanceCalibrationResult(false, message, Array.Empty<BalanceMeasurement>());
    }

    private static SpeakerMeasurement MeasureSpeaker(
        MMDeviceEnumerator enumerator,
        BalanceSpeaker speaker)
    {
        MMDevice? renderDevice = null;
        MMDevice? microphoneDevice = null;
        WasapiCapture? capture = null;

        try
        {
            renderDevice = enumerator.GetDevice(speaker.DeviceId);
            if (renderDevice.State != DeviceState.Active || renderDevice.DataFlow != DataFlow.Render)
            {
                return SpeakerMeasurement.Failed("The output endpoint is not active.");
            }

            using var endpoint = renderDevice.AudioEndpointVolume;
            var endpointVolume = endpoint.MasterVolumeLevelScalar;
            if (endpoint.Mute)
            {
                return SpeakerMeasurement.Failed(
                    "The endpoint is muted. Unmute it in Windows and retry.");
            }

            if (endpointVolume < 0.10)
            {
                return SpeakerMeasurement.Failed(
                    "The endpoint volume is below 10%. Raise it slightly and retry.");
            }

            using var audioClient = renderDevice.AudioClient;
            var targetFormat = AudioFormatHelper.GetTargetOutputFormat(audioClient.MixFormat);
            var noise = new PinkNoiseSampleProvider(
                targetFormat.SampleRate,
                targetFormat.Channels,
                durationSeconds: PreRollSeconds + ToneSeconds + 0.5,
                amplitude: 0.18,
                seed: speaker.DeviceId.GetHashCode());
            var waveProvider = new FloatToWaveProvider(noise, targetFormat);

            using var output = new WasapiOut(
                renderDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                100);
            output.Init(waveProvider);

            microphoneDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            capture = new WasapiCapture(microphoneDevice);
            var converter = new WaveFormatToFloatConverter(capture.WaveFormat);
            var sampleRate = converter.SourceFormat.SampleRate;
            var recorded = new List<float>();
            var gate = new object();
            var work = Array.Empty<float>();

            capture.DataAvailable += (_, args) =>
            {
                try
                {
                    var frames = converter.Convert(args.Buffer, args.BytesRecorded, ref work);
                    if (frames <= 0)
                    {
                        return;
                    }

                    lock (gate)
                    {
                        for (var index = 0; index < frames * AudioRingBuffer.Channels; index++)
                        {
                            recorded.Add(work[index]);
                        }
                    }
                }
                catch (Exception)
                {
                    // A malformed capture block is ignored; the overall SNR check
                    // rejects a measurement that did not contain enough signal.
                }
            };

            capture.StartRecording();
            Thread.Sleep((int)(PreRollSeconds * 1000));
            output.Play();
            Thread.Sleep((int)(ToneSeconds * 1000));
            output.Stop();
            Thread.Sleep((int)(PostRollSeconds * 1000));
            capture.StopRecording();
            Thread.Sleep(80);

            float[] samples;
            lock (gate)
            {
                samples = recorded.ToArray();
            }

            if (samples.Length < sampleRate / 2 * AudioRingBuffer.Channels)
            {
                return SpeakerMeasurement.Failed("The microphone did not deliver enough audio.");
            }

            var analysis = AnalyzeRecording(samples, sampleRate);
            if (!VolumeBalanceMath.IsReliable(analysis.ToneRms, analysis.NoiseRms))
            {
                return SpeakerMeasurement.Failed(
                    $"The measurement was not reliable (signal {analysis.ToneRms:0.000000}, " +
                    $"noise {analysis.NoiseRms:0.000000}, SNR {analysis.SignalToNoiseDb:0.0} dB). " +
                    "Move the microphone closer to the listening position, reduce background noise, " +
                    "and retry.");
            }

            return new SpeakerMeasurement(
                true,
                string.Empty,
                endpointVolume,
                analysis.ToneRms,
                analysis.NoiseRms,
                analysis.SignalToNoiseDb,
                0);
        }
        catch (Exception ex)
        {
            return SpeakerMeasurement.Failed(ex.Message);
        }
        finally
        {
            try
            {
                capture?.Dispose();
            }
            catch (Exception)
            {
                // Cleanup is best-effort.
            }

            try
            {
                microphoneDevice?.Dispose();
            }
            catch (Exception)
            {
                // Cleanup is best-effort.
            }

            try
            {
                renderDevice?.Dispose();
            }
            catch (Exception)
            {
                // Cleanup is best-effort.
            }
        }
    }

    private static LoudnessAnalysis AnalyzeRecording(float[] samples, int sampleRate)
    {
        var channels = AudioRingBuffer.Channels;
        var frames = samples.Length / channels;
        var blockFrames = Math.Max(1, (int)Math.Round(sampleRate * BlockSeconds));
        var highPass = new[]
        {
            BiquadFilter.HighPass(sampleRate, 250, 0.707),
            BiquadFilter.HighPass(sampleRate, 250, 0.707)
        };
        var lowPass = new[]
        {
            BiquadFilter.LowPass(sampleRate, 3500, 0.707),
            BiquadFilter.LowPass(sampleRate, 3500, 0.707)
        };

        var filtered = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var value = samples[frame * channels + channel];
                value = (float)highPass[channel].Process(value);
                value = (float)lowPass[channel].Process(value);
                filtered[frame * channels + channel] = value;
            }
        }

        var blockPowers = new List<double>();
        for (var start = 0; start + blockFrames <= frames; start += blockFrames)
        {
            double sum = 0;
            var count = blockFrames * channels;
            for (var index = 0; index < count; index++)
            {
                var sample = filtered[start * channels + index];
                sum += sample * sample;
            }

            blockPowers.Add(sum / count);
        }

        if (blockPowers.Count < NoiseBlocks + ToneWindowBlocks)
        {
            return new LoudnessAnalysis(0, 0, 0);
        }

        var noisePower = blockPowers.Take(NoiseBlocks).Average();
        var maxTonePower = 0.0;
        for (var start = NoiseBlocks; start + ToneWindowBlocks <= blockPowers.Count; start++)
        {
            var windowPower = 0.0;
            for (var offset = 0; offset < ToneWindowBlocks; offset++)
            {
                windowPower += blockPowers[start + offset];
            }

            maxTonePower = Math.Max(maxTonePower, windowPower / ToneWindowBlocks);
        }

        var noiseRms = Math.Sqrt(Math.Max(0, noisePower));
        var toneRms = Math.Sqrt(Math.Max(0, maxTonePower - noisePower));
        var snr = toneRms <= 0
            ? 0
            : 20.0 * Math.Log10(toneRms / Math.Max(noiseRms, 1e-9));

        return new LoudnessAnalysis(toneRms, noiseRms, snr);
    }

    private readonly record struct LoudnessAnalysis(double ToneRms, double NoiseRms, double SignalToNoiseDb);

    private readonly record struct SpeakerMeasurement(
        bool Success,
        string Error,
        double EndpointVolume,
        double ToneRms,
        double NoiseRms,
        double SignalToNoiseDb,
        double Normalized)
    {
        public static SpeakerMeasurement Failed(string error) =>
            new(false, error, 0, 0, 0, 0, 0);
    }
}
