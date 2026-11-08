namespace SpeakerConnect.Audio;

/// <summary>
/// Thread-safe, overwrite-oldest circular buffer of interleaved stereo float frames.
/// One writer (loopback capture) and one reader per output channel are supported.
/// </summary>
public sealed class AudioRingBuffer
{
    public const int Channels = 2;

    private readonly object _gate = new();
    private readonly float[] _data;
    private readonly int _capacityFrames;
    private long _writeFrame;

    public AudioRingBuffer(int sampleRate, TimeSpan capacity)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (capacity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        SampleRate = sampleRate;
        _capacityFrames = Math.Max(sampleRate, (int)Math.Ceiling(sampleRate * capacity.TotalSeconds));
        _data = new float[_capacityFrames * Channels];
    }

    public int SampleRate { get; }

    public int CapacityFrames => _capacityFrames;

    public (long WriteFrame, long OldestFrame) GetSnapshot()
    {
        lock (_gate)
        {
            return (_writeFrame, Math.Max(0, _writeFrame - _capacityFrames));
        }
    }

    public long WriteFrameCount
    {
        get
        {
            lock (_gate)
            {
                return _writeFrame;
            }
        }
    }

    public long OldestFrame
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, _writeFrame - _capacityFrames);
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / Channels;
        if (frames == 0)
        {
            return;
        }

        if (frames > _capacityFrames)
        {
            // Keep the newest portion of an unusually large capture block.
            samples = samples.Slice((frames - _capacityFrames) * Channels);
            frames = _capacityFrames;
        }

        lock (_gate)
        {
            var start = (int)(_writeFrame % _capacityFrames);
            var firstFrames = Math.Min(frames, _capacityFrames - start);

            samples.Slice(0, firstFrames * Channels)
                .CopyTo(_data.AsSpan(start * Channels, firstFrames * Channels));

            var remainingFrames = frames - firstFrames;
            if (remainingFrames > 0)
            {
                samples.Slice(firstFrames * Channels, remainingFrames * Channels)
                    .CopyTo(_data.AsSpan(0, remainingFrames * Channels));
            }

            _writeFrame += frames;
        }
    }

    /// <summary>
    /// Copies the requested frames if they are still present. Returns zero when the
    /// requested start position has already been overwritten or is in the future.
    /// </summary>
    public int Read(long startFrame, Span<float> destination, int frames)
    {
        if (frames <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var oldestFrame = Math.Max(0, _writeFrame - _capacityFrames);
            if (startFrame < oldestFrame || startFrame >= _writeFrame)
            {
                return 0;
            }

            var availableFrames = (int)Math.Min(frames, _writeFrame - startFrame);
            var sourceStart = (int)(startFrame % _capacityFrames);
            var firstFrames = Math.Min(availableFrames, _capacityFrames - sourceStart);

            _data.AsSpan(sourceStart * Channels, firstFrames * Channels)
                .CopyTo(destination.Slice(0, firstFrames * Channels));

            var remainingFrames = availableFrames - firstFrames;
            if (remainingFrames > 0)
            {
                _data.AsSpan(0, remainingFrames * Channels)
                    .CopyTo(destination.Slice(firstFrames * Channels, remainingFrames * Channels));
            }

            return availableFrames;
        }
    }
}
