using NAudio.Wave;

namespace SpeakerConnect.Audio;

/// <summary>
/// Pull-based reader for the shared loopback ring buffer. Each output channel owns one
/// reader with its own cursor, so the same capture stream can feed several endpoints
/// at different delays without copying the stream five times.
///
/// The reader keeps a configurable lag (base latency + per-device delay) behind the
/// capture writer. If the render clock runs faster than the capture clock it inserts
/// silence; if it runs slower it skips forward. The small amount of correction keeps
/// latency bounded instead of letting the buffer grow forever.
/// </summary>
public sealed class JitterBufferSampleProvider : ISampleProvider
{
    private const int Channels = AudioRingBuffer.Channels;
    private const int MaxDelayMilliseconds = 5_000;

    /// <summary>Maximum fade-in length after a silence/underrun period, in frames (5 ms at 48 kHz).</summary>
    private const int MaxFadeInFrames = 240;

    private readonly AudioRingBuffer _buffer;
    private readonly long _baseLatencyFrames;
    private readonly long _maxDriftFrames;
    private readonly long _maxPacingFramesPerCall;
    private readonly long _maxPacingBudgetFrames;

    private long _readFrame;
    private long _currentTargetLagFrames;
    private long _pauseTargetLagFrames;
    private long _startFrame = -1;
    private bool _primed;
    private bool _inUnderrun;

    // Gradual drift pacing: instead of jumping the read cursor to the target lag in
    // one step, the reader sheds or inserts at most a few frames per render callback
    // until the lag returns to the target. A 25-50 ms jump is audible as a distinct
    // glitch; sprinkling the same correction over ~50 one-frame steps is nearly
    // inaudible and keeps measureably less dead time.
    private long _pendingShedFrames;
    private long _pendingInsertFrames;

    private int _delayMilliseconds;
    private long _underrunCount;
    private long _droppedFrameCount;
    private long _shedFrameCount;
    private long _insertedFrameCount;

    // Pause/resume tracking: number of consecutive frames emitted as silence during
    // the current underrun run, used to decide whether to re-anchor on resume and to
    // apply a click-free fade-in when audio returns.
    private long _silenceRunFrames;
    private int _fadeInFramesRemaining;

    public JitterBufferSampleProvider(
        AudioRingBuffer buffer,
        int baseLatencyMilliseconds,
        int delayMilliseconds,
        double driftWindowMilliseconds = 50)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if (baseLatencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseLatencyMilliseconds));
        }

        if (driftWindowMilliseconds is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(driftWindowMilliseconds));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, Channels);
        BaseLatencyMilliseconds = baseLatencyMilliseconds;
        _baseLatencyFrames = (long)Math.Round(buffer.SampleRate * baseLatencyMilliseconds / 1000.0);
        _maxDriftFrames = (long)Math.Round(buffer.SampleRate * driftWindowMilliseconds / 1000.0);

        // Pace corrections across many callbacks: ~0.5 ms of shed/insert per pull,
        // with a backlog that must not exceed the drift window itself.
        _maxPacingFramesPerCall = Math.Max(1, buffer.SampleRate / 2_000);
        _maxPacingBudgetFrames = Math.Max(_maxPacingFramesPerCall, _maxDriftFrames);
        _delayMilliseconds = Math.Clamp(delayMilliseconds, 0, MaxDelayMilliseconds);
        _currentTargetLagFrames = TargetLagFrames;
        _silenceRunFrames = 0;
        _fadeInFramesRemaining = 0;
    }

    /// <summary>
    /// Re-anchors the read cursor to the current capture position minus the target
    /// lag. Used after a long silence/underrun so the reader skips stale silence and
    /// resumes with the correct playback latency instead of playing through a backlog
    /// of silence frames. Safe to call from any thread.
    /// </summary>
    public void ReAnchorToCapture()
    {
        var (writeFrame, oldestFrame) = _buffer.GetSnapshot();
        var newReadFrame = Math.Max(oldestFrame, writeFrame - _currentTargetLagFrames);
        Volatile.Write(ref _readFrame, newReadFrame);
        _pendingShedFrames = 0;
        _pendingInsertFrames = 0;
        _silenceRunFrames = 0;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>The configured jitter-buffer lag in milliseconds (profile base latency).</summary>
    public int BaseLatencyMilliseconds { get; }

    /// <summary>Configured drift-correction half window in milliseconds.</summary>
    public double DriftWindowMilliseconds => _maxDriftFrames * 1000.0 / _buffer.SampleRate;

    public int DelayMilliseconds
    {
        get => Volatile.Read(ref _delayMilliseconds);
        set => Volatile.Write(ref _delayMilliseconds, Math.Clamp(value, 0, MaxDelayMilliseconds));
    }

    /// <summary>
    /// Anchors the first played sample to a frame in the shared capture stream. The
    /// engine uses the capture start frame so every destination renders the same
    /// content timeline instead of priming at whatever frame its render thread happens
    /// to wake up on. Per-device delay is still applied on top of that anchor.
    /// </summary>
    public void StartAtFrame(long startFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        Volatile.Write(ref _startFrame, startFrame);
    }

    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    /// <summary>Frames removed by gradual drift correction (shed-ahead pacing).</summary>
    public long ShedFrameCount => Interlocked.Read(ref _shedFrameCount);

    /// <summary>Frames of silence inserted by gradual drift correction.</summary>
    public long InsertedFrameCount => Interlocked.Read(ref _insertedFrameCount);

    public double BufferedMilliseconds
    {
        get
        {
            var (writeFrame, _) = _buffer.GetSnapshot();
            var bufferedFrames = Math.Max(0, writeFrame - Interlocked.Read(ref _readFrame));
            return bufferedFrames * 1000.0 / _buffer.SampleRate;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var framesRequested = count / Channels;
        if (framesRequested <= 0)
        {
            return 0;
        }

        var samplesRequested = framesRequested * Channels;
        var (writeFrame, oldestFrame) = _buffer.GetSnapshot();
        var targetLag = TargetLagFrames;

        if (!_primed)
        {
            _currentTargetLagFrames = targetLag;

            // Wait until enough history exists to fill the requested latency.
            if (writeFrame - oldestFrame < targetLag)
            {
                FillSilence(buffer, offset, samplesRequested);
                return samplesRequested;
            }

            var startFrame = Volatile.Read(ref _startFrame);
            _readFrame = startFrame >= 0
                ? Math.Max(oldestFrame, Math.Min(startFrame, writeFrame - targetLag))
                : Math.Max(oldestFrame, writeFrame - targetLag);
            _primed = true;
        }
        else if (targetLag != _currentTargetLagFrames)
        {
            _currentTargetLagFrames = targetLag;

            if (targetLag > writeFrame - _readFrame)
            {
                // Increasing delay must pause consumption until the writer has built
                // up the additional history. It must not replay already-played audio.
                _pauseTargetLagFrames = targetLag;
            }
            else
            {
                _pauseTargetLagFrames = 0;
                _readFrame = Math.Max(oldestFrame, writeFrame - targetLag);
            }
        }

        if (_pauseTargetLagFrames > 0)
        {
            if (writeFrame - _readFrame < _pauseTargetLagFrames)
            {
                FillSilence(buffer, offset, samplesRequested);
                return samplesRequested;
            }

            _readFrame = Math.Max(oldestFrame, writeFrame - _pauseTargetLagFrames);
            _pauseTargetLagFrames = 0;
        }

        var lag = writeFrame - _readFrame;

        if (_readFrame < oldestFrame ||
            lag > _currentTargetLagFrames + _maxDriftFrames + _maxPacingBudgetFrames)
        {
            // Severe fallback: the reader fell behind, the capture overwrote unread
            // history, or drift outran the gradual pacing. Jump back to the target
            // immediately; this is a rare safety net, not the normal drift path.
            var newReadFrame = Math.Max(oldestFrame, writeFrame - _currentTargetLagFrames);
            Interlocked.Add(ref _droppedFrameCount, Math.Max(0, newReadFrame - _readFrame));
            _readFrame = newReadFrame;
            lag = writeFrame - _readFrame;
            _inUnderrun = false;
            _pendingShedFrames = 0;
            _pendingInsertFrames = 0;
            _silenceRunFrames = 0;
        }

        if (lag <= 0)
        {
            // Capture has stopped or the cursor caught up with the writer. Nothing to
            // play; hold position (never rewind) and count the starvation.
            if (!_inUnderrun)
            {
                Interlocked.Increment(ref _underrunCount);
                _inUnderrun = true;
            }

            _silenceRunFrames += framesRequested;

            // Suspend drift correction during silence so frame sheds/inserts don't
            // accumulate while there is no audio to correct.
            _pendingShedFrames = 0;
            _pendingInsertFrames = 0;

            FillSilence(buffer, offset, samplesRequested);
            return samplesRequested;
        }

        // Audio has returned after a possible silence period. If the reader has been
        // silent long enough that there is a significant backlog of silence in the
        // ring, re-anchor to the current capture position minus the target lag. The
        // skip happens during silence, so it is not audible. For brief underruns the
        // reader continues from its current position to avoid skipping real audio.
        if (_inUnderrun && _silenceRunFrames > _buffer.SampleRate / 2)
        {
            ReAnchorToCapture();
            lag = writeFrame - _readFrame;
            if (lag <= 0)
            {
                // Still nothing to read after re-anchor; emit silence.
                FillSilence(buffer, offset, samplesRequested);
                return samplesRequested;
            }
        }

        // Normal-path drift correction: when the lag leaves the sweet spot, queue a
        // gradual shed (lag too high) or insert (lag too low) instead of jumping the
        // cursor. Pacing must be scheduled before the hard-skip bound is reached,
        // otherwise the safety net would always fire first.
        ScheduleDriftPacing(lag);

        var availableFrames = (int)Math.Min(framesRequested, lag);

        // Gradual pacing, insert side: deliver fewer captured frames this call and pad
        // the tail with silence, advancing the cursor only by the played frames. The
        // silenced frames are simply played in the next callback — no rewind, no replay.
        var deliberateSilence = 0;
        if (_pendingInsertFrames > 0 && availableFrames > 0)
        {
            deliberateSilence = (int)Math.Min(
                Math.Min(_pendingInsertFrames, _maxPacingFramesPerCall),
                availableFrames);
            if (deliberateSilence > 0)
            {
                availableFrames -= deliberateSilence;
                _pendingInsertFrames -= deliberateSilence;
                Interlocked.Add(ref _insertedFrameCount, deliberateSilence);
            }
        }

        var availableSamples = availableFrames * Channels;
        var framesRead = _buffer.Read(_readFrame, buffer.AsSpan(offset, availableSamples), availableFrames);
        _readFrame += framesRead;

        // Gradual pacing, shed side: advance the cursor past frames that are dropped
        // instead of played (a soft skip spread over many callbacks). Only shed when
        // the ring delivered everything we asked for, so we never overtake the writer.
        if (_pendingShedFrames > 0 && framesRead == availableFrames && availableFrames > 0)
        {
            var headroom = (int)Math.Min(
                writeFrame - _readFrame,
                _maxPacingFramesPerCall);
            var shed = (int)Math.Min(_pendingShedFrames, Math.Max(0, headroom));
            if (shed > 0)
            {
                _readFrame += shed;
                _pendingShedFrames -= shed;
                Interlocked.Add(ref _shedFrameCount, shed);
            }
        }

        if (framesRead < framesRequested)
        {
            FillSilence(
                buffer,
                offset + framesRead * Channels,
                samplesRequested - framesRead * Channels);
        }

        // Underruns are counted only for TRUE starvation: the ring could not deliver
        // what it promised (starved), or the request could not be served from the
        // buffer even after deliberate pacing silence (shortServed). Gradual pacing
        // silence is a normal drift correction and must not count as an underrun.
        var starved = framesRead < availableFrames;
        var shortServed = framesRead + deliberateSilence < framesRequested;

        var justResumed = _inUnderrun;

        if (starved || shortServed)
        {
            if (!_inUnderrun)
            {
                Interlocked.Increment(ref _underrunCount);
            }

            _inUnderrun = true;
        }
        else
        {
            // Transition from silence/underrun to real audio: start a short click-free
            // fade-in so the first audio samples don't produce an audible pop.
            if (justResumed)
            {
                _inUnderrun = false;
                _silenceRunFrames = 0;
                _fadeInFramesRemaining = Math.Min(MaxFadeInFrames, framesRequested);
            }
            else
            {
                _inUnderrun = false;
            }
        }

        // Apply click-free fade-in when resuming after silence.
        if (_fadeInFramesRemaining > 0)
        {
            ApplyFadeIn(buffer, offset, samplesRequested);
        }

        return samplesRequested;
    }

    /// <summary>
    /// Applies a linear gain ramp from 0 to 1 across the buffer, preventing clicks
    /// when audio resumes after a silence period. The ramp is only applied to the
    /// first <see cref="MaxFadeInFrames"/> samples after resume.
    /// </summary>
    private void ApplyFadeIn(float[] buffer, int offset, int count)
    {
        var framesToFade = Math.Min(_fadeInFramesRemaining, count / Channels);
        if (framesToFade <= 0)
        {
            return;
        }

        var sampleCount = framesToFade * Channels;
        for (var frame = 0; frame < framesToFade; frame++)
        {
            var gain = frame / (float)Math.Max(1, framesToFade - 1);
            var baseIndex = offset + frame * Channels;
            buffer[baseIndex] *= gain;
            buffer[baseIndex + 1] *= gain;
        }

        _fadeInFramesRemaining -= framesToFade;
    }

    private void ScheduleDriftPacing(long lag)
    {
        var excess = lag - _currentTargetLagFrames;

        if (excess > _maxDriftFrames && _pendingInsertFrames == 0)
        {
            // The render clock runs slower than the capture clock: shed audio ahead,
            // rate-matched to how far the lag has drifted past the sweet spot.
            var backlog = Math.Min(excess - _maxDriftFrames, _maxPacingBudgetFrames);
            Interlocked.Add(ref _pendingShedFrames, backlog);
        }
        else if (excess < -_maxDriftFrames && _pendingShedFrames == 0)
        {
            // The render clock runs faster: pad with silence to build lag back up.
            var deficit = Math.Min(-excess - _maxDriftFrames, _maxPacingBudgetFrames);
            Interlocked.Add(ref _pendingInsertFrames, deficit);
        }
    }

    private long TargetLagFrames =>
        _baseLatencyFrames +
        (long)Math.Round(_buffer.SampleRate * DelayMilliseconds / 1000.0);

    private static void FillSilence(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
    }
}
