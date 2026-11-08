namespace SpeakerConnect.Audio;

/// <summary>
/// Rolling-window detector for playback instability on one destination. Every
/// monitor tick the engine feeds the channel's cumulative underrun/dropped-frame
/// counters; when new events within the window cross a threshold the channel is
/// escalated to a more robust buffer profile.
///
/// Underruns are treated as the stronger signal (they indicate the render thread
/// ran out of captured audio), while dropped frames indicate the capture stream
/// overtook the reader and the jitter buffer had to skip ahead.
/// </summary>
public sealed class StabilityMonitor
{
    public const int TickPeriodMilliseconds = 250;
    public const int WindowMilliseconds = 4_000;

    /// <summary>New underruns within the window that trigger an escalation.</summary>
    public const int UnderrunThreshold = 6;

    /// <summary>New dropped frames within the window that trigger an escalation.</summary>
    public const int DropThreshold = 12;

    private const int WindowTicks = WindowMilliseconds / TickPeriodMilliseconds;

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Underruns, long Drops)> _previous =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<(long Underruns, long Drops)>> _window =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Feeds one tick of cumulative counters for a channel. The first call primes
    /// the baseline and never escalates; subsequent calls accumulate per-tick deltas
    /// into the rolling window.
    /// </summary>
    /// <returns>True when the channel should be escalated.</returns>
    public bool Evaluate(string deviceId, long underrunCount, long droppedFrameCount)
    {
        lock (_gate)
        {
            if (!_previous.TryGetValue(deviceId, out var baseline))
            {
                _previous[deviceId] = (underrunCount, droppedFrameCount);
                _window[deviceId] = new Queue<(long, long)>();
                return false;
            }

            var underrunDelta = Math.Max(0, underrunCount - baseline.Underruns);
            var dropDelta = Math.Max(0, droppedFrameCount - baseline.Drops);
            _previous[deviceId] = (underrunCount, droppedFrameCount);

            var window = _window[deviceId];
            window.Enqueue((underrunDelta, dropDelta));
            while (window.Count > WindowTicks)
            {
                window.Dequeue();
            }

            long windowUnderruns = 0;
            long windowDrops = 0;
            foreach (var (underruns, drops) in window)
            {
                windowUnderruns += underruns;
                windowDrops += drops;
            }

            return windowUnderruns >= UnderrunThreshold || windowDrops >= DropThreshold;
        }
    }

    /// <summary>Discards the history of one channel, e.g. after a rebuild.</summary>
    public void Reset(string deviceId)
    {
        lock (_gate)
        {
            _previous.Remove(deviceId);
            _window.Remove(deviceId);
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _previous.Clear();
            _window.Clear();
        }
    }
}
