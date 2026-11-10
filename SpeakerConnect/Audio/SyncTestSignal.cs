namespace SpeakerConnect.Audio;

/// <summary>
/// Test signals and timing analysis for the synchronization workflow.
///
/// The automatic measurement uses a short linear chirp because its autocorrelation
/// has one narrow, dominant peak even in a reflective room. The same chirp is used
/// for every speaker; time slots and per-channel test gain isolate one speaker at a
/// time, so the microphone recording can be correlated against a single known
/// waveform without relying on recording-start timestamps.
/// </summary>
public static class SyncTestSignal
{
    public const double ChirpStartHz = 350;
    public const double ChirpEndHz = 3_500;
    public const double ChirpSeconds = 0.16;
    public const double FadeSeconds = 0.008;
    public const double DefaultAmplitude = 0.26;
    public const double ManualBurstSeconds = 0.12;
    public const double ManualBurstFrequencyHz = 1_000;

    /// <summary>Analysis rate used by the correlator. High enough for the chirp band, low enough to stay fast.</summary>
    public const int AnalysisSampleRate = 12_000;

    /// <summary>
    /// Creates the linear chirp used for automatic timing. The returned buffer is
    /// interleaved stereo by default so it can be mixed directly into the routing
    /// ring or rendered to an endpoint.
    /// </summary>
    public static float[] CreateChirp(
        int sampleRate,
        double amplitude = DefaultAmplitude,
        bool stereo = true)
    {
        return CreateChirp(
            sampleRate,
            ChirpStartHz,
            ChirpEndHz,
            ChirpSeconds,
            amplitude,
            stereo);
    }

    public static float[] CreateChirp(
        int sampleRate,
        double startHz,
        double endHz,
        double durationSeconds,
        double amplitude = DefaultAmplitude,
        bool stereo = true)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (startHz <= 0 || endHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startHz));
        }

        if (!double.IsFinite(amplitude) || amplitude is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude));
        }

        var channels = stereo ? 2 : 1;
        var frames = Math.Max(1, (int)Math.Round(sampleRate * durationSeconds));
        var fadeFrames = Math.Min(
            frames / 2,
            Math.Max(1, (int)Math.Round(sampleRate * FadeSeconds)));
        var result = new float[frames * channels];
        var sweep = (endHz - startHz) / durationSeconds;

        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)sampleRate;
            var phase = 2 * Math.PI * (startHz * t + 0.5 * sweep * t * t);
            var envelope = 1.0;

            if (frame < fadeFrames)
            {
                var x = frame / (double)fadeFrames;
                envelope = Math.Sin(0.5 * Math.PI * x);
                envelope *= envelope;
            }
            else if (frame >= frames - fadeFrames)
            {
                var x = (frames - 1 - frame) / (double)fadeFrames;
                envelope = Math.Sin(0.5 * Math.PI * x);
                envelope *= envelope;
            }

            var value = (float)(Math.Sin(phase) * envelope * amplitude);
            var index = frame * channels;
            result[index] = value;
            if (stereo)
            {
                result[index + 1] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a click-free burst train for the manual live test. The bursts are
    /// rendered natively through the loopback source, captured by the running
    /// routing session, and copied to every destination with its configured delay.
    /// </summary>
    public static float[] CreateManualBurstTrain(
        int sampleRate,
        int bursts,
        double intervalSeconds,
        double amplitude = DefaultAmplitude)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (bursts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bursts));
        }

        if (intervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        }

        var burstFrames = Math.Max(1, (int)Math.Round(sampleRate * ManualBurstSeconds));
        var intervalFrames = Math.Max(burstFrames + 1, (int)Math.Round(sampleRate * intervalSeconds));
        var totalFrames = intervalFrames * bursts;
        var result = new float[totalFrames * AudioRingBuffer.Channels];
        var fadeFrames = Math.Min(
            burstFrames / 2,
            Math.Max(1, (int)Math.Round(sampleRate * 0.006)));

        for (var burst = 0; burst < bursts; burst++)
        {
            var start = burst * intervalFrames;
            for (var frame = 0; frame < burstFrames; frame++)
            {
                var t = frame / (double)sampleRate;
                var envelope = 1.0;

                if (frame < fadeFrames)
                {
                    var x = frame / (double)fadeFrames;
                    envelope = Math.Sin(0.5 * Math.PI * x);
                    envelope *= envelope;
                }
                else if (frame >= burstFrames - fadeFrames)
                {
                    var x = (burstFrames - 1 - frame) / (double)fadeFrames;
                    envelope = Math.Sin(0.5 * Math.PI * x);
                    envelope *= envelope;
                }

                var value = (float)(
                    Math.Sin(2 * Math.PI * ManualBurstFrequencyHz * t) *
                    envelope *
                    amplitude);
                var index = (start + frame) * AudioRingBuffer.Channels;
                result[index] = value;
                result[index + 1] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Mixes a stereo test clip into a mono analysis buffer. The two channels are
    /// averaged so a chirp played only on one channel is still detected.
    /// </summary>
    public static float[] ToMono(
        ReadOnlySpan<float> interleavedStereo,
        int channels = AudioRingBuffer.Channels)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var frames = interleavedStereo.Length / channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * channels;
            if (channels == 1)
            {
                mono[frame] = interleavedStereo[index];
                continue;
            }

            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleavedStereo[index + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    /// <summary>Downsamples a mono signal for correlation. A short box filter suppresses aliasing.</summary>
    public static float[] ResampleMono(
        ReadOnlySpan<float> input,
        int sourceSampleRate,
        int targetSampleRate)
    {
        if (sourceSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        }

        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        if (input.Length == 0 || sourceSampleRate == targetSampleRate)
        {
            return input.ToArray();
        }

        var ratio = sourceSampleRate / (double)targetSampleRate;
        var outputLength = Math.Max(1, (int)Math.Round(input.Length / ratio));
        var output = new float[outputLength];
        var window = Math.Max(1, (int)Math.Floor(ratio));

        for (var index = 0; index < outputLength; index++)
        {
            var start = index * ratio;
            var end = Math.Min(input.Length, start + ratio);
            var first = (int)Math.Floor(start);
            var last = Math.Max(first + 1, (int)Math.Ceiling(end));
            last = Math.Min(last, input.Length);

            double sum = 0;
            var count = 0;
            for (var source = first; source < last; source++)
            {
                sum += input[source];
                count++;
            }

            // A small box average is enough: the chirp is below 4 kHz and the
            // analysis rate is 12 kHz, so the anti-alias filter only has to remove
            // content above 6 kHz.
            if (window > count)
            {
                for (var extra = count; extra < window && first + extra < input.Length; extra++)
                {
                    sum += input[first + extra];
                    count++;
                }
            }

            output[index] = count == 0 ? 0 : (float)(sum / count);
        }

        return output;
    }
}

/// <summary>Result of correlating a recording against the known chirp.</summary>
public sealed record SyncCorrelationResult(
    bool Found,
    double LagSeconds,
    double Correlation,
    double PeakToSidelobeRatio,
    double CoherentSignalToNoiseDb,
    double ConfidencePercent,
    string? FailureReason)
{
    public static SyncCorrelationResult Failed(string reason) =>
        new(false, 0, 0, 0, 0, 0, reason);
}

/// <summary>
/// Normalized chirp correlation. The analysis is deliberately simple and
/// deterministic: downsample to 12 kHz, slide the known chirp, and normalize by the
/// local recording energy. A narrow peak with high peak-to-sidelobe ratio and
/// coherent SNR is required before a measurement is accepted.
/// </summary>
public static class SyncSignalAnalyzer
{
    public const double MinimumCorrelation = 0.18;
    public const double MinimumPeakToSidelobe = 3.0;
    public const double MinimumCoherentSnrDb = 4.0;

    /// <summary>
    /// Finds the chirp in a mono recording window.
    /// </summary>
    /// <param name="monoRecording">Full mono recording at <paramref name="sampleRate"/>.</param>
    /// <param name="sampleRate">Recording sample rate.</param>
    /// <param name="searchStartSample">First sample of the search window.</param>
    /// <param name="searchLengthSamples">Window length. The window must contain the whole chirp.</param>
    public static SyncCorrelationResult FindChirp(
        ReadOnlySpan<float> monoRecording,
        int sampleRate,
        int searchStartSample,
        int searchLengthSamples)
    {
        if (sampleRate <= 0)
        {
            return SyncCorrelationResult.Failed("The recording sample rate is invalid.");
        }

        if (searchStartSample < 0 ||
            searchLengthSamples <= 0 ||
            searchStartSample >= monoRecording.Length)
        {
            return SyncCorrelationResult.Failed("The correlation search window is outside the recording.");
        }

        var available = Math.Min(searchLengthSamples, monoRecording.Length - searchStartSample);
        if (available <= 0)
        {
            return SyncCorrelationResult.Failed("The correlation search window is empty.");
        }

        var window = monoRecording.Slice(searchStartSample, available).ToArray();
        var analysis = SyncTestSignal.ResampleMono(
            window,
            sampleRate,
            SyncTestSignal.AnalysisSampleRate);
        var template = SyncTestSignal.CreateChirp(
            SyncTestSignal.AnalysisSampleRate,
            stereo: false);

        if (analysis.Length < template.Length)
        {
            return SyncCorrelationResult.Failed("The recording is shorter than the test chirp.");
        }

        var frames = analysis.Length;
        var templateFrames = template.Length;
        var maxLag = frames - templateFrames;
        if (maxLag <= 0)
        {
            return SyncCorrelationResult.Failed("The recording is too short for correlation.");
        }

        // Prefix energy makes the normalized correlation cheap: the template energy
        // is constant and the local window energy is a subtraction.
        var prefix = new double[frames + 1];
        for (var index = 0; index < frames; index++)
        {
            prefix[index + 1] = prefix[index] + (double)analysis[index] * analysis[index];
        }

        double templateEnergy = 0;
        for (var index = 0; index < templateFrames; index++)
        {
            templateEnergy += (double)template[index] * template[index];
        }

        if (templateEnergy <= 0)
        {
            return SyncCorrelationResult.Failed("The test chirp has no energy.");
        }

        var correlations = new double[maxLag + 1];
        var bestLag = 0;
        var bestCorrelation = double.NegativeInfinity;
        var bestDot = 0.0;
        var bestEnergy = 0.0;

        for (var lag = 0; lag <= maxLag; lag++)
        {
            double dot = 0;
            for (var index = 0; index < templateFrames; index++)
            {
                dot += (double)analysis[lag + index] * template[index];
            }

            var energy = Math.Max(0, prefix[lag + templateFrames] - prefix[lag]);
            var correlation = energy <= 0
                ? 0
                : dot / Math.Sqrt(energy * templateEnergy);
            correlations[lag] = correlation;

            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
                bestDot = dot;
                bestEnergy = energy;
            }
        }

        var guard = Math.Max(
            1,
            (int)Math.Round(SyncTestSignal.AnalysisSampleRate * 0.012));
        var secondPeak = 0.0;
        var sumSquares = 0.0;
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var correlation = correlations[lag];
            sumSquares += correlation * correlation;
            if (Math.Abs(lag - bestLag) <= guard)
            {
                continue;
            }

            secondPeak = Math.Max(secondPeak, correlation);
        }

        var rms = Math.Sqrt(sumSquares / (maxLag + 1));
        var sidelobe = Math.Max(secondPeak, Math.Max(rms, 1e-9));
        var psr = bestCorrelation / sidelobe;

        var projectedEnergy = bestDot * bestDot / templateEnergy;
        var residualEnergy = Math.Max(1e-12, bestEnergy - projectedEnergy);
        var coherentSnr = 10 * Math.Log10(projectedEnergy / residualEnergy);

        if (!double.IsFinite(bestCorrelation) || !double.IsFinite(psr))
        {
            return SyncCorrelationResult.Failed("The correlation result was not finite.");
        }

        var correlationScore = Math.Clamp(bestCorrelation / 0.45, 0, 1);
        var psrScore = Math.Clamp((psr - 2.5) / 7.5, 0, 1);
        var snrScore = Math.Clamp(coherentSnr / 20.0, 0, 1);
        var confidence = 100.0 * (
            0.45 * correlationScore +
            0.25 * psrScore +
            0.30 * snrScore);

        var failure = bestCorrelation < MinimumCorrelation
            ? $"The chirp correlation is too weak ({bestCorrelation:0.00}; need {MinimumCorrelation:0.00})."
            : psr < MinimumPeakToSidelobe
                ? $"The chirp peak is not distinct (peak/sidelobe {psr:0.0}; need {MinimumPeakToSidelobe:0.0})."
                : coherentSnr < MinimumCoherentSnrDb
                    ? $"The chirp signal-to-noise ratio is too low ({coherentSnr:0.0} dB; need {MinimumCoherentSnrDb:0.0} dB)."
                    : null;

        var lagSeconds = bestLag / (double)SyncTestSignal.AnalysisSampleRate;
        return new SyncCorrelationResult(
            failure is null,
            lagSeconds,
            bestCorrelation,
            psr,
            coherentSnr,
            confidence,
            failure);
    }
}
