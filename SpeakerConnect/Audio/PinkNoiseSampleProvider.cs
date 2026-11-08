using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Generates a deterministic, band-limited pink-noise test signal. Pink noise has
/// approximately equal energy per octave, which is a better relative-loudness probe
/// than a single sine wave (room modes and one narrow frequency can dominate a
/// single-tone measurement). A 200–4000 Hz band keeps the signal in the range most
/// audible through small speakers and rejects low-frequency rumble.
/// </summary>
public sealed class PinkNoiseSampleProvider : ISampleProvider
{
    public const double DefaultAmplitude = 0.16;
    public const double DefaultDurationSeconds = 2.4;
    public const double BandHighPassHz = 200;
    public const double BandLowPassHz = 4000;

    private readonly int _channels;
    private readonly long _totalFrames;
    private readonly int _fadeFrames;
    private readonly double _amplitude;
    private readonly Random _random;
    private readonly BiquadFilter[] _highPass;
    private readonly BiquadFilter[] _lowPass;

    private double _b0;
    private double _b1;
    private double _b2;
    private double _b3;
    private double _b4;
    private double _b5;
    private double _b6;
    private long _frame;

    public PinkNoiseSampleProvider(
        int sampleRate,
        int channels,
        double durationSeconds = DefaultDurationSeconds,
        double amplitude = DefaultAmplitude,
        int seed = 1)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono or stereo is supported.");
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        SampleRate = sampleRate;
        _channels = channels;
        _amplitude = Math.Clamp(amplitude, 0.001, 0.5);
        _totalFrames = Math.Max(1, (long)Math.Round(sampleRate * durationSeconds));
        _fadeFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.03));
        _random = new Random(seed);
        _highPass = Enumerable.Range(0, channels)
            .Select(_ => BiquadFilter.HighPass(sampleRate, BandHighPassHz, 0.707))
            .ToArray();
        _lowPass = Enumerable.Range(0, channels)
            .Select(_ => BiquadFilter.LowPass(sampleRate, BandLowPassHz, 0.707))
            .ToArray();

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int SampleRate { get; }

    public long DurationFrames => _totalFrames;

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / _channels;
        var framesWritten = 0;

        while (framesWritten < framesRequested && _frame < _totalFrames)
        {
            var white = _random.NextDouble() * 2.0 - 1.0;

            // Paul Kellet's economy pink-noise filter.
            _b0 = 0.99886 * _b0 + white * 0.0555179;
            _b1 = 0.99332 * _b1 + white * 0.0750759;
            _b2 = 0.96900 * _b2 + white * 0.1538520;
            _b3 = 0.86650 * _b3 + white * 0.3104856;
            _b4 = 0.55000 * _b4 + white * 0.5329522;
            _b5 = -0.7616 * _b5 - white * 0.0168980;
            var pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
            _b6 = white * 0.115926;

            // Normalize the filter's naturally large range, then apply the requested
            // test amplitude and a click-free fade in/out.
            var sample = pink * 0.11 * _amplitude;
            var fade = 1.0;
            if (_frame < _fadeFrames)
            {
                fade = _frame / (double)_fadeFrames;
            }
            else if (_frame >= _totalFrames - _fadeFrames)
            {
                fade = Math.Max(0, _totalFrames - _frame - 1) / (double)_fadeFrames;
            }

            sample *= fade;
            sample = Math.Clamp(sample, -0.5, 0.5);

            for (var channel = 0; channel < _channels; channel++)
            {
                var filtered = _highPass[channel].Process(sample);
                filtered = _lowPass[channel].Process(filtered);
                buffer[offset + framesWritten * _channels + channel] = (float)Math.Clamp(filtered, -0.5, 0.5);
            }

            framesWritten++;
            _frame++;
        }

        var samplesWritten = framesWritten * _channels;
        if (samplesWritten < count)
        {
            Array.Clear(buffer, offset + samplesWritten, count - samplesWritten);
        }

        return count;
    }
}

/// <summary>Small RBJ biquad used for the calibration signal's band limiting.</summary>
public sealed class BiquadFilter
{
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;

    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    private BiquadFilter(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public static BiquadFilter HighPass(int sampleRate, double frequencyHz, double q)
    {
        var (b0, b1, b2, a0, a1, a2) = Coefficients(sampleRate, frequencyHz, q, highPass: true);
        return new BiquadFilter(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter LowPass(int sampleRate, double frequencyHz, double q)
    {
        var (b0, b1, b2, a0, a1, a2) = Coefficients(sampleRate, frequencyHz, q, highPass: false);
        return new BiquadFilter(b0, b1, b2, a0, a1, a2);
    }

    public double Process(double input)
    {
        var output =
            _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return output;
    }

    private static (double B0, double B1, double B2, double A0, double A1, double A2)
        Coefficients(int sampleRate, double frequencyHz, double q, bool highPass)
    {
        var w0 = 2.0 * Math.PI * Math.Clamp(frequencyHz, 10, sampleRate * 0.45) / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * Math.Max(0.1, q));

        if (highPass)
        {
            return ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        return ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }
}
