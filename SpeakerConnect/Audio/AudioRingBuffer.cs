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
    private readonly List<ScheduledSignal> _scheduledSignals = new();
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

    /// <summary>
    /// Copies a range of previously captured frames for offline timing analysis.
    /// Returns false when the requested range is not entirely retained; the caller
    /// must treat that as a failed measurement rather than correlating stale data.
    /// </summary>
    public bool TryCopyFrames(long startFrame, int frameCount, float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (frameCount < 0 || startFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (destination.Length < frameCount * Channels)
        {
            throw new ArgumentException("The destination buffer is too small.", nameof(destination));
        }

        lock (_gate)
        {
            var oldest = Math.Max(0, _writeFrame - _capacityFrames);
            if (startFrame < oldest || startFrame + frameCount > _writeFrame)
            {
                return false;
            }

            var start = (int)(startFrame % _capacityFrames);
            var firstFrames = Math.Min(frameCount, _capacityFrames - start);
            Array.Copy(
                _data,
                start * Channels,
                destination,
                0,
                firstFrames * Channels);

            var remainingFrames = frameCount - firstFrames;
            if (remainingFrames > 0)
            {
                Array.Copy(
                    _data,
                    0,
                    destination,
                    firstFrames * Channels,
                    remainingFrames * Channels);
            }

            return true;
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

    /// <summary>
    /// Schedules a stereo test signal to be mixed into the shared content stream at
    /// an exact future frame. The capture writer mixes it while writing that frame,
    /// so the signal is heard on every destination with the same content-time anchor
    /// and the destination's normal configured delay. The start frame must be far
    /// enough ahead that readers have not already passed it; the routing engine uses
    /// a 300 ms lead, which is larger than every profile's base latency.
    /// </summary>
    public long ScheduleSignal(long startFrame, ReadOnlySpan<float> interleavedSamples)
    {
        var frames = interleavedSamples.Length / Channels;
        if (frames <= 0)
        {
            throw new ArgumentException("The scheduled signal is empty.", nameof(interleavedSamples));
        }

        if (startFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }

        var copy = interleavedSamples.ToArray();
        lock (_gate)
        {
            if (startFrame < _writeFrame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startFrame),
                    "The scheduled signal starts in the past.");
            }

            if (startFrame - _writeFrame > _capacityFrames - frames)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startFrame),
                    "The scheduled signal does not fit in the ring buffer's future capacity.");
            }

            _scheduledSignals.Add(new ScheduledSignal(startFrame, copy));
            return startFrame;
        }
    }

    /// <summary>Drops any test signals that have not yet been mixed.</summary>
    public void ClearScheduledSignals()
    {
        lock (_gate)
        {
            _scheduledSignals.Clear();
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
            var blockStart = _writeFrame;
            var start = (int)(blockStart % _capacityFrames);
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

            if (_scheduledSignals.Count > 0)
            {
                MixScheduledSignalsLocked(blockStart, frames);
            }
        }
    }

    private void MixScheduledSignalsLocked(long blockStart, int blockFrames)
    {
        var blockEnd = blockStart + blockFrames;
        for (var index = _scheduledSignals.Count - 1; index >= 0; index--)
        {
            var signal = _scheduledSignals[index];
            var signalEnd = signal.StartFrame + signal.Frames;
            var overlapStart = Math.Max(blockStart, signal.StartFrame);
            var overlapEnd = Math.Min(blockEnd, signalEnd);

            if (overlapEnd > overlapStart)
            {
                var destinationFrame = overlapStart - blockStart;
                var sourceFrame = (int)(overlapStart - signal.StartFrame);
                var overlapFrames = (int)(overlapEnd - overlapStart);
                MixFramesLocked(destinationFrame, signal.Samples, sourceFrame, overlapFrames);
            }

            if (signalEnd <= blockEnd)
            {
                _scheduledSignals.RemoveAt(index);
            }
        }
    }

    private void MixFramesLocked(
        long destinationFrame,
        float[] source,
        int sourceFrame,
        int frames)
    {
        var start = (int)(destinationFrame % _capacityFrames);
        var firstFrames = Math.Min(frames, _capacityFrames - start);
        var sourceStart = sourceFrame * Channels;
        var destinationStart = start * Channels;

        for (var sample = 0; sample < firstFrames * Channels; sample++)
        {
            _data[destinationStart + sample] += source[sourceStart + sample];
        }

        var remainingFrames = frames - firstFrames;
        if (remainingFrames > 0)
        {
            var remainingSourceStart = (sourceFrame + firstFrames) * Channels;
            for (var sample = 0; sample < remainingFrames * Channels; sample++)
            {
                _data[sample] += source[remainingSourceStart + sample];
            }
        }
    }

    private sealed class ScheduledSignal
    {
        public ScheduledSignal(long startFrame, float[] samples)
        {
            StartFrame = startFrame;
            Samples = samples;
            Frames = samples.Length / Channels;
        }

        public long StartFrame { get; }

        public float[] Samples { get; }

        public int Frames { get; }
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
