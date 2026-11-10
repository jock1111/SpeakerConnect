using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeakerConnect.Audio;
using SpeakerConnect.Infrastructure;
using SpeakerConnect.Models;
using SpeakerConnect.ViewModels;

var options = DiagnosticsOptions.Parse(args);

Console.WriteLine("Speaker Connect diagnostics");
Console.WriteLine("===========================");
Console.WriteLine($"Mode: {options.ModeDescription}");
Console.WriteLine();

var failures = new List<string>();
var passed = 0;
const int DiagnosticToneFrequencyHz = 1_234;

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace is not null)
        {
            foreach (var line in ex.StackTrace.Split(Environment.NewLine))
            {
                Console.WriteLine($"      {line.Trim()}");
            }
        }
    }
}

Run("device limits are five total including the source", TestDeviceLimits);
Run("ring buffer read/write and wrap", TestRingBuffer);
Run("jitter buffer latency, anchor, and continuity", TestJitterBuffer);
Run("jitter buffer delay increase and underrun handling", TestJitterDelay);
Run("jitter drift pacing (shed and insert)", TestDriftPacing);
Run("resampling chain 44.1 kHz -> 48 kHz", TestResampling);
Run("capture format conversion (8/16/24/32-bit PCM and float)", TestCaptureFormats);
Run("output format conversion (8/16/24/32-bit PCM and float)", TestOutputFormats);
Run("target format selection and fallback", TestTargetFormats);
Run("buffer profiles, escalation, and WASAPI fallback candidates", TestBufferProfiles);
Run("stability monitor escalation", TestStabilityMonitor);
Run("calibration tone burst", TestCalibrationTone);
Run("sync chirp correlation under noise", TestSyncSignalCorrelation);
Run("sync calibration plan for 2/3/5 outputs", TestSyncCalibrationPlan);
Run("sync calibration rejects drift and low confidence", TestSyncCalibrationRejection);
Run("jitter pause/resume continuity", TestJitterPauseResume);
Run("jitter shared base-latency raise", TestJitterBaseLatencyRaise);
Run("master volume sample gain (unity/trim/mute)", TestMasterVolumeSampleGain);
Run("master volume ramp is smooth under rapid changes", TestMasterVolumeRampSmoothness);
Run("volume balance gain math is attenuation-only", TestVolumeBalanceMath);
Run("volume balance application, trims, mute, and ramps", TestVolumeBalanceApplication);
Run("volume balance persistence and reconnect by device id", TestVolumeBalanceStore);
Run("device settings persistence, atomic writes, and legacy migration", TestDeviceSettingsStore);
Run("view model restores device settings across restart and reconnect", TestViewModelDeviceSettingsPersistence);
Run("pink-noise calibration signal", TestPinkNoiseSignal);

var devices = new List<AudioDeviceInfo>();

try
{
    devices = new AudioDeviceEnumerator().GetActiveRenderDevices().ToList();
    Console.WriteLine();
    Console.WriteLine($"Active render devices: {devices.Count}");
    foreach (var device in devices)
    {
        Console.WriteLine(
            $"  {(device.IsDefault ? "default" : "       ")} | {device.FriendlyName} | " +
            $"{device.FormatDescription}");
    }

    using var watcher = new AudioDeviceWatcher();
    Console.WriteLine($"Endpoint notifications registered: {watcher.IsListening}");
}
catch (Exception ex)
{
    failures.Add($"device enumeration: {ex.Message}");
    Console.WriteLine($"FAIL  device enumeration: {ex.Message}");
}

if (options.RunHardware)
{
    Run("muted multi-output hardware initialization", () => TestMutedHardware(devices));
    Run("buffer profile scan (Low/Balanced/Stable, muted)", () => TestBufferProfileScan(devices));
    Run("Windows volume notification path (real endpoint)", TestSystemVolumeNotificationPath);
    Run("source endpoint balance mapping (real endpoint)", TestSourceEndpointBalanceMapping);
}

if (options.RunSignal)
{
    Run("two-target end-to-end loopback signal", () => TestTwoTargetSignal(devices));
    Run("loopback volume behavior probe (pre/post endpoint volume)", TestLoopbackVolumeBehavior);
}

Console.WriteLine();
Console.WriteLine($"{passed} test(s) passed, {failures.Count} failed.");
if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  {failure}");
    }

    Environment.ExitCode = 1;
}

return;

static void TestDeviceLimits()
{
    Assert(
        AudioRoutingEngine.MaxTotalAudibleDevices == 5,
        "MaxTotalAudibleDevices must be 5.");
    Assert(
        AudioRoutingEngine.MaxAdditionalOutputs == 4,
        "MaxAdditionalOutputs must be 4.");
    Assert(
        AudioRoutingEngine.MaxOutputChannels == 4,
        "compatibility alias must also be 4.");

    using var watcher = new AudioDeviceWatcher();
    using var engine = new AudioRoutingEngine(new AudioDeviceEnumerator(), watcher);
    var outputs = Enumerable.Range(0, 5)
        .Select(index => new AudioOutputTarget($"missing-device-{index}", 0.5f, 0))
        .ToList();

    var result = engine.Start(new AudioRoutingRequest("missing-source", outputs));
    Assert(!result.Started, "five additional outputs must be rejected.");
    Assert(
        result.Message.Contains("at most 4", StringComparison.OrdinalIgnoreCase),
        $"the error must mention the four-output cap, got: {result.Message}");
}

static void TestRingBuffer()
{
    var ring = new AudioRingBuffer(48000, TimeSpan.FromSeconds(2));
    var write = new float[1000 * 2];
    for (var frame = 0; frame < 1000; frame++)
    {
        write[frame * 2] = frame;
        write[frame * 2 + 1] = -frame;
    }

    ring.Write(write);
    var read = new float[1000 * 2];
    var frames = ring.Read(0, read, 1000);
    Assert(frames == 1000, "ring frame count");
    Assert(read[0] == 0 && read[1] == 0 && read[999 * 2] == 999 && read[999 * 2 + 1] == -999, "ring contents");

    ring.Write(write.AsSpan(0, 400 * 2));
    frames = ring.Read(800, read, 300);
    Assert(frames == 300, "ring wrap frame count");
    Assert(read[0] == 800 && read[1] == -800, "ring wrap contents");
}

static void TestJitterBuffer()
{
    const int rate = 48000;
    const int blockFrames = 480;
    const int baseLatency = 150;
    var ring = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var jitter = new JitterBufferSampleProvider(ring, baseLatency, 0);
    jitter.StartAtFrame(0);

    var input = new float[blockFrames * 2];
    var output = new float[blockFrames * 2];
    var firstAudioBlock = -1;
    var nextExpectedFrame = 0L;
    var contiguous = true;

    for (var block = 0; block < 200; block++)
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var absoluteFrame = block * blockFrames + frame;
            input[frame * 2] = absoluteFrame * 0.001f;
            input[frame * 2 + 1] = absoluteFrame * -0.001f;
        }

        ring.Write(input);
        var read = jitter.Read(output, 0, output.Length);
        Assert(read == output.Length, "jitter returns full block");

        var hasAudio = false;
        for (var frame = 0; frame < blockFrames; frame++)
        {
            if (output[frame * 2] != 0f || output[frame * 2 + 1] != 0f)
            {
                hasAudio = true;
                break;
            }
        }

        if (hasAudio && firstAudioBlock < 0)
        {
            firstAudioBlock = block;
        }

        if (hasAudio)
        {
            for (var frame = 0; frame < blockFrames; frame++)
            {
                if (Math.Abs(output[frame * 2] - (float)(nextExpectedFrame * 0.001)) > 0.00001f)
                {
                    contiguous = false;
                    break;
                }

                nextExpectedFrame++;
            }
        }
    }

    Console.WriteLine($"      first audio block: {firstAudioBlock} (expected ~{(baseLatency / 10) - 1})");
    Console.WriteLine($"      contiguous: {contiguous}; underruns: {jitter.UnderrunCount}; drops: {jitter.DroppedFrameCount}");
    Assert(firstAudioBlock >= (baseLatency / 10) - 2, "jitter latency");
    Assert(contiguous, "jitter contiguous samples");

    // Deliberately prefill the ring slightly beyond the target latency before the
    // reader's first read. The start anchor must win over the reader's wake-up time,
    // otherwise destinations prime on different chunks of capture audio. A larger
    // prefill would legitimately trigger the bounded-latency catch-up path instead.
    var anchoredRing = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var anchoredFrames = rate * 160 / 1000; // 160 ms: target is 150 ms, drift limit is 50 ms
    var anchoredInput = new float[anchoredFrames * 2];
    for (var frame = 0; frame < anchoredFrames; frame++)
    {
        anchoredInput[frame * 2] = frame * 0.001f;
        anchoredInput[frame * 2 + 1] = frame * -0.001f;
    }

    anchoredRing.Write(anchoredInput);
    var anchored = new JitterBufferSampleProvider(anchoredRing, baseLatency, 0);
    anchored.StartAtFrame(0);
    var anchoredOutput = new float[blockFrames * 2];
    Assert(anchored.Read(anchoredOutput, 0, anchoredOutput.Length) == anchoredOutput.Length, "anchored read length");
    Assert(Math.Abs(anchoredOutput[0]) < 0.00001f, "first output sample must be capture frame 0");
    Assert(Math.Abs(anchoredOutput[2] - 0.001f) < 0.00001f, "second output frame must be capture frame 1");
}

static void TestJitterDelay()
{
    const int rate = 48000;
    const int blockFrames = 480;
    var ring = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var jitter = new JitterBufferSampleProvider(ring, 150, 0);
    var input = new float[blockFrames * 2];
    var output = new float[blockFrames * 2];

    for (var block = 0; block < 200; block++)
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var absoluteFrame = block * blockFrames + frame;
            input[frame * 2] = absoluteFrame * 0.001f;
            input[frame * 2 + 1] = absoluteFrame * -0.001f;
        }

        ring.Write(input);
        jitter.Read(output, 0, output.Length);
    }

    jitter.DelayMilliseconds = 500;
    var silenceBlocks = 0;
    var sawAudioAfterChange = false;
    for (var block = 0; block < 100; block++)
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var absoluteFrame = (200 + block) * blockFrames + frame;
            input[frame * 2] = absoluteFrame * 0.001f;
            input[frame * 2 + 1] = absoluteFrame * -0.001f;
        }

        ring.Write(input);
        jitter.Read(output, 0, output.Length);

        var hasAudio = false;
        for (var frame = 0; frame < blockFrames; frame++)
        {
            if (output[frame * 2] != 0f)
            {
                hasAudio = true;
                break;
            }
        }

        if (!sawAudioAfterChange && !hasAudio)
        {
            silenceBlocks++;
        }
        else
        {
            sawAudioAfterChange = true;
        }
    }

    Console.WriteLine($"      silence blocks after +500 ms delay: {silenceBlocks} (expected ~50)");
    Assert(silenceBlocks >= 45 && silenceBlocks <= 55, "delay increase gap");

    var underrunsBefore = jitter.UnderrunCount;

    // Drain the ring completely with no further capture. Gradual insert pacing keeps
    // serving audio for a while (that is its purpose), but once the cursor catches up
    // with the writer the provider must report a true underrun and keep returning
    // full-length silence blocks.
    for (var block = 0; block < 200; block++)
    {
        var read = jitter.Read(output, 0, output.Length);
        Assert(read == output.Length, "jitter underrun returns full block");
    }

    Assert(jitter.UnderrunCount > underrunsBefore, "underrun detected when the ring runs dry");
}

static void TestDriftPacing()
{
    const int rate = 48000;
    const int blockFrames = 480;
    const int baseLatencyMs = 20;
    const double driftWindowMs = 5;

    // Slow render clock: the writer gains 2 frames per block. Pacing must shed
    // gradually (no hard skip), return the lag near the target, and never starve.
    var shedRing = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var shed = new JitterBufferSampleProvider(shedRing, baseLatencyMs, 0, driftWindowMs);
    var input = new float[(blockFrames + 4) * AudioRingBuffer.Channels];
    var output = new float[blockFrames * 2];
    var pacingBudgetFrames = (int)Math.Round(rate * driftWindowMs / 1000);
    var previousFrame = -1L;
    var contiguous = true;

    for (var block = 0; block < 400; block++)
    {
        // Fill the two drift frames as well so the extra capture carries a
        // contiguous timeline instead of stale zeros.
        for (var frame = 0; frame < blockFrames + 2; frame++)
        {
            var absoluteFrame = block * blockFrames + frame;
            input[frame * 2] = absoluteFrame * 0.001f;
            input[frame * 2 + 1] = absoluteFrame * -0.001f;
        }

        // 2 extra frames of capture per block simulate a slow render clock.
        shedRing.Write(input.AsSpan(0, (blockFrames + 2) * AudioRingBuffer.Channels));
        shed.Read(output, 0, output.Length);

        if (output[0] != 0f)
        {
            var frame = (long)Math.Round(output[0] / 0.001);
            if (previousFrame >= 0)
            {
                var jump = frame - previousFrame;

                // Normal advance is one block (480 frames) plus up to the pacing
                // budget of shed/insert. A jump of zero or less means the reader
                // replayed audio; a huge jump means a hard skip. Both are failures.
                if (jump <= 0 || jump > blockFrames + pacingBudgetFrames + 10)
                {
                    contiguous = false;
                    Console.WriteLine(
                        $"      ! jump anomaly at block {block}: prev={previousFrame}, " +
                        $"now={frame}, jump={jump}, buffered={shed.BufferedMilliseconds:0.0} ms");
                }
            }

            previousFrame = frame;
        }
    }

    Console.WriteLine(
        $"      shed: buffered={shed.BufferedMilliseconds:0} ms, " +
        $"underruns={shed.UnderrunCount}, dropped={shed.DroppedFrameCount}, shed={shed.ShedFrameCount}");
    Assert(shed.ShedFrameCount > 0, "slow render clock must trigger shed pacing");
    Assert(shed.DroppedFrameCount == 0, "gradual pacing must avoid hard skips");
    Assert(shed.UnderrunCount == 0, "shed pacing must not starve the reader");
    Assert(shed.BufferedMilliseconds < baseLatencyMs + driftWindowMs + 5,
        "shed pacing must bring the lag back toward the target");
    Assert(contiguous, "pacing must keep the stream monotonic");

    // Fast render clock: the writer loses 2 frames per block. Pacing must insert
    // silence instead of latching into a hard underrun.
    var insertRing = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var insert = new JitterBufferSampleProvider(insertRing, baseLatencyMs, 0, driftWindowMs);
    var sawInserted = false;

    for (var block = 0; block < 400; block++)
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var absoluteFrame = block * blockFrames + frame;
            input[frame * 2] = absoluteFrame * 0.001f;
            input[frame * 2 + 1] = absoluteFrame * -0.001f;
        }

        insertRing.Write(input.AsSpan(0, (blockFrames - 2) * AudioRingBuffer.Channels));
        insert.Read(output, 0, output.Length);

        if (insert.InsertedFrameCount > 0)
        {
            sawInserted = true;
        }
    }

    Console.WriteLine(
        $"      insert: buffered={insert.BufferedMilliseconds:0} ms, " +
        $"underruns={insert.UnderrunCount}, inserted={insert.InsertedFrameCount}");
    Assert(sawInserted, "fast render clock must trigger insert pacing");
    Assert(insert.UnderrunCount <= 1, "insert pacing must prevent hard underruns");

    // With rate-matched pacing the lag settles at the correction boundary rather
    // than at the full target: the post-read cushion is target - drift - block plus
    // the pending correction. What matters is that the reader never collapsed into
    // starvation silence and still holds a usable cushion (a few ms here; the real
    // profiles keep 40+ ms because their drift windows are much larger).
    Assert(insert.BufferedMilliseconds > 2,
        "insert pacing must keep a usable playback cushion");
}

static void TestBufferProfiles()
{
    Assert(BufferProfile.LowLatency.BaseLatencyMs is >= 40 and <= 60,
        "Low latency must sit in the 40-60 ms band");
    Assert(BufferProfile.Balanced.BaseLatencyMs > BufferProfile.LowLatency.BaseLatencyMs &&
           BufferProfile.Balanced.BaseLatencyMs < BufferProfile.Stable.BaseLatencyMs,
        "Balanced must sit between Low latency and Stable");
    Assert(BufferProfile.Stable.BaseLatencyMs == 150 && BufferProfile.Stable.WasapiLatencyMs == 100,
        "Stable must preserve the previous fixed behavior (150/100 ms)");

    var escalations = new List<string>();
    var profile = BufferProfile.LowLatency;
    while (profile.Escalate() is BufferProfile next)
    {
        escalations.Add(next.DisplayName);
        profile = next;
    }

    Assert(escalations.Count == 2 &&
           escalations[0] == BufferProfile.Balanced.DisplayName &&
           escalations[1] == BufferProfile.Stable.DisplayName,
        "escalation order must be Low latency -> Balanced -> Stable");
    Assert(BufferProfile.Stable.Escalate() is null, "Stable must not escalate further");

    var candidates = BufferProfile.GetWasapiLatencyCandidates(50);
    Assert(candidates.SequenceEqual(new[] { 50, 100, 200 }),
        "WASAPI fallback candidates for 50 ms must be 50, 100, 200");
    Assert(BufferProfile.GetWasapiLatencyCandidates(200).SequenceEqual(new[] { 200 }),
        "200 ms must not produce larger fallback candidates");
    Assert(BufferProfile.GetWasapiLatencyCandidates(500).SequenceEqual(new[] { 200 }),
        "out-of-range requests must clamp to the 200 ms ceiling");
    Assert(BufferProfile.GetWasapiLatencyCandidates(10).SequenceEqual(new[] { 20, 100, 200 }),
        "out-of-range requests must clamp to the 20 ms floor");
}

static void TestStabilityMonitor()
{
    var monitor = new StabilityMonitor();
    const string device = "device-a";

    Assert(!monitor.Evaluate(device, 0, 0), "priming tick must never escalate");

    for (var tick = 0; tick < StabilityMonitor.UnderrunThreshold - 1; tick++)
    {
        Assert(!monitor.Evaluate(device, tick + 1, 0),
            $"underrun delta {tick + 1} must stay below the threshold");
    }

    Assert(monitor.Evaluate(device, StabilityMonitor.UnderrunThreshold, 0),
        "reaching the underrun threshold within the window must escalate");

    // A rebuild resets the channel history, so a fresh baseline must prime again.
    monitor.Reset(device);
    Assert(!monitor.Evaluate(device, 0, 0), "reset must clear the escalation state");

    for (var tick = 0; tick < StabilityMonitor.DropThreshold; tick++)
    {
        monitor.Evaluate(device, 0, tick + 1);
    }

    Assert(monitor.Evaluate(device, 0, StabilityMonitor.DropThreshold),
        "reaching the drop threshold within the window must escalate");

    // Old samples age out: a steady 1 underrun per tick over a long session with the
    // window repeatedly rolling must not escalate once the spike has passed.
    monitor.ResetAll();
    var escalatedDuringSpike = false;
    var escalatedAfterSpike = false;
    for (var tick = 0; tick < StabilityMonitor.WindowMilliseconds / StabilityMonitor.TickPeriodMilliseconds + 10; tick++)
    {
        var underruns = tick < 3 ? tick + 1 : 3;
        var escalate = monitor.Evaluate("device-b", underruns, 0);
        if (escalate)
        {
            if (tick < 10)
            {
                escalatedDuringSpike = true;
            }
            else
            {
                escalatedAfterSpike = true;
            }
        }
    }

    Assert(!escalatedDuringSpike || !escalatedAfterSpike,
        "a short spike must not keep escalating after it ages out of the window");
    Assert(!escalatedAfterSpike, "aged-out samples must not escalate");
}

static void TestCalibrationTone()
{
    var burst = AudioCalibrationTone.CreateBurst(48000, 0.25);
    Assert(burst.Length == (int)(48000 * 0.25) * AudioRingBuffer.Channels, "burst frame count");
    Assert(Math.Abs(burst[0]) < 0.02, "burst must fade in from silence");
    Assert(Math.Abs(burst[^1]) < 0.02, "burst must fade out to silence");

    var peak = 0f;
    for (var index = 0; index < burst.Length; index++)
    {
        peak = Math.Max(peak, Math.Abs(burst[index]));
    }

    Assert(peak > 0.15 && peak <= 0.25, $"burst peak must be audible but safe, got {peak:0.000}");
    Assert(Math.Abs(burst[burst.Length / 2] - burst[burst.Length / 2 + 1]) < 0.0001,
        "burst must be identical on both channels");
}

static void TestSyncSignalCorrelation()
{
    const int rate = 48_000;
    var chirp = SyncTestSignal.CreateChirp(rate, stereo: false);
    var random = new Random(20_261_109);
    var leadFrames = (int)Math.Round(rate * 0.42);
    var recording = new float[leadFrames + chirp.Length + rate / 2];

    for (var index = 0; index < recording.Length; index++)
    {
        recording[index] = (float)((random.NextDouble() * 2 - 1) * 0.035);
    }

    for (var index = 0; index < chirp.Length; index++)
    {
        recording[leadFrames + index] += chirp[index];
    }

    var result = SyncSignalAnalyzer.FindChirp(recording, rate, 0, recording.Length);
    Assert(result.Found, $"the chirp must be found in noisy audio: {result.FailureReason}");
    Assert(Math.Abs(result.LagSeconds - leadFrames / (double)rate) <= 0.004,
        $"detected chirp lag {result.LagSeconds * 1000:0.0} ms, expected " +
        $"{leadFrames * 1000.0 / rate:0.0} ms");
    Assert(result.ConfidencePercent >= SyncCalibrationMath.MinimumConfidencePercent,
        $"noisy chirp confidence {result.ConfidencePercent:0}% is below the gate");

    var noiseOnly = new float[rate];
    for (var index = 0; index < noiseOnly.Length; index++)
    {
        noiseOnly[index] = (float)((random.NextDouble() * 2 - 1) * 0.12);
    }

    var missing = SyncSignalAnalyzer.FindChirp(noiseOnly, rate, 0, noiseOnly.Length);
    Assert(!missing.Found, "noise alone must not be accepted as a chirp measurement");

    var truncated = SyncSignalAnalyzer.FindChirp(new float[64], rate, 0, 64);
    Assert(!truncated.Found, "a recording shorter than the chirp must fail cleanly");

    Console.WriteLine(
        $"      noisy chirp lag={result.LagSeconds * 1000:0.0} ms, " +
        $"confidence={result.ConfidencePercent:0}%, correlation={result.Correlation:0.00}");
}

static void TestSyncCalibrationPlan()
{
    var destinationLatencies = new[] { 50.0, 95.0, 150.0, 220.0 };
    var configuredDelays = new[] { 0, 40, 80, 120 };

    foreach (var destinationCount in new[] { 1, 2, 4 })
    {
        var measurements = new List<SyncSpeakerMeasurement>
        {
            new(
                "sync-source",
                "Sync Source",
                IsSource: true,
                ConfiguredDelayMilliseconds: 0,
                ArrivalOffsetMilliseconds: 35,
                ConfidencePercent: 90,
                IsReliable: true,
                FailureReason: null)
        };

        for (var index = 0; index < destinationCount; index++)
        {
            measurements.Add(new SyncSpeakerMeasurement(
                $"sync-destination-{index}",
                $"Sync Destination {index}",
                IsSource: false,
                configuredDelays[index],
                destinationLatencies[index] + configuredDelays[index],
                ConfidencePercent: 90,
                IsReliable: true,
                FailureReason: null));
        }

        var plan = SyncCalibrationMath.Plan(measurements);
        Assert(plan.Success, $"plan for {destinationCount + 1} speakers failed: {plan.Message}");

        var expectedTarget = Math.Max(35, destinationLatencies.Take(destinationCount).Max());
        Assert(Math.Abs(plan.AnchorInherentLatencyMilliseconds - expectedTarget) < 0.01,
            $"anchor latency {plan.AnchorInherentLatencyMilliseconds:0.0} ms, expected {expectedTarget:0.0} ms");
        Assert(plan.SourceIsEarlier,
            "a source at 35 ms must be reported as earlier than the measured destinations");
        Assert(Math.Abs(plan.SourceDeficitMilliseconds - (expectedTarget - 35)) < 0.01,
            "the source deficit must equal the anchor latency minus the source latency");

        foreach (var recommendation in plan.Recommendations.Where(item => !item.IsSource))
        {
            Assert(recommendation.SuggestedDelayMilliseconds >= 0,
                "a plan must never suggest a negative delay");
            Assert(Math.Abs(
                    recommendation.InherentLatencyMilliseconds +
                    recommendation.SuggestedDelayMilliseconds -
                    expectedTarget) <= 0.5,
                $"\"{recommendation.DisplayName}\" would not align after compensation");
        }

        var sourceRecommendation = plan.Recommendations.Single(item => item.IsSource);
        Assert(sourceRecommendation.SuggestedDelayMilliseconds == 0,
            "the source must never be assigned a software delay");
        Assert(sourceRecommendation.Note.Contains("cannot be delayed", StringComparison.OrdinalIgnoreCase),
            "an early source must be described as uncorrectable, not as a fix");

        // The source can also be the latest path. In that case the anchor must be the
        // source and every destination must be delayed to match it.
        measurements[0] = new SyncSpeakerMeasurement(
            "sync-source",
            "Sync Source",
            IsSource: true,
            ConfiguredDelayMilliseconds: 0,
            ArrivalOffsetMilliseconds: 300,
            ConfidencePercent: 90,
            IsReliable: true,
            FailureReason: null);

        plan = SyncCalibrationMath.Plan(measurements);
        Assert(plan.Success, $"source-latest plan failed: {plan.Message}");
        Assert(!plan.SourceIsEarlier, "a latest source must not be reported as a deficit");
        Assert(plan.AcousticAnchorDeviceId == "sync-source",
            "the source must become the acoustic anchor when it is the latest path");

        foreach (var recommendation in plan.Recommendations.Where(item => !item.IsSource))
        {
            Assert(Math.Abs(
                    recommendation.InherentLatencyMilliseconds +
                    recommendation.SuggestedDelayMilliseconds -
                    300) <= 0.5,
                "destinations must align to the source when the source is the latest path");
        }

        var referencedPlan = SyncCalibrationMath.Plan(measurements, "sync-destination-0");
        Assert(referencedPlan.Success && referencedPlan.AnchorNote is not null,
            "an earlier selected reference must be explained, not used as an impossible anchor");
    }

    // The source is the latest path here, so the earlier destination would need
    // 6000 ms of delay. That is above the engine limit and must reject the plan
    // instead of applying a partial alignment.
    var tooLate = new List<SyncSpeakerMeasurement>
    {
        new("sync-source", "Sync Source", true, 0, 6_020, 90, true, null),
        new("sync-destination", "Sync Destination", false, 0, 20, 90, true, null)
    };
    var failed = SyncCalibrationMath.Plan(tooLate);
    Assert(!failed.Success, "a required delay above the limit must fail the plan");
    Assert(failed.Message.Contains("5000", StringComparison.Ordinal),
        "the delay-limit failure must state the limit");

    Console.WriteLine("      plans verified for 2, 3, and 5 simulated speakers");
}

static void TestSyncCalibrationRejection()
{
    var goodTrials = new[]
    {
        new SyncTimingTrial(0.1000, 85, true, 0.8, 8, 22),
        new SyncTimingTrial(0.1005, 86, true, 0.8, 8, 22),
        new SyncTimingTrial(0.0995, 84, true, 0.8, 8, 22)
    };
    var good = SyncCalibrationMath.SummarizeTrials(goodTrials);
    Assert(good.Reliable, $"consistent trials must be reliable: {good.FailureReason}");
    Assert(Math.Abs(good.ArrivalOffsetMilliseconds - 100) < 1.0,
        "the trial median must preserve the measured offset");

    var driftedTrials = new[]
    {
        new SyncTimingTrial(0.100, 85, true, 0.8, 8, 22),
        new SyncTimingTrial(0.130, 85, true, 0.8, 8, 22),
        new SyncTimingTrial(0.105, 85, true, 0.8, 8, 22)
    };
    var drifted = SyncCalibrationMath.SummarizeTrials(driftedTrials);
    Assert(!drifted.Reliable, "clock drift beyond the trial window must be rejected");
    Assert(drifted.FailureReason?.Contains("disagree", StringComparison.OrdinalIgnoreCase) == true,
        "a drift failure must explain that the trials disagree");

    var lowConfidenceTrials = new[]
    {
        new SyncTimingTrial(0.100, 15, true, 0.2, 2, 3),
        new SyncTimingTrial(0.101, 16, true, 0.2, 2, 3),
        new SyncTimingTrial(0.100, 14, true, 0.2, 2, 3)
    };
    var lowConfidence = SyncCalibrationMath.SummarizeTrials(lowConfidenceTrials);
    Assert(!lowConfidence.Reliable,
        "low-correlation trials must fail even when their lags happen to agree");

    var unreliableDestination = new List<SyncSpeakerMeasurement>
    {
        new("sync-source", "Sync Source", true, 0, 40, 90, true, null),
        new("sync-destination", "Sync Destination", false, 0, 100, 20, false,
            "The chirp correlation is too weak.")
    };
    var failedPlan = SyncCalibrationMath.Plan(unreliableDestination);
    Assert(!failedPlan.Success, "one unreliable destination must fail the whole plan");
    Assert(failedPlan.Message.Contains("could not be measured reliably", StringComparison.OrdinalIgnoreCase),
        "the failure must identify the unreliable destination");

    // An unreliable source cannot be corrected, but the destinations can still be
    // aligned with each other. This must be reported honestly rather than failing
    // the entire feature.
    var unreliableSource = new List<SyncSpeakerMeasurement>
    {
        new("sync-source", "Sync Source", true, 0, 0, 10, false, "Source chirp was not found."),
        new("sync-left", "Sync Left", false, 0, 80, 90, true, null),
        new("sync-right", "Sync Right", false, 0, 150, 92, true, null)
    };
    var sourcePlan = SyncCalibrationMath.Plan(unreliableSource);
    Assert(sourcePlan.Success, $"destination-only alignment should succeed: {sourcePlan.Message}");
    Assert(!sourcePlan.SourceMeasured, "an unreliable source must not be marked as measured");
    Assert(sourcePlan.Recommendations.Single(item => item.IsSource).SuggestedDelayMilliseconds == 0,
        "an unreliable source still must not receive a software delay");
    Assert(Math.Abs(sourcePlan.AnchorInherentLatencyMilliseconds - 150) < 0.01,
        "destinations must align to the latest reliable destination when the source is unreliable");

    Console.WriteLine(
        $"      rejected drift={drifted.FailureReason}; low confidence={lowConfidence.FailureReason}");
}

static void TestJitterPauseResume()
{
    const int rate = 48_000;
    const int blockFrames = 480;
    const int baseLatencyMs = 60;
    const double driftWindowMs = 30;

    var ring = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var jitter = new JitterBufferSampleProvider(ring, baseLatencyMs, 0, driftWindowMs);
    jitter.StartAtFrame(0);
    var input = new float[blockFrames * AudioRingBuffer.Channels];
    var output = new float[blockFrames * AudioRingBuffer.Channels];
    long writeFrame = 0;

    void WriteRamp()
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var value = (float)(((writeFrame + frame) % 60_000) / 60_000.0);
            input[frame * 2] = value;
            input[frame * 2 + 1] = value;
        }

        ring.Write(input);
        writeFrame += blockFrames;
    }

    for (var block = 0; block < 120; block++)
    {
        WriteRamp();
        jitter.Read(output, 0, output.Length);
    }

    // A media pause is silence in the captured content, not a starved writer. The
    // configured pipeline latency still delivers the previously captured ramp tail
    // for the first few blocks; after that the output must be silence. The absolute
    // content timeline continues through the pause, so the resumed ramp must start
    // at the first frame written *after* the pause, never replay pre-pause audio.
    const int pauseBlocks = 30;
    var latencyBlocks = (baseLatencyMs * rate / 1000) / blockFrames;

    for (var block = 0; block < pauseBlocks; block++)
    {
        Array.Clear(input);
        ring.Write(input);
        writeFrame += blockFrames;
        jitter.Read(output, 0, output.Length);

        if (block >= latencyBlocks)
        {
            Assert(output.All(sample => sample == 0f),
                "a captured silence pause must produce silence after the pipeline latency drains");
        }
    }

    Assert(jitter.UnderrunCount == 0, "a content pause must not be counted as an underrun");

    var resumeStartFrame = writeFrame;
    var expectedResumeValue = (float)((resumeStartFrame % 60_000) / 60_000.0);
    var sawResumedAudio = false;
    var sawPrePauseAudio = false;
    var firstContentResumeValue = float.NaN;
    var previousResumedValue = float.NaN;
    var consecutiveStepViolations = 0;

    for (var block = 0; block < 40; block++)
    {
        WriteRamp();
        jitter.Read(output, 0, output.Length);

        for (var frame = 0; frame < blockFrames; frame++)
        {
            var sample = output[frame * 2];
            if (sample == 0f)
            {
                continue;
            }

            if (!sawResumedAudio)
            {
                sawResumedAudio = true;
                firstContentResumeValue = sample;
            }

            // The pre-pause ramp tail was above 0.9. Seeing it again after the pause
            // would mean the reader replayed audio instead of advancing.
            if (sample > 0.6f)
            {
                sawPrePauseAudio = true;
            }

            if (!float.IsNaN(previousResumedValue) &&
                Math.Abs(sample - previousResumedValue - (float)(1.0 / 60_000)) > 0.0001f)
            {
                consecutiveStepViolations++;
            }

            previousResumedValue = sample;
        }
    }

    Assert(sawResumedAudio, "audio must resume after a captured content pause");
    Assert(!sawPrePauseAudio, "a content pause must not replay audio from before the pause");
    Assert(consecutiveStepViolations == 0,
        "the resumed stream must advance one content frame at a time without skips");
    Assert(Math.Abs(firstContentResumeValue - expectedResumeValue) < 0.002f,
        $"the resumed content must begin at absolute frame {resumeStartFrame}: got " +
        $"{firstContentResumeValue:0.0000}, expected {expectedResumeValue:0.0000}");

    // A true capture stall is different: the writer stops. Once audio returns, the
    // reader must wait for fresh history and fade in, never replay pre-stall audio.
    var stalledRing = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var stalled = new JitterBufferSampleProvider(stalledRing, baseLatencyMs, 0, driftWindowMs);
    stalled.StartAtFrame(0);

    for (var block = 0; block < 120; block++)
    {
        Array.Fill(input, 0.9f);
        stalledRing.Write(input);
        stalled.Read(output, 0, output.Length);
    }

    var underrunsBefore = stalled.UnderrunCount;
    for (var block = 0; block < 120; block++)
    {
        stalled.Read(output, 0, output.Length);
    }

    Assert(stalled.UnderrunCount > underrunsBefore,
        "a writer stall must be counted as an underrun");

    var sawNewAudio = false;
    var sawOldAudio = false;
    var firstResumedValue = float.NaN;

    for (var block = 0; block < 40; block++)
    {
        Array.Fill(input, 0.05f);
        stalledRing.Write(input);
        stalled.Read(output, 0, output.Length);

        for (var index = 0; index < output.Length; index++)
        {
            var sample = output[index];
            if (sample == 0f)
            {
                continue;
            }

            if (sample > 0.5f)
            {
                sawOldAudio = true;
            }
            else
            {
                sawNewAudio = true;
                if (float.IsNaN(firstResumedValue))
                {
                    firstResumedValue = sample;
                }
            }
        }
    }

    Assert(sawNewAudio, "audio must resume after a capture stall");
    Assert(!sawOldAudio, "resuming must not replay audio from before the stall");
    Assert(!float.IsNaN(firstResumedValue) && Math.Abs(firstResumedValue) < 0.02f,
        $"the first resumed sample must fade in, got {firstResumedValue:0.0000}");

    Console.WriteLine(
        $"      content pause: underruns={jitter.UnderrunCount}; " +
        $"capture stall: underruns={stalled.UnderrunCount}, first resumed={firstResumedValue:0.0000}");
}

static void TestJitterBaseLatencyRaise()
{
    const int rate = 48_000;
    const int blockFrames = 480;
    var ring = new AudioRingBuffer(rate, TimeSpan.FromSeconds(12));
    var jitter = new JitterBufferSampleProvider(ring, 60, 0, 30);
    jitter.StartAtFrame(0);
    var input = new float[blockFrames * AudioRingBuffer.Channels];
    var output = new float[blockFrames * AudioRingBuffer.Channels];
    long writeFrame = 0;

    void WriteRamp()
    {
        for (var frame = 0; frame < blockFrames; frame++)
        {
            var value = (float)(((writeFrame + frame) % 120_000) / 120_000.0);
            input[frame * 2] = value;
            input[frame * 2 + 1] = value;
        }

        ring.Write(input);
        writeFrame += blockFrames;
    }

    for (var block = 0; block < 100; block++)
    {
        WriteRamp();
        jitter.Read(output, 0, output.Length);
    }

    var lastBeforeRaise = output[^2];
    var underrunsBefore = jitter.UnderrunCount;

    // This is the engine's shared fallback operation: all destinations are raised to
    // the same base latency. It must pause long enough to build the extra history
    // without replaying audio or starving.
    jitter.SetBaseLatencyMilliseconds(150);

    var silenceBlocks = 0;
    var sawAudioAfterRaise = false;
    var firstAfterRaise = float.NaN;

    for (var block = 0; block < 40; block++)
    {
        WriteRamp();
        jitter.Read(output, 0, output.Length);

        var hasAudio = false;
        for (var index = 0; index < output.Length; index += 2)
        {
            if (output[index] != 0f)
            {
                hasAudio = true;
                if (float.IsNaN(firstAfterRaise))
                {
                    firstAfterRaise = output[index];
                }

                break;
            }
        }

        if (hasAudio)
        {
            sawAudioAfterRaise = true;
        }
        else if (!sawAudioAfterRaise)
        {
            silenceBlocks++;
        }
    }

    Assert(sawAudioAfterRaise, "audio must resume after the shared base latency rises");
    Assert(silenceBlocks is >= 6 and <= 13,
        $"raising 60 -> 150 ms must pause about 90 ms (9 blocks), got {silenceBlocks}");
    Assert(jitter.UnderrunCount == underrunsBefore,
        "raising the base latency must pause cleanly rather than starve");
    Assert(firstAfterRaise > lastBeforeRaise,
        "the raised-latency reader must resume at a newer frame, never rewind");

    Console.WriteLine(
        $"      base latency raise: paused {silenceBlocks} block(s), " +
        $"buffered={jitter.BufferedMilliseconds:0} ms, underruns={jitter.UnderrunCount}");
}

static void TestResampling()
{
    const int captureRate = 44100;
    const int outputRate = 48000;
    const int captureBlock = 441;
    const int outputBlock = 480;
    var ring = new AudioRingBuffer(captureRate, TimeSpan.FromSeconds(12));
    var jitter = new JitterBufferSampleProvider(ring, 150, 0);
    var resampler = new WdlResamplingSampleProvider(jitter, outputRate);
    var input = new float[captureBlock * 2];
    var output = new float[outputBlock * 2];
    var nonZero = 0L;

    for (var block = 0; block < 300; block++)
    {
        for (var frame = 0; frame < captureBlock; frame++)
        {
            var t = (block * captureBlock + frame) / (double)captureRate;
            var sample = (float)(Math.Sin(2 * Math.PI * 440 * t) * 0.5);
            input[frame * 2] = sample;
            input[frame * 2 + 1] = sample;
        }

        ring.Write(input);
        var read = resampler.Read(output, 0, output.Length);
        Assert(read == output.Length, "resampler returns full block");
        for (var index = 0; index < read; index++)
        {
            if (output[index] != 0f)
            {
                nonZero++;
            }
        }
    }

    Assert(nonZero > 0, "resampler produced audio");
    Console.WriteLine($"      resampled non-zero samples: {nonZero}");
}

static void TestCaptureFormats()
{
    TestPcm16CaptureConversion();
    TestPcm24CaptureConversion();
    TestPcm32CaptureConversion();
    TestPcm8CaptureConversion();
    TestFloatCaptureConversion();
}

static void TestPcm16CaptureConversion()
{
    var format = new WaveFormat(48000, 16, 2);
    var converter = new WaveFormatToFloatConverter(format);
    short[] pcm = { 0, 16384, -16384, 32767, -32768, 0 };
    var bytes = new byte[pcm.Length * 2];
    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
    var output = Array.Empty<float>();
    var frames = converter.Convert(bytes, bytes.Length, ref output);
    Assert(frames == 3, "pcm16 capture frames");
    Assert(Math.Abs(output[0]) < 0.0001, "pcm16 zero");
    Assert(Math.Abs(output[1] - 0.5f) < 0.001, "pcm16 half");
    Assert(Math.Abs(output[2] + 0.5f) < 0.001, "pcm16 negative half");
    Assert(Math.Abs(output[3] - 0.99997f) < 0.001, "pcm16 max");
    Assert(Math.Abs(output[4] + 1f) < 0.001, "pcm16 min");
    Assert(Math.Abs(output[5]) < 0.0001, "pcm16 zero right");
}

static void TestPcm24CaptureConversion()
{
    var format = new WaveFormat(48000, 24, 2);
    var converter = new WaveFormatToFloatConverter(format);
    var bytes = new byte[6];
    WritePcm24(bytes, 0, 4_194_304);   // 0.5
    WritePcm24(bytes, 3, -4_194_304);  // -0.5
    var output = Array.Empty<float>();
    var frames = converter.Convert(bytes, bytes.Length, ref output);
    Assert(frames == 1, "pcm24 capture frames");
    Assert(Math.Abs(output[0] - 0.5f) < 0.001, "pcm24 left");
    Assert(Math.Abs(output[1] + 0.5f) < 0.001, "pcm24 right");
}

static void TestPcm32CaptureConversion()
{
    var format = new WaveFormat(48000, 32, 2);
    var converter = new WaveFormatToFloatConverter(format);
    var input = new[] { 1_073_741_824, -1_073_741_824 };
    var bytes = new byte[input.Length * 4];
    MemoryMarshal.Cast<int, byte>(input.AsSpan()).CopyTo(bytes);
    var output = Array.Empty<float>();
    var frames = converter.Convert(bytes, bytes.Length, ref output);
    Assert(frames == 1, "pcm32 capture frames");
    Assert(Math.Abs(output[0] - 0.5f) < 0.001, "pcm32 left");
    Assert(Math.Abs(output[1] + 0.5f) < 0.001, "pcm32 right");
}

static void TestPcm8CaptureConversion()
{
    var format = new WaveFormat(48000, 8, 2);
    var converter = new WaveFormatToFloatConverter(format);
    var bytes = new byte[] { 128, 192, 64, 255, 0, 128 };
    var output = Array.Empty<float>();
    var frames = converter.Convert(bytes, bytes.Length, ref output);
    Assert(frames == 3, "pcm8 capture frames");
    Assert(Math.Abs(output[0]) < 0.001, "pcm8 zero");
    Assert(Math.Abs(output[1] - 0.5f) < 0.01, "pcm8 half");
    Assert(Math.Abs(output[2] + 0.5f) < 0.01, "pcm8 negative half");
    Assert(Math.Abs(output[3] - 1f) < 0.01, "pcm8 max");
    Assert(Math.Abs(output[4] + 1f) < 0.01, "pcm8 min");
    Assert(Math.Abs(output[5]) < 0.001, "pcm8 zero right");
}

static void TestFloatCaptureConversion()
{
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    var converter = new WaveFormatToFloatConverter(format);
    var input = new[] { 0.25f, -0.5f, 0.75f, -1f };
    var bytes = new byte[input.Length * 4];
    MemoryMarshal.Cast<float, byte>(input.AsSpan()).CopyTo(bytes);
    var output = Array.Empty<float>();
    var frames = converter.Convert(bytes, bytes.Length, ref output);
    Assert(frames == 2, "float capture frames");
    Assert(output[0] == 0.25f && output[1] == -0.5f && output[2] == 0.75f && output[3] == -1f, "float capture contents");
}

static void TestOutputFormats()
{
    TestPcm16OutputConversion();
    TestPcm24OutputConversion();
    TestPcm32OutputConversion();
    TestPcm8OutputConversion();
    TestFloatOutputConversion();
}

static void TestPcm16OutputConversion()
{
    var source = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 0.5f);
    var target = new WaveFormat(48000, 16, 2);
    var provider = new FloatToWaveProvider(source, target);
    var bytes = new byte[8];
    var read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 8, "pcm16 output bytes");
    var left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2));
    var right = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2, 2));
    Assert(Math.Abs(left - 16384) <= 1 && Math.Abs(right - 16384) <= 1, "pcm16 output sample");

    source.Value = 0f;
    read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 8, "pcm16 output zero fill bytes");
}

static void TestPcm24OutputConversion()
{
    var source = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), -0.5f);
    var provider = new FloatToWaveProvider(source, new WaveFormat(48000, 24, 2));
    var bytes = new byte[6];
    var read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 6, "pcm24 output bytes");
    Assert(ReadPcm24(bytes, 0) == -4_194_304, "pcm24 left sample");
    Assert(ReadPcm24(bytes, 3) == -4_194_304, "pcm24 right sample");
}

static void TestPcm32OutputConversion()
{
    var source = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 0.5f);
    var provider = new FloatToWaveProvider(source, new WaveFormat(48000, 32, 2));
    var bytes = new byte[8];
    var read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 8, "pcm32 output bytes");
    Assert(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)) == 1_073_741_824, "pcm32 left sample");
}

static void TestPcm8OutputConversion()
{
    var source = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 0.5f);
    var provider = new FloatToWaveProvider(source, new WaveFormat(48000, 8, 2));
    var bytes = new byte[2];
    var read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 2, "pcm8 output bytes");
    Assert(Math.Abs(bytes[0] - 192) <= 1 && Math.Abs(bytes[1] - 192) <= 1, "pcm8 output sample");
}

static void TestFloatOutputConversion()
{
    var source = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), -0.25f);
    var provider = new FloatToWaveProvider(source, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    var bytes = new byte[8];
    var read = provider.Read(bytes, 0, bytes.Length);
    Assert(read == 8, "float output bytes");
    var left = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4));
    var right = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4));
    Assert(left == -0.25f && right == -0.25f, "float output sample");
}

static void TestTargetFormats()
{
    var floatTarget = AudioFormatHelper.GetTargetOutputFormat(
        WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    Assert(floatTarget.Encoding == WaveFormatEncoding.IeeeFloat && floatTarget.BitsPerSample == 32, "float target");

    var pcm16Target = AudioFormatHelper.GetTargetOutputFormat(new WaveFormat(44100, 16, 2));
    Assert(pcm16Target.SampleRate == 44100 && pcm16Target.BitsPerSample == 16, "pcm16 target");

    var pcm8Target = AudioFormatHelper.GetTargetOutputFormat(new WaveFormat(48000, 8, 1));
    Assert(pcm8Target.Encoding == WaveFormatEncoding.Pcm && pcm8Target.BitsPerSample == 8, "pcm8 target");

    var fallback = AudioFormatHelper.GetFallbackOutputFormat(new WaveFormat(48000, 16, 2));
    Assert(fallback.Encoding == WaveFormatEncoding.IeeeFloat && fallback.SampleRate == 48000, "fallback target");
}

static void TestMutedHardware(IReadOnlyList<AudioDeviceInfo> devices)
{
    Assert(devices.Count >= 2, "need at least two active render endpoints");

    var source = devices.First(device => device.IsDefault);
    var targets = devices
        .Where(device => !string.Equals(device.Id, source.Id, StringComparison.OrdinalIgnoreCase))
        .Take(AudioRoutingEngine.MaxAdditionalOutputs)
        .Select(device => new AudioOutputTarget(device.Id, 0f, 0))
        .ToList();

    Assert(targets.Count > 0, "need at least one additional endpoint");

    using var watcher = new AudioDeviceWatcher();
    using var engine = new AudioRoutingEngine(new AudioDeviceEnumerator(), watcher);
    var result = engine.Start(new AudioRoutingRequest(source.Id, targets));
    Assert(result.Started, result.Message);

    Thread.Sleep(2000);
    var statuses = engine.GetChannelStatuses();
    Assert(statuses.Count == targets.Count, "all requested outputs must have a channel");
    Assert(statuses.All(status => status.State == AudioChannelState.Playing), "all outputs must be Playing");

    Console.WriteLine($"      source: {source.FriendlyName}");
    foreach (var status in statuses)
    {
        Console.WriteLine(
            $"      {status.DeviceName}: {status.State}, profile={status.ProfileName}, " +
            $"jitter={status.BufferedMilliseconds:0} ms, est.routing={status.EstimatedRoutingLatencyMilliseconds:0} ms " +
            $"(software), underruns={status.UnderrunCount}, drops={status.DroppedFrameCount}");
    }

    engine.Stop();
    Assert(engine.State == AudioRoutingState.Stopped, "engine must stop cleanly");
    Assert(engine.GetChannelStatuses().Count == 0, "all output channels must be released");
}

/// <summary>
/// Runs the same muted multi-output session once per buffer profile and reports the
/// measured software stability per profile. A profile passes when the session starts
/// and stops cleanly; the underrun/drop numbers are reported so the user can see
/// which profile their hardware sustains. This is a software-side measurement only:
/// it cannot prove audible quality, and Bluetooth codec latency is not exercised here
/// unless the user's machine has a Bluetooth endpoint connected.
/// </summary>
static void TestBufferProfileScan(IReadOnlyList<AudioDeviceInfo> devices)
{
    Assert(devices.Count >= 2, "need at least two active render endpoints");

    var source = devices.First(device => device.IsDefault);
    var targets = devices
        .Where(device => !string.Equals(device.Id, source.Id, StringComparison.OrdinalIgnoreCase))
        .Take(AudioRoutingEngine.MaxAdditionalOutputs)
        .Select(device => new AudioOutputTarget(device.Id, 0f, 0))
        .ToList();

    Assert(targets.Count > 0, "need at least one additional endpoint");

    foreach (var profile in BufferProfile.All)
    {
        using var watcher = new AudioDeviceWatcher();
        using var engine = new AudioRoutingEngine(new AudioDeviceEnumerator(), watcher);
        var result = engine.Start(new AudioRoutingRequest(source.Id, targets, profile));
        Assert(result.Started, $"{profile.DisplayName}: {result.Message}");

        Thread.Sleep(2_500);
        var statuses = engine.GetChannelStatuses();
        Assert(statuses.Count == targets.Count, $"{profile.DisplayName}: all outputs must have a channel");

        var totalUnderruns = statuses.Sum(status => status.UnderrunCount);
        var totalDrops = statuses.Sum(status => status.DroppedFrameCount);
        var maxDepth = statuses.Count == 0 ? 0 : statuses.Max(status => status.BufferedMilliseconds);
        var maxEstimate = statuses.Count == 0
            ? 0
            : statuses.Max(status => status.EstimatedRoutingLatencyMilliseconds);

        Console.WriteLine(
            $"      {profile.DisplayName} (base {profile.BaseLatencyMs} ms, WASAPI {profile.WasapiLatencyMs} ms): " +
            $"jitter depth {maxDepth:0} ms, est. routing latency {maxEstimate:0} ms (software), " +
            $"underruns {totalUnderruns}, dropped frames {totalDrops}");
        foreach (var status in statuses)
        {
            Assert(status.State == AudioChannelState.Playing,
                $"{profile.DisplayName}: {status.DeviceName} must be Playing, got {status.State}");
        }

        engine.Stop();
        Assert(engine.State == AudioRoutingState.Stopped, $"{profile.DisplayName}: engine must stop cleanly");
    }

    Console.WriteLine(
        "      Note: these numbers are software-side. Acoustic latency and Bluetooth " +
        "codec delay are not measured by this scan.");
}

static void TestTwoTargetSignal(IReadOnlyList<AudioDeviceInfo> devices)
{
    Assert(devices.Count >= 3, "need a source and at least two target endpoints");

    var source = devices.FirstOrDefault(device =>
                     device.FriendlyName.Contains("Oculus", StringComparison.OrdinalIgnoreCase))
                 ?? devices.First(device => device.IsDefault);

    var targets = devices
        .Where(device => !string.Equals(device.Id, source.Id, StringComparison.OrdinalIgnoreCase))
        .Take(2)
        .ToList();

    Assert(targets.Count >= 2, "need two non-source target endpoints");
    Console.WriteLine($"      source: {source.FriendlyName}");
    foreach (var target in targets)
    {
        Console.WriteLine($"      target: {target.FriendlyName}");
    }

    using var enumerator = new MMDeviceEnumerator();
    using var sourceDevice = enumerator.GetDevice(source.Id);
    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var defaultEndpoint = defaultDevice.AudioEndpointVolume;
    using var sourceEndpoint = sourceDevice.AudioEndpointVolume;
    var originalVolume = defaultEndpoint.MasterVolumeLevelScalar;
    var originalMute = defaultEndpoint.Mute;
    var originalSourceVolume = sourceEndpoint.MasterVolumeLevelScalar;
    var originalSourceMute = sourceEndpoint.Mute;

    var captures = targets
        .Select(target => new TargetLoopbackCapture(enumerator.GetDevice(target.Id), target.FriendlyName))
        .ToList();

    AudioRoutingEngine? engine = null;

    try
    {
        using var watcher = new AudioDeviceWatcher();
        engine = new AudioRoutingEngine(new AudioDeviceEnumerator(), watcher);
        var result = engine.Start(new AudioRoutingRequest(
            source.Id,
            targets.Select(target => new AudioOutputTarget(target.Id, 0.35f, 0)).ToList()));
        Assert(result.Started, result.Message);

        // Give every destination, especially Bluetooth ones, time to enter its
        // render loop before measuring. The routing engine starts channels
        // asynchronously; a slow codec can otherwise still be priming when the
        // first tone starts, making the first phase artificially quiet.
        Thread.Sleep(2_000);
        Assert(
            engine.GetChannelStatuses().All(status => status.State == AudioChannelState.Playing),
            "all signal-test destinations must enter Playing before measurement");

        // The manager adopts the Windows default endpoint's mute state. This
        // machine's endpoints are muted, which would silence every destination
        // while the pre-volume loopback still hears the source tone. Clear the
        // logical mute explicitly; the finally block restores the endpoint mute.
        engine.MasterMute = false;

        // Phase A: master at unity. The Windows endpoint volume is temporarily
        // raised to 1.0 so the group gain is the only variable between phases.
        engine.MasterVolume = 1f;
        Thread.Sleep(350);
        var loud = RunTonePhase(captures, sourceDevice, 2_500);

        // Phase B: master at 50%. The routed copies must be 6 dB quieter.
        foreach (var capture in captures)
        {
            capture.Reset();
        }

        engine.MasterVolume = 0.5f;
        Thread.Sleep(350);
        var quiet = RunTonePhase(captures, sourceDevice, 2_500);

        foreach (var status in engine.GetChannelStatuses())
        {
            Console.WriteLine(
                $"      routed {status.DeviceName}: {status.State}, buffer={status.BufferedMilliseconds:0} ms, " +
                $"underruns={status.UnderrunCount}, drops={status.DroppedFrameCount}");
        }

        for (var index = 0; index < captures.Count; index++)
        {
            var loudRms = loud[index].ToneRms;
            var quietRms = quiet[index].ToneRms;
            var ratio = loudRms <= 1e-9 ? 0 : quietRms / loudRms;
            Console.WriteLine(
                $"      {captures[index].Name}: tone RMS at master 1.0 = {loudRms:0.000000} " +
                $"(mix RMS {loud[index].TrimmedRms:0.000000}, n={loud[index].Count}), " +
                $"at master 0.5 = {quietRms:0.000000} " +
                $"(mix RMS {quiet[index].TrimmedRms:0.000000}, n={quiet[index].Count}), ratio {ratio:0.000}");
            Assert(loudRms > 0.0001, $"{captures[index].Name} did not receive the tone");
            Assert(ratio > 0.40f && ratio < 0.60f,
                $"{captures[index].Name}: master volume did not scale the routed samples " +
                $"(expected ratio ~0.5, got {ratio:0.000})");
        }

        // Phase C baseline: measure unity again immediately before enabling balance,
        // so slow endpoint level drift between phases cannot masquerade as a balance
        // gain change.
        foreach (var capture in captures)
        {
            capture.Reset();
        }

        engine.BalanceEnabled = false;
        engine.MasterVolume = 1f;
        Thread.Sleep(350);
        var unity = RunTonePhase(captures, sourceDevice, 2_500);

        // Phase C: with master at unity, an independent balance trim must
        // attenuate only its own destination and leave the other unchanged. The
        // narrowband tone RMS is used so unrelated system audio does not dominate.
        foreach (var capture in captures)
        {
            capture.Reset();
        }

        engine.SetBalanceGain(targets[0].Id, 0.25f);
        engine.SetBalanceGain(targets[1].Id, 1f);
        engine.BalanceEnabled = true;
        engine.MasterVolume = 1f;
        Thread.Sleep(350);
        var balanced = RunTonePhase(captures, sourceDevice, 2_500);

        for (var index = 0; index < captures.Count; index++)
        {
            var baseline = unity[index].ToneRms;
            var balancedRms = balanced[index].ToneRms;
            var ratio = baseline <= 1e-9 ? 0 : balancedRms / baseline;
            var expected = index == 0 ? 0.25 : 1.0;
            Console.WriteLine(
                $"      {captures[index].Name}: balance gain {expected:0.00} -> " +
                $"tone rms {balancedRms:0.000000} vs {baseline:0.000000}, ratio {ratio:0.000}");

            // Some endpoints (digital outputs, unattended HDMI/USB devices) return
            // a loopback signal far below the noise floor of this measurement. A
            // ratio computed on that floor cannot resolve a 25% gain change, so the
            // endpoint is reported as inconclusive instead of failing the run.
            if (baseline < 0.0005)
            {
                Console.WriteLine(
                    $"      {captures[index].Name}: balance check inconclusive " +
                    "(baseline tone below the measurement floor)");
                continue;
            }

            Assert(ratio > expected - 0.15 && ratio < expected + 0.15,
                $"{captures[index].Name}: routed balance gain was not applied independently " +
                $"(expected ~{expected:0.00}, got {ratio:0.000})");
        }

        engine.Stop();
    }
    finally
    {
        if (engine is not null)
        {
            try
            {
                engine.Dispose();
            }
            catch (Exception)
            {
                // The test outcome is already determined.
            }
        }

        // Restore the user's system volume after the engine is disposed so the
        // master-volume manager no longer re-applies its logical state. The
        // manager also mirrors the group level onto the source endpoint when it
        // differs from the Windows default, so restore that endpoint as well.
        try
        {
            defaultEndpoint.MasterVolumeLevelScalar = originalVolume;
            defaultEndpoint.Mute = originalMute;
        }
        catch (Exception)
        {
            // Best-effort restore.
        }

        try
        {
            sourceEndpoint.MasterVolumeLevelScalar = originalSourceVolume;
            sourceEndpoint.Mute = originalSourceMute;
        }
        catch (Exception)
        {
            // Best-effort restore.
        }

        foreach (var capture in captures)
        {
            capture.Dispose();
        }
    }
}

static List<(int Count, double Rms, float Max, double TrimmedRms, double ToneRms)> RunTonePhase(
    IReadOnlyList<TargetLoopbackCapture> captures,
    MMDevice sourceDevice,
    int toneMilliseconds)
{
    foreach (var capture in captures)
    {
        capture.Start();
    }

    var generator = new SignalGenerator(48000, 2)
    {
        Type = SignalGeneratorType.Sin,
        Frequency = DiagnosticToneFrequencyHz,
        Gain = 0.08
    };

    using (var toneOutput = new WasapiOut(sourceDevice, AudioClientShareMode.Shared, true, 100))
    {
        toneOutput.Init(generator.ToWaveProvider());
        toneOutput.Play();
        Thread.Sleep(toneMilliseconds);
        toneOutput.Stop();
    }

    // High-latency endpoints (Bluetooth, some HDMI/USB devices) can delay the
    // routed tone by several hundred milliseconds. A longer tail keeps the tone
    // inside the capture window so the narrowband analysis sees it.
    Thread.Sleep(500);
    var results = new List<(int Count, double Rms, float Max, double TrimmedRms, double ToneRms)>();
    foreach (var capture in captures)
    {
        capture.Stop();
        var result = capture.GetResult();
        results.Add((
            result.Count,
            result.Rms,
            result.Max,
            result.TrimmedRms,
            capture.GetToneRms(DiagnosticToneFrequencyHz)));
    }

    return results;
}

static void TestMasterVolumeSampleGain()
{
    using var manager = new MasterVolumeManager(manageSystemVolume: false, rampMilliseconds: 200);
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    var source = new ConstantSampleProvider(format, 0.5f);
    var gain = new GainSampleProvider(source, () => manager.GetRampedVolume("speaker-a"));

    manager.SetDeviceVolume("speaker-a", 1f);
    manager.MasterVolume = 1f;
    AssertSampleGain(gain, 0.5f, 0.001f, "unity gain");

    manager.MasterVolume = 0.5f;
    Thread.Sleep(300);
    AssertSampleGain(gain, 0.25f, 0.002f, "50% master");

    manager.SetDeviceVolume("speaker-a", 0.5f);
    AssertSampleGain(gain, 0.125f, 0.002f, "50% master * 50% trim");

    manager.MasterMute = true;
    Thread.Sleep(300);
    AssertSampleGain(gain, 0f, 0.0001f, "mute");

    manager.MasterMute = false;
    manager.MasterVolume = 1f;
    manager.SetDeviceVolume("speaker-a", 1f);
    Thread.Sleep(300);
    AssertSampleGain(gain, 0.5f, 0.002f, "restored after unmute");

    // Two devices keep independent trims under the same master.
    manager.SetDeviceVolume("speaker-a", 1f);
    manager.SetDeviceVolume("speaker-b", 0.4f);
    manager.MasterVolume = 0.5f;
    Thread.Sleep(300);
    Assert(Math.Abs(manager.GetEffectiveVolume("speaker-a") - 0.5f) < 0.002f, "device A trim preserved");
    Assert(Math.Abs(manager.GetEffectiveVolume("speaker-b") - 0.2f) < 0.002f, "device B trim preserved");
}

static void TestMasterVolumeRampSmoothness()
{
    using var manager = new MasterVolumeManager(manageSystemVolume: false, rampMilliseconds: 200);
    manager.SetDeviceVolume("speaker-a", 1f);
    manager.MasterVolume = 1f;

    // Start a downward ramp: the first read must still be near the old value,
    // an instant jump to 0 would be an audible click.
    manager.MasterVolume = 0f;
    var immediate = manager.GetRampedVolume("speaker-a");
    Assert(immediate > 0.8f, $"ramp must start from the previous gain, got {immediate:0.000}");

    Thread.Sleep(100);
    var middle = manager.GetRampedVolume("speaker-a");
    Assert(middle > 0.15f && middle < 0.85f, $"ramp must pass through intermediate gain, got {middle:0.000}");

    // Re-target in the middle of a ramp: the new ramp must start from the current
    // interpolated gain, not from the old target, otherwise the gain jumps.
    manager.MasterVolume = 1f;
    var afterRetarget = manager.GetRampedVolume("speaker-a");
    Assert(Math.Abs(afterRetarget - middle) < 0.15f,
        $"retarget must not jump (before {middle:0.000}, after {afterRetarget:0.000})");

    Thread.Sleep(300);
    Assert(manager.GetRampedVolume("speaker-a") > 0.99f, "ramp must reach the new target");

    // Mute ramps too instead of hard-cutting.
    manager.MasterMute = true;
    var muteImmediate = manager.GetRampedVolume("speaker-a");
    Assert(muteImmediate > 0.8f, $"mute must ramp down, got {muteImmediate:0.000}");
    Thread.Sleep(300);
    Assert(manager.GetRampedVolume("speaker-a") < 0.001f, "mute must reach zero");
}

static void TestVolumeBalanceMath()
{
    var gains = VolumeBalanceMath.ComputeGains(new[]
    {
        new BalanceLevelInput("quiet-output", 0.010, 1.0, IsSource: false),
        new BalanceLevelInput("loud-output", 0.040, 1.0, IsSource: false),
        new BalanceLevelInput("source", 0.020, 0.5, IsSource: true)
    });

    Assert(Math.Abs(gains["quiet-output"] - 1.0f) < 0.001f,
        "the quietest measured speaker must not be attenuated");
    Assert(Math.Abs(gains["loud-output"] - 0.25f) < 0.001f,
        $"a speaker 4x louder must receive 25% gain, got {gains["loud-output"]:0.000}");
    Assert(Math.Abs(gains["source"] - 0.25f) < 0.001f,
        $"the source must use endpoint-relative math, got {gains["source"]:0.000}");
    Assert(gains.Values.All(gain => gain <= 1f), "balance must never boost above unity");

    // A quiet source measured at a 50% endpoint volume needs a 50% physical
    // endpoint gain to reproduce that same level at logical master 100%.
    var quietSource = VolumeBalanceMath.ComputeGains(new[]
    {
        new BalanceLevelInput("source", 0.010, 0.5, IsSource: true),
        new BalanceLevelInput("loud-output", 0.040, 1.0, IsSource: false)
    });
    Assert(Math.Abs(quietSource["source"] - 0.5f) < 0.001f,
        $"quiet source endpoint gain should be 0.5, got {quietSource["source"]:0.000}");
    Assert(Math.Abs(quietSource["loud-output"] - 0.25f) < 0.001f,
        "destination gain must remain the measured RMS ratio");

    Assert(Math.Abs(VolumeBalanceMath.GainToDecibels(0.5f) + 6.0206) < 0.01,
        "50% gain must be about -6 dB");
    Assert(VolumeBalanceMath.IsReliable(0.010, 0.001),
        "20 dB SNR must be accepted");
    Assert(!VolumeBalanceMath.IsReliable(0.010, 0.010),
        "0 dB SNR must be rejected as unreliable");
    Assert(!VolumeBalanceMath.IsReliable(VolumeBalanceMath.MinimumToneRms / 2, 0.000001),
        "a too-quiet measurement must be rejected");
}

static void TestVolumeBalanceApplication()
{
    using var manager = new MasterVolumeManager(manageSystemVolume: false, rampMilliseconds: 150);
    manager.SetDeviceVolume("speaker-a", 1f);
    manager.SetDeviceVolume("speaker-b", 0.8f);
    manager.SetDeviceBalanceGain("speaker-a", 0.5f);
    manager.SetDeviceBalanceGain("speaker-b", 1f);
    manager.BalanceEnabled = true;
    manager.MasterVolume = 1f;
    Thread.Sleep(250);

    AssertNear(manager.GetRampedVolume("speaker-a"), 0.5f, 0.01f, "balance attenuation A");
    AssertNear(manager.GetRampedVolume("speaker-b"), 0.8f, 0.01f, "independent trim B");

    manager.MasterVolume = 0.6f;
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.30f, 0.01f,
        "master change must preserve A's balance and trim");
    AssertNear(manager.GetRampedVolume("speaker-b"), 0.48f, 0.01f,
        "master change must preserve B's independent trim");

    // A balance change is ramped from the current value rather than jumping.
    manager.SetDeviceBalanceGain("speaker-a", 0.2f);
    var immediate = manager.GetRampedVolume("speaker-a");
    Assert(immediate > 0.20f,
        $"balance adjustment must ramp instead of jumping, immediate={immediate:0.000}");
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.12f, 0.01f,
        "balance target after ramp");

    // Mute and unmute remain reliable with balance active.
    manager.MasterMute = true;
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0f, 0.0001f, "mute silences balance gain");
    manager.MasterMute = false;
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.12f, 0.01f, "unmute restores gain");

    // Clipping guard: a near-full-scale input multiplied by the final dynamic
    // gain must never exceed the input itself.
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    var source = new ConstantSampleProvider(format, 0.9f);
    var gain = new GainSampleProvider(source, () => manager.GetRampedVolume("speaker-a"));
    var buffer = new float[512];
    gain.Read(buffer, 0, buffer.Length);
    Assert(buffer.All(sample => Math.Abs(sample) <= 0.9f + 0.0001f),
        "balance gain must not clip or add gain");

    // Disabling keeps the saved calibration but ramps the audible gain to unity.
    manager.BalanceEnabled = false;
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.6f, 0.01f,
        "disabled balance must return to master * trim");
    AssertNear(manager.GetDeviceBalanceGain("speaker-a"), 0.2f, 0.001f,
        "disabling must keep the saved calibration");

    manager.BalanceEnabled = true;
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.12f, 0.01f,
        "re-enabling must restore the saved balance");

    manager.ClearBalanceGains();
    Thread.Sleep(250);
    AssertNear(manager.GetRampedVolume("speaker-a"), 0.6f, 0.01f,
        "clearing balance must ramp back to unity");
    AssertNear(manager.GetDeviceBalanceGain("speaker-a"), 1f, 0.001f,
        "clearing removes the saved gain");

    Console.WriteLine(
        $"      final gains: A={manager.GetRampedVolume("speaker-a"):0.000}, " +
        $"B={manager.GetRampedVolume("speaker-b"):0.000}, " +
        $"mute={manager.MasterMute}");
}

static void TestVolumeBalanceStore()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "SpeakerConnect.Diagnostics",
        Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "volume-balance.json");

    try
    {
        var store = new VolumeBalanceStore(path);
        store.SetEnabled(true);
        store.SaveGain("device-a", 0.4f);
        store.SaveGain("device-b", 0.75f);
        store.SaveGain("clamped-low", 0.00001f);
        store.SaveGain("clamped-high", 5f);

        // A fresh instance simulates an application restart.
        var reopened = new VolumeBalanceStore(path);
        Assert(reopened.Enabled, "enabled flag must survive a restart");
        Assert(reopened.TryGetGain("device-a", out var gainA) &&
               Math.Abs(gainA - 0.4f) < 0.001f,
            "device-a gain must survive by persistent device id");
        Assert(reopened.TryGetGain("device-b", out var gainB) &&
               Math.Abs(gainB - 0.75f) < 0.001f,
            "device-b gain must survive by persistent device id");
        Assert(reopened.TryGetGain("clamped-low", out var low) && low >= 0.009f,
            "saved gains must clamp the low end");
        Assert(reopened.TryGetGain("clamped-high", out var high) && high <= 1.0f,
            "saved gains must never exceed unity");

        // A reconnected endpoint is just a fresh lookup of the same id.
        Assert(reopened.GetSnapshot().Count == 4, "snapshot must contain all saved devices");

        var merged = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["device-a"] = 0.55f,
            ["device-c"] = 0.9f
        };
        store.SaveGains(merged);
        var afterMerge = new VolumeBalanceStore(path);
        Assert(afterMerge.GetSnapshot().Count == 2, "bulk save must replace the active set");
        Assert(afterMerge.TryGetGain("device-c", out var gainC) &&
               Math.Abs(gainC - 0.9f) < 0.001f,
            "bulk save must write new devices");

        store.ResetGains();
        var afterReset = new VolumeBalanceStore(path);
        Assert(afterReset.Enabled, "Reset Calibration must keep the enabled preference");
        Assert(afterReset.GetSnapshot().Count == 0, "Reset Calibration must clear all gains");

        store.SetEnabled(false);
        var disabled = new VolumeBalanceStore(path);
        Assert(!disabled.Enabled, "disabled preference must survive a restart");

        File.WriteAllText(path, "{ this is not valid json");
        var corrupt = new VolumeBalanceStore(path);
        Assert(!corrupt.Enabled && corrupt.GetSnapshot().Count == 0,
            "a corrupt settings file must fall back to safe defaults");

        Console.WriteLine($"      persistence verified at {path}");
    }
    finally
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Test cleanup is best-effort.
        }
    }
}

static void TestDeviceSettingsStore()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "SpeakerConnect.Diagnostics",
        Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, DeviceSettingsStore.FileName);

    try
    {
        var measuredAt = new DateTimeOffset(2026, 11, 9, 10, 30, 0, TimeSpan.Zero);
        const string syncMethod = "microphone chirp cross-correlation (routed path, relative to anchor)";

        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(!store.TryGet("device-a", out _), "a new settings store must be empty");
            Assert(store.ReferenceDeviceId is null && store.SourceDeviceId is null,
                "a new settings store must not invent a reference or source");

            store.SaveVolume("device-a", 42.5);
            store.SaveDelay("device-a", 120);
            store.SaveSelection("device-a", true);
            store.SaveSyncMeasurement("device-a", 87.5, 92, syncMethod, measuredAt);
            store.SaveVolume("device-b", 33);
            store.SaveReferenceDevice("device-a");
            store.SaveSourceDevice("device-b");
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("device-a", out var deviceA),
                "saved device-a must survive a restart");
            Assert(deviceA.VolumePercent is 42.5 &&
                   deviceA.DelayMilliseconds is 120 &&
                   deviceA.IsSelected is true,
                "volume, delay, and selection must survive a restart");
            Assert(deviceA.MeasuredOffsetMilliseconds is 87.5 &&
                   deviceA.MeasurementConfidencePercent is 92 &&
                   deviceA.SyncMeasurementMethod == syncMethod &&
                   deviceA.SyncMeasuredAtUtc == measuredAt,
                "acoustic timing metadata must survive a restart separately from the manual delay");
            Assert(reopened.TryGet("device-b", out var deviceB) &&
                   deviceB.VolumePercent is 33,
                "a second device must survive a restart");
            Assert(reopened.ReferenceDeviceId == "device-a" &&
                   reopened.SourceDeviceId == "device-b",
                "reference and source ids must survive a restart");
        }

        // Per-device updates must never rewrite other endpoints. device-b is
        // deliberately not "connected" in this scope.
        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveVolume("device-a", 10);
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("device-a", out var deviceA) &&
                   deviceA.VolumePercent is 10,
                "updating one device must persist its new volume");
            Assert(deviceA.MeasuredOffsetMilliseconds is 87.5 &&
                   deviceA.MeasurementConfidencePercent is 92,
                "a volume-only update must preserve the acoustic timing metadata");
            Assert(reopened.TryGet("device-b", out var deviceB) &&
                   deviceB.VolumePercent is 33,
                "updating one device must preserve a disconnected device's volume");
        }

        // Removing the manual delay must not erase the acoustic measurement: they
        // are deliberately separate facts about the same endpoint.
        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.RemoveDelay("device-a");
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("device-a", out var deviceA) &&
                   deviceA.DelayMilliseconds is null &&
                   deviceA.VolumePercent is 10 &&
                   deviceA.IsSelected is true,
                "removing a delay must keep the volume and selection for that device");
            Assert(deviceA.MeasuredOffsetMilliseconds is 87.5 &&
                   deviceA.SyncMeasurementMethod == syncMethod,
                "removing a manual delay must not clear the acoustic measurement");
        }

        // ClearSyncMeasurement removes only the acoustic metadata and keeps the
        // manual delay, volume, and selection.
        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveDelay("device-a", 55);
            store.ClearSyncMeasurement("device-a");
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("device-a", out var deviceA) &&
                   deviceA.DelayMilliseconds is 55 &&
                   deviceA.VolumePercent is 10 &&
                   deviceA.IsSelected is true,
                "clearing acoustic metadata must preserve the manual delay and other settings");
            Assert(deviceA.MeasuredOffsetMilliseconds is null &&
                   deviceA.MeasurementConfidencePercent is null &&
                   deviceA.SyncMeasurementMethod is null &&
                   deviceA.SyncMeasuredAtUtc is null,
                "clearing acoustic metadata must remove every measurement field");
        }

        // Reset Sync uses RemoveDelayAndSync: both timing fields go, while volume
        // and group selection survive.
        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveSyncMeasurement("device-b", 140, 88, syncMethod, measuredAt);
            store.RemoveDelayAndSync("device-b");
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("device-b", out var deviceB) &&
                   deviceB.VolumePercent is 33 &&
                   deviceB.DelayMilliseconds is null &&
                   deviceB.MeasuredOffsetMilliseconds is null &&
                   deviceB.SyncMeasurementMethod is null,
                "Reset Sync must clear delay and acoustic timing but keep unrelated settings");
        }

        // Invalid or out-of-range acoustic values must never produce a bogus
        // measurement. Finite values are clamped to the documented ranges.
        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveSyncMeasurement("sync-clamped", 50_000, 150, syncMethod, measuredAt);
            store.SaveSyncMeasurement("sync-invalid", double.NaN, double.NaN, syncMethod, measuredAt);
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("sync-clamped", out var clamped) &&
                   clamped.MeasuredOffsetMilliseconds is 10_000 &&
                   clamped.MeasurementConfidencePercent is 100,
                "acoustic offsets and confidence must clamp to their documented ranges");
            Assert(!reopened.TryGet("sync-invalid", out _),
                "non-finite acoustic values must be ignored rather than saved as a measurement");
        }

        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveVolume("clamped-low", -5);
            store.SaveVolume("clamped-high", 150);
            store.SaveDelay("delay-low", -10);
            store.SaveDelay("delay-high", 9_000);
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.TryGet("clamped-low", out var low) && low.VolumePercent is 0,
                "volume below zero must clamp to 0%");
            Assert(reopened.TryGet("clamped-high", out var high) && high.VolumePercent is 100,
                "volume above 100 must clamp to 100%");
            Assert(reopened.TryGet("delay-low", out var delayLow) &&
                   delayLow.DelayMilliseconds is 0,
                "negative delay must clamp to 0 ms");
            Assert(reopened.TryGet("delay-high", out var delayHigh) &&
                   delayHigh.DelayMilliseconds is 5_000,
                "delay above the engine maximum must clamp to 5000 ms");
        }

        using (var store = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            store.SaveReferenceDevice(null);
            store.SaveSourceDevice(null);
        }

        using (var reopened = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(reopened.ReferenceDeviceId is null && reopened.SourceDeviceId is null,
                "clearing the reference/source must survive a restart");
        }

        Assert(!File.Exists(path + ".tmp"), "an atomic write must not leave a temp file behind");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert(document.RootElement.TryGetProperty("Version", out _),
                "the settings file must be valid, versioned JSON");
        }

        File.WriteAllText(path, "{ this is not valid json");
        using (var corrupt = new DeviceSettingsStore(path, TimeSpan.Zero))
        {
            Assert(corrupt.GetSnapshot().Count == 0 &&
                   corrupt.ReferenceDeviceId is null &&
                   corrupt.SourceDeviceId is null,
                "a corrupt settings file must fall back to safe defaults");
        }

        Assert(Directory.GetFiles(directory, DeviceSettingsStore.FileName + ".corrupt-*").Length == 1,
            "a corrupt settings file must be backed up for inspection");
        Assert(!File.Exists(path),
            "the corrupt settings file must be moved aside before defaults are used");

        // Legacy delay-only file migration.
        var legacyDirectory = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        var legacyPath = Path.Combine(legacyDirectory, DeviceSettingsStore.FileName);
        File.WriteAllText(
            Path.Combine(legacyDirectory, DeviceSettingsStore.LegacyFileName),
            "{\"old-speaker\":85,\"too-large\":9000}");

        using (var migrated = new DeviceSettingsStore(legacyPath, TimeSpan.Zero))
        {
            Assert(migrated.TryGet("old-speaker", out var old) &&
                   old.DelayMilliseconds is 85,
                "legacy delay offsets must be imported once");
            Assert(migrated.TryGet("too-large", out var high) &&
                   high.DelayMilliseconds is 5_000,
                "legacy delays must be clamped during migration");
        }

        Assert(File.Exists(legacyPath),
            "legacy migration must write the new settings file");

        using (var migrated = new DeviceSettingsStore(legacyPath, TimeSpan.Zero))
        {
            migrated.RemoveDelay("old-speaker");
        }

        using (var reopened = new DeviceSettingsStore(legacyPath, TimeSpan.Zero))
        {
            Assert(!reopened.TryGet("old-speaker", out _),
                "a removed legacy offset must not be re-imported from the stale legacy file");
        }

        Console.WriteLine($"      persistence, atomic writes, and migration verified at {directory}");
    }
    finally
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Test cleanup is best-effort.
        }
    }
}

static void TestViewModelDeviceSettingsPersistence()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            RunViewModelDeviceSettingsScenario();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    })
    {
        IsBackground = true,
        Name = "SpeakerConnect diagnostics UI persistence"
    };

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        throw new InvalidOperationException(failure.Message, failure);
    }
}

static void RunViewModelDeviceSettingsScenario()
{
    const string sourceId = "diag-source";
    const string leftId = "diag-speaker-left";
    const string rightId = "diag-speaker-right";

    var source = new AudioDeviceInfo(sourceId, "Diagnostic Source", true, 48_000, 2, 16, "16-bit 48 kHz");
    var left = new AudioDeviceInfo(leftId, "Diagnostic Left", false, 48_000, 2, 16, "16-bit 48 kHz");
    var right = new AudioDeviceInfo(rightId, "Diagnostic Right", false, 48_000, 2, 16, "16-bit 48 kHz");

    var directory = Path.Combine(
        Path.GetTempPath(),
        "SpeakerConnect.Diagnostics",
        Guid.NewGuid().ToString("N"));
    var settingsPath = Path.Combine(directory, DeviceSettingsStore.FileName);
    var balancePath = Path.Combine(directory, "volume-balance.json");
    var fake = new FakeAudioDeviceEnumerator
    {
        Devices = new[] { source, left, right }
    };

    MainViewModel? viewModel = null;

    try
    {
        viewModel = new MainViewModel(
            fake,
            new DeviceSettingsStore(settingsPath, TimeSpan.Zero),
            new VolumeBalanceStore(balancePath));

        Assert(viewModel.Devices.Count == 3, "all fake endpoints must be listed");
        Assert(viewModel.SourceDevice?.Id == sourceId,
            "the Windows default endpoint must be the initial source");

        var leftRow = viewModel.Devices.Single(device => device.Id == leftId);
        var rightRow = viewModel.Devices.Single(device => device.Id == rightId);

        leftRow.IsSelected = true;
        leftRow.Volume = 42;
        leftRow.DelayMilliseconds = 120;
        leftRow.BalanceGainPercent = 80;
        viewModel.SaveBalanceCommand.Execute(null);

        var measuredAt = new DateTimeOffset(2026, 11, 9, 11, 15, 0, TimeSpan.Zero);
        const string syncMethod = "microphone chirp cross-correlation (routed path, relative to anchor)";
        leftRow.ApplySyncMeasurement(-87.5, 92, syncMethod, measuredAt);
        viewModel.SaveSyncCommand.Execute(null);

        // Change the source explicitly to prove the selected source is saved
        // rather than only the Windows default being re-derived at startup.
        viewModel.SourceDevice = rightRow;
        viewModel.ReferenceDevice = leftRow;

        Assert(File.Exists(settingsPath),
            "changing a saved value must write the settings file automatically");
        Assert(File.Exists(balancePath),
            "Save Calibration must write the balance file");

        using (var persisted = new DeviceSettingsStore(settingsPath, TimeSpan.Zero))
        {
            Assert(persisted.SourceDeviceId == rightId &&
                   persisted.ReferenceDeviceId == leftId,
                "source and sync reference must be saved immediately");
            Assert(persisted.TryGet(leftId, out var saved) &&
                   saved.VolumePercent is 42 &&
                   saved.DelayMilliseconds is 120 &&
                   saved.IsSelected is true,
                "volume, delay, and selection must be saved immediately");
            Assert(saved.MeasuredOffsetMilliseconds is -87.5 &&
                   saved.MeasurementConfidencePercent is 92 &&
                   saved.SyncMeasurementMethod == syncMethod &&
                   saved.SyncMeasuredAtUtc == measuredAt,
                "Save Calibration must persist acoustic timing metadata with the delay");
        }

        var persistedBalance = new VolumeBalanceStore(balancePath);
        Assert(persistedBalance.TryGetGain(leftId, out var gain) &&
               Math.Abs(gain - 0.8f) < 0.001f,
            "the Smart Volume Balance calibration must be saved by endpoint id");

        viewModel.Dispose();
        viewModel = null;

        // Restart: a fresh store and VM must restore every value.
        viewModel = new MainViewModel(
            fake,
            new DeviceSettingsStore(settingsPath, TimeSpan.Zero),
            new VolumeBalanceStore(balancePath));

        leftRow = viewModel.Devices.Single(device => device.Id == leftId);
        Assert(viewModel.SourceDevice?.Id == rightId, "the saved source must survive a restart");
        Assert(viewModel.ReferenceDevice?.Id == leftId, "the saved reference must survive a restart");
        Assert(leftRow.IsSelected, "the selected output group must survive a restart");
        Assert(leftRow.Volume == 42, "the saved volume must survive a restart");
        Assert(leftRow.DelayMilliseconds == 120, "the saved delay must survive a restart");
        Assert(Math.Abs(leftRow.BalanceGainPercent - 80) < 0.01,
            "the saved balance calibration must survive a restart");
        Assert(leftRow.MeasuredOffsetMilliseconds is -87.5 &&
               leftRow.MeasurementConfidencePercent is 92 &&
               leftRow.SyncMeasurementMethod == syncMethod &&
               leftRow.SyncMeasuredAtUtc == measuredAt,
            "the saved acoustic timing metadata must survive a restart");
        Assert(leftRow.SyncMeasurementDisplay.Contains("ms earlier than the anchor", StringComparison.Ordinal) &&
               leftRow.SyncMeasurementDisplay.Contains("92% confidence", StringComparison.Ordinal),
            $"the restored measurement must stay visibly relative and measured: {leftRow.SyncMeasurementDisplay}");

        viewModel.Dispose();
        viewModel = null;

        // Disconnect: a missing endpoint must not be rewritten with defaults.
        fake.Devices = new[] { source, right };
        var disconnectedStore = new DeviceSettingsStore(settingsPath, TimeSpan.Zero);
        viewModel = new MainViewModel(
            fake,
            disconnectedStore,
            new VolumeBalanceStore(balancePath));

        Assert(viewModel.Devices.All(device => device.Id != leftId),
            "the disconnected endpoint must not appear in the list");
        Assert(disconnectedStore.TryGet(leftId, out var kept) &&
               kept.VolumePercent is 42 &&
               kept.DelayMilliseconds is 120 &&
               kept.IsSelected is true,
            "a disconnected device's saved volume, delay, and selection must be preserved");
        Assert(kept.MeasuredOffsetMilliseconds is -87.5 &&
               kept.MeasurementConfidencePercent is 92 &&
               kept.SyncMeasurementMethod == syncMethod,
            "a disconnected device's acoustic timing metadata must be preserved");
        Assert(disconnectedStore.ReferenceDeviceId == leftId,
            "the saved reference id must be preserved while its endpoint is absent");
        Assert(viewModel.ReferenceDevice is null,
            "an absent reference must not be shown as the active reference");

        viewModel.Dispose();
        viewModel = null;

        // Reconnect: the same persistent endpoint id recovers its values.
        fake.Devices = new[] { source, left, right };
        viewModel = new MainViewModel(
            fake,
            new DeviceSettingsStore(settingsPath, TimeSpan.Zero),
            new VolumeBalanceStore(balancePath));

        leftRow = viewModel.Devices.Single(device => device.Id == leftId);
        Assert(viewModel.SourceDevice?.Id == rightId,
            "the saved source must be restored after reconnect");
        Assert(viewModel.ReferenceDevice?.Id == leftId,
            "the saved reference must be restored after reconnect");
        Assert(leftRow.IsSelected &&
               leftRow.Volume == 42 &&
               leftRow.DelayMilliseconds == 120,
            "the reconnected endpoint must recover its group, volume, and delay");
        Assert(Math.Abs(leftRow.BalanceGainPercent - 80) < 0.01,
            "the reconnected endpoint must recover its balance calibration");
        Assert(leftRow.MeasuredOffsetMilliseconds is -87.5 &&
               leftRow.MeasurementConfidencePercent is 92 &&
               leftRow.SyncMeasurementMethod == syncMethod &&
               leftRow.SyncMeasuredAtUtc == measuredAt,
            "the reconnected endpoint must recover its acoustic timing metadata");

        Console.WriteLine($"      view-model persistence verified at {settingsPath}");
    }
    finally
    {
        viewModel?.Dispose();

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Test cleanup is best-effort.
        }
    }
}

static void TestPinkNoiseSignal()
{
    const int rate = 48000;
    const int blockFrames = 480;
    const double duration = 0.5;
    var first = new PinkNoiseSampleProvider(rate, 2, duration, amplitude: 0.18, seed: 42);
    var second = new PinkNoiseSampleProvider(rate, 2, duration, amplitude: 0.18, seed: 42);
    var firstBuffer = new float[blockFrames * 2];
    var secondBuffer = new float[blockFrames * 2];
    var samples = new List<float>();
    var peak = 0f;
    var maxDifference = 0f;

    var reads = (int)Math.Ceiling(rate * duration / blockFrames) + 1;
    for (var block = 0; block < reads; block++)
    {
        first.Read(firstBuffer, 0, firstBuffer.Length);
        second.Read(secondBuffer, 0, secondBuffer.Length);

        for (var index = 0; index < firstBuffer.Length; index++)
        {
            samples.Add(firstBuffer[index]);
            peak = Math.Max(peak, Math.Abs(firstBuffer[index]));
            maxDifference = Math.Max(maxDifference, Math.Abs(firstBuffer[index] - secondBuffer[index]));
        }
    }

    Assert(maxDifference < 1e-7f, "pink noise must be deterministic for a fixed seed");
    Assert(peak > 0.01f, "pink noise must produce an audible test signal");
    Assert(peak <= 0.5f, $"pink noise must stay safely below full scale, peak={peak:0.000}");
    Assert(Math.Abs(samples[0]) < 0.0001f, "pink noise must fade in from silence");

    var middleStart = samples.Count / 2;
    var middle = samples.Skip(middleStart).Take(blockFrames * 2).ToArray();
    var rms = Math.Sqrt(middle.Average(sample => sample * sample));
    Assert(rms > 0.001, $"pink noise mid-signal RMS is too low ({rms:0.000000})");

    // Reading past the sample duration returns silence, not stale noise.
    var tail = new float[blockFrames * 2];
    first.Read(tail, 0, tail.Length);
    Assert(tail.All(sample => sample == 0f), "pink noise must end with silence");

    Console.WriteLine($"      pink-noise peak={peak:0.000}, mid RMS={rms:0.000000}");
}

static void AssertNear(float actual, float expected, float tolerance, string description)
{
    Assert(Math.Abs(actual - expected) <= tolerance,
        $"{description}: got {actual:0.000000}, expected {expected:0.000000} +/- {tolerance:0.000000}");
}

static void AssertSampleGain(ISampleProvider provider, float expected, float tolerance, string description)
{
    var buffer = new float[256];
    var read = provider.Read(buffer, 0, buffer.Length);
    Assert(read == buffer.Length, $"{description}: short read");
    for (var index = 0; index < read; index++)
    {
        Assert(Math.Abs(buffer[index] - expected) <= tolerance,
            $"{description}: sample {index} = {buffer[index]:0.000000}, expected {expected:0.000000}");
    }
}

/// <summary>
/// Exercises the real Windows endpoint notification path. Changes the system
/// volume temporarily and restores it in a finally block. This is a hardware test:
/// it proves the notification is wired, but not that a physical speaker is audible.
/// </summary>
static void TestSourceEndpointBalanceMapping()
{
    using var enumerator = new MMDeviceEnumerator();
    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var endpoint = defaultDevice.AudioEndpointVolume;

    var originalVolume = endpoint.MasterVolumeLevelScalar;
    var originalMute = endpoint.Mute;
    MasterVolumeManager? manager = null;

    try
    {
        endpoint.Mute = false;

        manager = new MasterVolumeManager();
        Assert(manager.IsSystemVolumeAvailable, "default endpoint notification subscription failed");

        // Establish a known logical level first, then bind the default endpoint as
        // the loopback source and apply a 50% source correction.
        manager.MasterVolume = 0.8f;
        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.8f) < 0.03f, 2_000,
            "master volume was not written before source binding");

        manager.SetDeviceBalanceGain(defaultDevice.ID, 0.5f);
        manager.BalanceEnabled = true;
        manager.SetSourceDevice(defaultDevice.ID);

        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.4f) < 0.03f, 2_000,
            $"source balance was not mapped to the endpoint (expected 0.40, got {endpoint.MasterVolumeLevelScalar:0.00})");
        WaitUntil(() => Math.Abs(manager.MasterVolume - 0.8f) < 0.03f, 1_500,
            "binding the source changed the logical master");

        // An external Windows change below the ceiling maps back to the logical level.
        endpoint.MasterVolumeLevelScalar = 0.25f;
        WaitUntil(() => Math.Abs(manager.MasterVolume - 0.5f) < 0.04f, 1_500,
            $"external endpoint change did not map back to logical master (got {manager.MasterVolume:0.00})");
        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.25f) < 0.03f, 1_500,
            "Windows endpoint did not keep the external level below the ceiling");

        // Above the calibrated ceiling the endpoint is re-capped: logical master
        // reaches 1.0, physical stays at the 50% source gain.
        endpoint.MasterVolumeLevelScalar = 0.8f;
        WaitUntil(() => Math.Abs(manager.MasterVolume - 1f) < 0.02f, 1_500,
            "logical master did not clamp at 1.0 above the calibrated ceiling");
        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.5f) < 0.04f, 1_500,
            $"source endpoint was not re-capped at 0.50, got {endpoint.MasterVolumeLevelScalar:0.00}");

        manager.MasterVolume = 0.6f;
        WaitUntilWithMessage(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.3f) < 0.03f, 1_500,
            () => $"in-app master change was not scaled by the source gain " +
                  $"(logical={manager.MasterVolume:0.00}, endpoint={endpoint.MasterVolumeLevelScalar:0.00}, " +
                  $"source gain={manager.SourceBalanceGain:0.00})");

        manager.BalanceEnabled = false;
        WaitUntilWithMessage(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.6f) < 0.03f, 1_500,
            () => $"disabling balance did not restore the source endpoint's full range " +
                  $"(logical={manager.MasterVolume:0.00}, endpoint={endpoint.MasterVolumeLevelScalar:0.00}, " +
                  $"source gain={manager.SourceBalanceGain:0.00})");

        manager.BalanceEnabled = true;
        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.3f) < 0.03f, 1_500,
            "re-enabling balance did not reapply the source gain");

        Console.WriteLine(
            $"      source mapping verified: logical={manager.MasterVolume:0.00}, " +
            $"endpoint={endpoint.MasterVolumeLevelScalar:0.00}, " +
            $"source gain={manager.SourceBalanceGain:0.00}");
    }
    finally
    {
        try
        {
            manager?.Dispose();
        }
        catch (Exception)
        {
            // Test cleanup is best-effort.
        }

        try
        {
            endpoint.MasterVolumeLevelScalar = originalVolume;
            endpoint.Mute = originalMute;
        }
        catch (Exception)
        {
            // Restoring the test volume is best-effort.
        }
    }
}

/// <summary>
/// Exercises the real Windows endpoint notification path. Changes the system
/// volume temporarily and restores it in a finally block. This is a hardware test:
/// it proves the notification is wired, but not that a physical speaker is audible.
/// </summary>
static void TestSystemVolumeNotificationPath()
{
    using var enumerator = new MMDeviceEnumerator();
    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var endpoint = defaultDevice.AudioEndpointVolume;

    var originalVolume = endpoint.MasterVolumeLevelScalar;
    var originalMute = endpoint.Mute;

    try
    {
        endpoint.Mute = false;

        using var manager = new MasterVolumeManager();
        Assert(manager.IsSystemVolumeAvailable, "default endpoint notification subscription failed");

        // 1. External change (as if Windows volume keys were pressed) must be
        // delivered through the real COM notification callback. Watching the count
        // first proves this is not merely the 1-second fallback poll.
        var notificationsBefore = manager.ReceivedExternalNotificationCount;
        endpoint.MasterVolumeLevelScalar = 0.33f;
        WaitUntil(() => manager.ReceivedExternalNotificationCount > notificationsBefore, 1_500,
            "Windows volume notification callback was not received (only polling would work)");
        WaitUntil(() => Math.Abs(manager.MasterVolume - 0.33f) < 0.02f, 1_500,
            "external endpoint volume change did not reach the manager");

        // 2. In-app master change must be written to the Windows endpoint.
        manager.MasterVolume = 0.62f;
        WaitUntil(() => Math.Abs(endpoint.MasterVolumeLevelScalar - 0.62f) < 0.02f, 1_500,
            "in-app master change was not written to the endpoint");

        // 3. Our own write must not cause a feedback loop: the logical value stays
        // stable after the notification window.
        Thread.Sleep(400);
        Assert(Math.Abs(manager.MasterVolume - 0.62f) < 0.02f,
            "own endpoint write caused the logical volume to drift (feedback loop)");

        // 4. External mute must propagate.
        endpoint.Mute = true;
        WaitUntil(() => manager.MasterMute, 1_500,
            "external endpoint mute did not reach the manager");

        // 5. In-app mute must reach the endpoint.
        manager.MasterMute = false;
        WaitUntil(() => !endpoint.Mute, 1_500,
            "in-app unmute was not written to the endpoint");

        Console.WriteLine(
            $"      endpoint volume write/notify verified: manager={manager.MasterVolume:0.00}, " +
            $"endpoint={endpoint.MasterVolumeLevelScalar:0.00}, " +
            $"external notifications={manager.ReceivedExternalNotificationCount}");
    }
    finally
    {
        try
        {
            endpoint.MasterVolumeLevelScalar = originalVolume;
            endpoint.Mute = originalMute;
        }
        catch (Exception)
        {
            // Restoring a test volume is best-effort.
        }
    }
}

static void WaitUntil(Func<bool> condition, int timeoutMilliseconds, string failureMessage)
{
    var deadline = Environment.TickCount64 + timeoutMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        if (condition())
        {
            return;
        }

        Thread.Sleep(20);
    }

    throw new InvalidOperationException(failureMessage);
}

static void WaitUntilWithMessage(Func<bool> condition, int timeoutMilliseconds, Func<string> failureMessage)
{
    var deadline = Environment.TickCount64 + timeoutMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        if (condition())
        {
            return;
        }

        Thread.Sleep(20);
    }

    throw new InvalidOperationException(failureMessage());
}

/// <summary>
/// Measures whether WASAPI loopback capture is affected by endpoint volume on this
/// machine. Pre-volume (expected on most drivers) means the capture level is the
/// same at 100% and 50% endpoint volume, so the routing engine must apply gain to
/// destinations. Post-volume would mean the capture is already attenuated and
/// applying gain again would double-attenuate. This test changes the system volume
/// temporarily and restores it.
/// </summary>
static void TestLoopbackVolumeBehavior()
{
    using var enumerator = new MMDeviceEnumerator();
    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var endpoint = defaultDevice.AudioEndpointVolume;

    var originalVolume = endpoint.MasterVolumeLevelScalar;
    var originalMute = endpoint.Mute;

    try
    {
        endpoint.Mute = false;
        endpoint.MasterVolumeLevelScalar = 1f;
        Thread.Sleep(150);
        var loudRms = CaptureToneRms(enumerator, defaultDevice);

        endpoint.MasterVolumeLevelScalar = 0.35f;
        Thread.Sleep(200);
        var quietRms = CaptureToneRms(enumerator, defaultDevice);

        var ratio = loudRms <= 1e-9 ? 0 : quietRms / loudRms;
        var preVolume = ratio > 0.8;
        var postVolume = ratio > 0.35 && ratio < 0.65;

        Console.WriteLine(
            $"      loopback RMS at 100%: {loudRms:0.000000}, at 35%: {quietRms:0.000000}, ratio: {ratio:0.000}");
        Console.WriteLine(
            preVolume
                ? "      detected: pre-volume loopback (routing gain is required and applied)"
                : postVolume
                    ? "      detected: post-volume loopback (destination gain would double-attenuate)"
                    : "      detected: inconclusive (driver behaviour is not a clean pre/post response)");

        Assert(preVolume || postVolume,
            $"loopback volume behaviour is inconclusive (ratio {ratio:0.000}); " +
            "cannot decide whether destination gain should follow endpoint volume");
    }
    finally
    {
        try
        {
            endpoint.MasterVolumeLevelScalar = originalVolume;
            endpoint.Mute = originalMute;
        }
        catch (Exception)
        {
            // Restoring the test volume is best-effort.
        }
    }
}

static double CaptureToneRms(MMDeviceEnumerator enumerator, MMDevice endpointDevice)
{
    // TargetLoopbackCapture takes ownership of the MMDevice it is given.
    var captureDevice = enumerator.GetDevice(endpointDevice.ID);
    using var capture = new TargetLoopbackCapture(captureDevice, "loopback-volume-probe");

    capture.Start();

    var generator = new SignalGenerator(48000, 2)
    {
        Type = SignalGeneratorType.Sin,
        Frequency = DiagnosticToneFrequencyHz,
        Gain = 0.05
    };

    using (var output = new WasapiOut(endpointDevice, AudioClientShareMode.Shared, true, 100))
    {
        output.Init(generator.ToWaveProvider());
        output.Play();
        Thread.Sleep(1_200);
        output.Stop();
    }

    Thread.Sleep(200);
    capture.Stop();

    var (count, _, _, _) = capture.GetResult();
    Assert(count > 0, "loopback volume probe returned no samples");
    return capture.GetToneRms(DiagnosticToneFrequencyHz);
}

static void WritePcm24(byte[] destination, int offset, int value)
{
    destination[offset] = (byte)value;
    destination[offset + 1] = (byte)(value >> 8);
    destination[offset + 2] = (byte)(value >> 16);
}

static int ReadPcm24(byte[] source, int offset)
{
    var value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
    if ((value & 0x0080_0000) != 0)
    {
        value |= unchecked((int)0xFF00_0000);
    }

    return value;
}

static void Assert(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException(description);
    }
}

internal sealed class DiagnosticsOptions
{
    private DiagnosticsOptions(bool hardware, bool signal)
    {
        RunHardware = hardware;
        RunSignal = signal;
    }

    public bool RunHardware { get; }

    public bool RunSignal { get; }

    public string ModeDescription => RunSignal
        ? "logic tests + muted hardware + two-target signal test"
        : RunHardware
            ? "logic tests + muted hardware initialization"
            : "logic tests only (use --hardware or --signal for device tests)";

    public static DiagnosticsOptions Parse(string[] args)
    {
        var hardware = args.Any(arg =>
            arg.Equals("--hardware", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-h", StringComparison.OrdinalIgnoreCase));
        var signal = args.Any(arg => arg.Equals("--signal", StringComparison.OrdinalIgnoreCase));
        return new DiagnosticsOptions(hardware || signal, signal);
    }
}

internal sealed class ConstantSampleProvider : ISampleProvider
{
    public ConstantSampleProvider(WaveFormat format, float value)
    {
        WaveFormat = format;
        Value = value;
    }

    public WaveFormat WaveFormat { get; }

    public float Value { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        for (var index = 0; index < count; index++)
        {
            buffer[offset + index] = Value;
        }

        return count;
    }
}

internal sealed class TargetLoopbackCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly WaveFormatToFloatConverter _converter;
    private readonly object _gate = new();
    private readonly List<float> _samples = new();
    private float[] _working = Array.Empty<float>();

    public TargetLoopbackCapture(MMDevice device, string name)
    {
        _device = device;
        Name = name;
        _capture = new WasapiLoopbackCapture(device);
        _converter = new WaveFormatToFloatConverter(_capture.WaveFormat);
        _capture.DataAvailable += OnDataAvailable;
    }

    public string Name { get; }

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    public (int Count, double Rms, float Max, double TrimmedRms) GetResult()
    {
        lock (_gate)
        {
            double sumSquares = 0;
            var max = 0f;
            foreach (var sample in _samples)
            {
                sumSquares += sample * sample;
                max = Math.Max(max, Math.Abs(sample));
            }

            var rms = _samples.Count == 0 ? 0 : Math.Sqrt(sumSquares / _samples.Count);
            return (_samples.Count, rms, max, ComputeTrimmedRms());
        }
    }

    /// <summary>
    /// Narrowband RMS at one frequency over the middle 50% of the captured window.
    /// Isolates the generated diagnostic tone from unrelated system audio so the
    /// gain assertions are not affected by whatever else is playing.
    /// </summary>
    public double GetToneRms(double frequency)
    {
        lock (_gate)
        {
            var frames = _samples.Count / 2;
            if (frames < 4 || frequency <= 0)
            {
                return 0;
            }

            var start = frames / 4;
            var end = frames - start;
            var count = end - start;
            var sampleRate = _capture.WaveFormat.SampleRate;
            var omega = 2.0 * Math.PI * frequency / sampleRate;
            var coefficient = 2.0 * Math.Cos(omega);
            double s1 = 0;
            double s2 = 0;

            for (var frame = start; frame < end; frame++)
            {
                var sample = 0.5 * (_samples[frame * 2] + _samples[frame * 2 + 1]);
                var s0 = sample + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
            var amplitude = Math.Sqrt(Math.Max(power, 0.0)) * 2.0 / count;
            return amplitude / Math.Sqrt(2.0);
        }
    }

    /// <summary>
    /// RMS over the middle 50% of the capture window. The window includes silence
    /// before and after the test tone, and small start-time differences between
    /// endpoints would otherwise dominate the plain RMS ratio.
    /// </summary>
    private double ComputeTrimmedRms()
    {
        var count = _samples.Count;
        if (count < 4)
        {
            return 0;
        }

        var start = count / 4;
        var end = count - start;
        double sumSquares = 0;
        for (var index = start; index < end; index++)
        {
            var sample = _samples[index];
            sumSquares += sample * sample;
        }

        return Math.Sqrt(sumSquares / (end - start));
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _device.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var samples = _working;
        var frames = _converter.Convert(e.Buffer, e.BytesRecorded, ref samples);
        _working = samples;

        lock (_gate)
        {
            for (var index = 0; index < frames * 2; index++)
            {
                _samples.Add(samples[index]);
            }
        }
    }
}

/// <summary>
/// Deterministic endpoint list for view-model diagnostics. No hardware is
/// enumerated and no endpoint state is touched by this subclass.
/// </summary>
internal sealed class FakeAudioDeviceEnumerator : AudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } = Array.Empty<AudioDeviceInfo>();

    public override IReadOnlyList<AudioDeviceInfo> GetActiveRenderDevices() => Devices;
}
