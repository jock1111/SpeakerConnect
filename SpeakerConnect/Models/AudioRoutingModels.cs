using SpeakerConnect.Audio;

namespace SpeakerConnect.Models;

public enum AudioRoutingState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public enum AudioChannelState
{
    Stopped,
    Playing,
    Faulted
}

/// <summary>
/// A destination endpoint that receives a copy of the loopback capture.
/// </summary>
public sealed record AudioOutputTarget(string DeviceId, float Volume, int DelayMilliseconds);

/// <summary>
/// Request passed to the routing engine. The source endpoint is the endpoint whose
/// WASAPI loopback stream is captured; it always continues to play through Windows.
/// When no profile is given the engine behaves as before the latency-profile work
/// (the "Stable" preset).
/// </summary>
public sealed record AudioRoutingRequest(
    string SourceDeviceId,
    IReadOnlyList<AudioOutputTarget> Outputs,
    BufferProfile? Profile = null);

public sealed record AudioChannelStatus(
    string DeviceId,
    string DeviceName,
    AudioChannelState State,
    float Volume,
    int DelayMilliseconds,
    double BufferedMilliseconds,
    long UnderrunCount,
    long DroppedFrameCount,
    string? Error,
    string ProfileName,
    int BaseLatencyMilliseconds,
    int WasapiLatencyMilliseconds)
{
    /// <summary>
    /// Estimated software routing latency: jitter-buffer target + user delay +
    /// WASAPI buffer size. This is NOT acoustic latency: endpoint hardware latency,
    /// Bluetooth codec/transport delay, and clock drift are not included because the
    /// application cannot measure them.
    /// </summary>
    public double EstimatedRoutingLatencyMilliseconds =>
        BaseLatencyMilliseconds + DelayMilliseconds + WasapiLatencyMilliseconds;
}

/// <summary>
/// Engine-wide roll-up for the UI diagnostics strip. All numbers are software-side
/// measurements; acoustic latency remains unknown and is never part of this summary.
/// </summary>
public sealed record RoutingDiagnosticsSummary(
    AudioRoutingState State,
    int ActiveChannels,
    string ProfileName,
    double MaxBufferedMilliseconds,
    double MaxEstimatedRoutingLatencyMilliseconds,
    long TotalUnderruns,
    long TotalDroppedFrames);

public sealed record RoutingStartResult(
    bool Started,
    string Message,
    IReadOnlyList<AudioChannelStatus> Channels,
    IReadOnlyList<string> Errors);
