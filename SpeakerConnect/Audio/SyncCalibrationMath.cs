namespace SpeakerConnect.Audio;

/// <summary>
/// One timing trial for one speaker. <see cref="ArrivalOffsetSeconds"/> is the
/// microphone-detected chirp time minus the known content time of that chirp on the
/// shared routing timeline. All speakers share the same arbitrary recording-origin
/// offset, so only differences between speakers are used for calibration.
/// </summary>
public readonly record struct SyncTimingTrial(
    double ArrivalOffsetSeconds,
    double ConfidencePercent,
    bool Found,
    double Correlation,
    double PeakToSidelobeRatio,
    double CoherentSignalToNoiseDb);

/// <summary>Aggregated result of the repeated trials for one speaker.</summary>
public sealed record SyncTrialSummary(
    bool Reliable,
    double ArrivalOffsetMilliseconds,
    double ConfidencePercent,
    double SpreadMilliseconds,
    string? FailureReason);

/// <summary>
/// Pure synchronization math. It never adjusts the loopback source: the source is
/// rendered directly by Windows and cannot be delayed by this application. Destinations
/// are delayed to the latest arriving reliable speaker (the acoustic anchor). If the
/// source is earlier than that anchor, the deficit is reported instead of pretending
/// that adding delay to the already-late copies can fix it.
/// </summary>
public static class SyncCalibrationMath
{
    public const int MaximumDelayMilliseconds = 5_000;
    public const double MinimumConfidencePercent = 50.0;
    public const double MinimumTrialAgreementMilliseconds = 8.0;
    public const double MaximumTrialSpreadMilliseconds = 20.0;
    public const double AlignedToleranceMilliseconds = 1.5;

    /// <summary>
    /// Combines repeated correlation trials. Two trials must both be found, agree
    /// within a few milliseconds, and have reasonable correlation quality.
    /// </summary>
    public static SyncTrialSummary SummarizeTrials(IReadOnlyList<SyncTimingTrial> trials)
    {
        if (trials is null || trials.Count == 0)
        {
            return new SyncTrialSummary(false, 0, 0, 0, "No timing trials were recorded.");
        }

        var found = trials.Where(trial => trial.Found).ToList();
        if (found.Count < Math.Min(2, trials.Count))
        {
            return new SyncTrialSummary(
                false,
                0,
                0,
                0,
                "At least one chirp trial could not be identified in the microphone recording.");
        }

        var lags = found.Select(trial => trial.ArrivalOffsetSeconds).OrderBy(value => value).ToList();
        var median = lags.Count % 2 == 1
            ? lags[lags.Count / 2]
            : (lags[lags.Count / 2 - 1] + lags[lags.Count / 2]) / 2;
        var spreadMs = (lags[^1] - lags[0]) * 1000.0;
        var averageConfidence = found.Average(trial => trial.ConfidencePercent);
        var agreementScore = Math.Clamp(
            1.0 - spreadMs / Math.Max(1.0, MinimumTrialAgreementMilliseconds * 2),
            0,
            1);
        var confidence = Math.Clamp(
            0.65 * averageConfidence + 35.0 * agreementScore,
            0,
            100);

        if (spreadMs > MaximumTrialSpreadMilliseconds)
        {
            return new SyncTrialSummary(
                false,
                median * 1000.0,
                confidence,
                spreadMs,
                $"The repeated chirp trials disagree by {spreadMs:0.0} ms " +
                $"(limit {MaximumTrialSpreadMilliseconds:0.0} ms).");
        }

        if (confidence < MinimumConfidencePercent)
        {
            return new SyncTrialSummary(
                false,
                median * 1000.0,
                confidence,
                spreadMs,
                $"The measurement confidence is only {confidence:0}% " +
                $"(minimum {MinimumConfidencePercent:0}%).");
        }

        return new SyncTrialSummary(
            true,
            median * 1000.0,
            confidence,
            spreadMs,
            null);
    }

    /// <summary>
    /// Builds a calibration plan. Any unreliable destination fails the whole plan:
    /// silently skipping one speaker would leave the group partially calibrated.
    /// An unreliable source is reported separately because the app cannot control it
    /// anyway; the destinations can still be aligned with each other.
    /// </summary>
    public static SyncCalibrationPlan Plan(
        IReadOnlyList<SyncSpeakerMeasurement> measurements,
        string? selectedReferenceDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var destinations = measurements.Where(measurement => !measurement.IsSource).ToList();
        if (destinations.Count == 0)
        {
            return SyncCalibrationPlan.Failed(
                "Auto Sync needs at least one routed destination in addition to the source.",
                measurements);
        }

        var unreliableDestination = destinations.FirstOrDefault(measurement => !measurement.IsReliable);
        if (unreliableDestination is not null)
        {
            return SyncCalibrationPlan.Failed(
                $"\"{unreliableDestination.DisplayName}\" could not be measured reliably. " +
                "Auto Sync made no changes. Move the microphone, reduce background noise, " +
                "or retry; manual sync remains available. " +
                (unreliableDestination.FailureReason ?? string.Empty),
                measurements);
        }

        var source = measurements.FirstOrDefault(measurement => measurement.IsSource);
        var sourceReliable = source is { IsReliable: true };
        var reliable = measurements.Where(measurement => measurement.IsReliable).ToList();

        if (reliable.Count == 0)
        {
            return SyncCalibrationPlan.Failed(
                "No reliable timing measurements were obtained. Auto Sync made no changes.",
                measurements);
        }

        var withInherent = reliable
            .Select(measurement => new InherentMeasurement(
                measurement,
                measurement.ArrivalOffsetMilliseconds -
                (measurement.IsSource ? 0 : measurement.ConfiguredDelayMilliseconds)))
            .ToList();

        var anchor = withInherent
            .OrderByDescending(item => item.InherentLatencyMilliseconds)
            .ThenBy(item => item.Measurement.IsSource ? 0 : 1)
            .First();
        var target = anchor.InherentLatencyMilliseconds;

        var recommendations = new List<SyncDelayRecommendation>();
        var maxResidual = 0.0;

        foreach (var item in withInherent)
        {
            var measurement = item.Measurement;
            var needed = target - item.InherentLatencyMilliseconds;

            if (measurement.IsSource)
            {
                var earlier = needed > AlignedToleranceMilliseconds;
                recommendations.Add(new SyncDelayRecommendation(
                    measurement.DeviceId,
                    measurement.DisplayName,
                    IsSource: true,
                    ConfiguredDelayMilliseconds: 0,
                    SuggestedDelayMilliseconds: 0,
                    item.InherentLatencyMilliseconds,
                    needed,
                    measurement.ConfidencePercent,
                    measurement.IsReliable,
                    earlier
                        ? $"Source is {needed:0} ms earlier and cannot be delayed."
                        : "Source is the latest reliable path; copies are delayed to match it."));
                continue;
            }

            var suggested = (int)Math.Round(needed, MidpointRounding.AwayFromZero);
            if (suggested < 0)
            {
                suggested = 0;
            }

            if (suggested > MaximumDelayMilliseconds)
            {
                return SyncCalibrationPlan.Failed(
                    $"\"{measurement.DisplayName}\" would need {suggested} ms of delay, " +
                    $"above the {MaximumDelayMilliseconds} ms limit. Auto Sync made no changes.",
                    measurements);
            }

            var residual = Math.Abs(
                item.InherentLatencyMilliseconds + suggested - target);
            maxResidual = Math.Max(maxResidual, residual);

            recommendations.Add(new SyncDelayRecommendation(
                measurement.DeviceId,
                measurement.DisplayName,
                IsSource: false,
                measurement.ConfiguredDelayMilliseconds,
                suggested,
                item.InherentLatencyMilliseconds,
                needed,
                measurement.ConfidencePercent,
                measurement.IsReliable,
                suggested == 0
                    ? "Latest reliable path; used as the acoustic anchor."
                    : $"Add {suggested} ms to align with \"{anchor.Measurement.DisplayName}\"."));
        }

        var sourceDeficit = 0.0;
        var sourceEarlier = false;
        string? anchorNote = null;

        if (sourceReliable && source is not null)
        {
            sourceDeficit = Math.Max(0, target - source.ArrivalOffsetMilliseconds);
            sourceEarlier = sourceDeficit > AlignedToleranceMilliseconds;
        }
        else if (source is not null)
        {
            recommendations.Add(new SyncDelayRecommendation(
                source.DeviceId,
                source.DisplayName,
                IsSource: true,
                ConfiguredDelayMilliseconds: 0,
                SuggestedDelayMilliseconds: 0,
                0,
                0,
                source.ConfidencePercent,
                false,
                source.FailureReason ?? "The source could not be measured; it was not included in the anchor."));
        }

        if (selectedReferenceDeviceId is not null &&
            !string.Equals(selectedReferenceDeviceId, anchor.Measurement.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            var reference = measurements.FirstOrDefault(measurement =>
                string.Equals(measurement.DeviceId, selectedReferenceDeviceId, StringComparison.OrdinalIgnoreCase));
            if (reference is not null && reference.IsReliable)
            {
                var referenceInherent = reference.ArrivalOffsetMilliseconds -
                                        (reference.IsSource ? 0 : reference.ConfiguredDelayMilliseconds);
                var referenceOffset = target - referenceInherent;
                if (referenceOffset > AlignedToleranceMilliseconds)
                {
                    anchorNote =
                        $"Selected reference \"{reference.DisplayName}\" is " +
                        $"{referenceOffset:0} ms earlier than the acoustic anchor and " +
                        "will also be delayed to match the latest speaker.";
                }
            }
        }

        var overallConfidence = withInherent
            .Min(item => item.Measurement.ConfidencePercent);

        if (overallConfidence < MinimumConfidencePercent)
        {
            return SyncCalibrationPlan.Failed(
                $"Calibration confidence is only {overallConfidence:0}% " +
                $"(minimum {MinimumConfidencePercent:0}%). Auto Sync made no changes.",
                measurements,
                recommendations,
                anchor.Measurement.DeviceId,
                anchor.Measurement.DisplayName,
                target,
                sourceDeficit,
                sourceEarlier,
                sourceReliable,
                overallConfidence,
                maxResidual);
        }

        var message = sourceEarlier
            ? $"Aligned {destinations.Count} destination(s) to the latest measured path " +
              $"\"{anchor.Measurement.DisplayName}\". The source is {sourceDeficit:0} ms earlier " +
              "and cannot be delayed by this application."
            : sourceReliable
                ? $"Aligned {destinations.Count} destination(s) to the measured source timing."
                : $"Aligned {destinations.Count} destination(s) to the latest measured destination " +
                  $"\"{anchor.Measurement.DisplayName}\". The source was not measured reliably.";

        return new SyncCalibrationPlan(
            true,
            message,
            anchor.Measurement.DeviceId,
            anchor.Measurement.DisplayName,
            selectedReferenceDeviceId,
            target,
            sourceDeficit,
            sourceEarlier,
            sourceReliable,
            overallConfidence,
            recommendations,
            maxResidual,
            anchorNote,
            measurements);
    }

    private sealed record InherentMeasurement(
        SyncSpeakerMeasurement Measurement,
        double InherentLatencyMilliseconds);
}

/// <summary>One measured speaker before planning. Offsets include the configured delay.</summary>
public sealed record SyncSpeakerMeasurement(
    string DeviceId,
    string DisplayName,
    bool IsSource,
    int ConfiguredDelayMilliseconds,
    double ArrivalOffsetMilliseconds,
    double ConfidencePercent,
    bool IsReliable,
    string? FailureReason);

public sealed record SyncDelayRecommendation(
    string DeviceId,
    string DisplayName,
    bool IsSource,
    int ConfiguredDelayMilliseconds,
    int SuggestedDelayMilliseconds,
    double InherentLatencyMilliseconds,
    double RelativeOffsetMilliseconds,
    double ConfidencePercent,
    bool IsReliable,
    string Note);

public sealed record SyncCalibrationPlan(
    bool Success,
    string Message,
    string? AcousticAnchorDeviceId,
    string? AcousticAnchorDisplayName,
    string? ReferenceDeviceId,
    double AnchorInherentLatencyMilliseconds,
    double SourceDeficitMilliseconds,
    bool SourceIsEarlier,
    bool SourceMeasured,
    double OverallConfidencePercent,
    IReadOnlyList<SyncDelayRecommendation> Recommendations,
    double MaxResidualMilliseconds,
    string? AnchorNote,
    IReadOnlyList<SyncSpeakerMeasurement> Measurements)
{
    public static SyncCalibrationPlan Failed(
        string message,
        IReadOnlyList<SyncSpeakerMeasurement> measurements,
        IReadOnlyList<SyncDelayRecommendation>? recommendations = null,
        string? anchorId = null,
        string? anchorName = null,
        double anchorLatency = 0,
        double sourceDeficit = 0,
        bool sourceEarlier = false,
        bool sourceMeasured = false,
        double overallConfidence = 0,
        double maxResidual = 0)
    {
        return new SyncCalibrationPlan(
            false,
            message,
            anchorId,
            anchorName,
            null,
            anchorLatency,
            sourceDeficit,
            sourceEarlier,
            sourceMeasured,
            overallConfidence,
            recommendations ?? Array.Empty<SyncDelayRecommendation>(),
            maxResidual,
            null,
            measurements);
    }
}
