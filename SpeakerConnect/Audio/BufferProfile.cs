namespace SpeakerConnect.Audio;

using SpeakerConnect.Models;

/// <summary>
/// A latency/buffering preset. <see cref="BaseLatencyMs"/> is the shared jitter-buffer
/// lag behind the loopback capture; <see cref="WasapiLatencyMs"/> is the buffer size
/// requested from each WASAPI render stream. Lower values reduce routing delay but
/// are more likely to glitch on Bluetooth/virtual endpoints; the engine escalates a
/// destination to the next profile automatically when it detects instability.
/// </summary>
public sealed record BufferProfile(
    string DisplayName,
    int BaseLatencyMs,
    int WasapiLatencyMs,
    int WarmupMs,
    double DriftWindowMs,
    string Description)
{
    public static readonly BufferProfile LowLatency = new(
        "Low latency",
        BaseLatencyMs: 60,
        WasapiLatencyMs: 50,
        WarmupMs: 150,
        DriftWindowMs: 30,
        Description:
            "~60 ms software routing buffer. Lowest latency, but Bluetooth and some " +
            "hardware endpoints may glitch; those destinations fall back automatically.");

    public static readonly BufferProfile Balanced = new(
        "Balanced",
        BaseLatencyMs: 90,
        WasapiLatencyMs: 80,
        WarmupMs: 200,
        DriftWindowMs: 40,
        Description:
            "~90 ms software routing buffer. A compromise between latency and stability.");

    public static readonly BufferProfile Stable = new(
        "Stable",
        BaseLatencyMs: 150,
        WasapiLatencyMs: 100,
        WarmupMs: 250,
        DriftWindowMs: 50,
        Description:
            "~150 ms software routing buffer (the previous fixed behavior). The most " +
            "robust choice for Bluetooth and heavily loaded systems.");

    public static readonly BufferProfile Default = Balanced;

    public static readonly IReadOnlyList<BufferProfile> All = new[]
    {
        LowLatency, Balanced, Stable
    };

    /// <summary>
    /// The next, more robust profile, or null when this profile is already the most
    /// robust one. Used by the automatic stability fallback.
    /// </summary>
    public BufferProfile? Escalate()
    {
        if (ReferenceEquals(this, LowLatency))
        {
            return Balanced;
        }

        if (ReferenceEquals(this, Balanced))
        {
            return Stable;
        }

        return null;
    }

    /// <summary>
    /// WASAPI buffer sizes attempted in order when an endpoint rejects the requested
    /// one. The profile value first, then permissive fallbacks. Larger values are
    /// accepted by essentially every endpoint.
    /// </summary>
    public static IReadOnlyList<int> GetWasapiLatencyCandidates(int requestedLatencyMs)
    {
        var requested = Math.Clamp(requestedLatencyMs, 20, 200);
        var candidates = new List<int> { requested };
        foreach (var fallback in new[] { 100, 200 })
        {
            if (fallback > requested)
            {
                candidates.Add(fallback);
            }
        }

        return candidates;
    }

    public override string ToString() => DisplayName;
}
