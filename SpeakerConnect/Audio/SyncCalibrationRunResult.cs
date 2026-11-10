namespace SpeakerConnect.Audio;

/// <summary>
/// Outcome of one automatic synchronization run. <see cref="Plan"/> is null only when
/// the run could not obtain measurements at all; a completed run that failed its
/// confidence gates still carries the per-speaker measurements for the UI.
/// </summary>
public sealed record SyncCalibrationRunResult(
    bool Success,
    string Message,
    SyncCalibrationPlan? Plan,
    IReadOnlyList<SyncSpeakerMeasurement> Measurements,
    DateTimeOffset MeasuredAtUtc)
{
    public static SyncCalibrationRunResult Failed(string message) =>
        new(
            false,
            message,
            null,
            Array.Empty<SyncSpeakerMeasurement>(),
            DateTimeOffset.UtcNow);
}
