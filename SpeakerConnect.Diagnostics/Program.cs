using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeakerConnect.Audio;
using SpeakerConnect.Infrastructure;
using SpeakerConnect.Models;

var options = DiagnosticsOptions.Parse(args);

Console.WriteLine("Speaker Connect diagnostics");
Console.WriteLine("===========================");
Console.WriteLine($"Mode: {options.ModeDescription}");
Console.WriteLine();

var failures = new List<string>();
var passed = 0;

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
Run("master volume sample gain (unity/trim/mute)", TestMasterVolumeSampleGain);
Run("master volume ramp is smooth under rapid changes", TestMasterVolumeRampSmoothness);
Run("volume balance gain math is attenuation-only", TestVolumeBalanceMath);
Run("volume balance application, trims, mute, and ramps", TestVolumeBalanceApplication);
Run("volume balance persistence and reconnect by device id", TestVolumeBalanceStore);
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
    var originalVolume = defaultEndpoint.MasterVolumeLevelScalar;
    var originalMute = defaultEndpoint.Mute;

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

        // Phase A: master at unity. The Windows endpoint volume is temporarily
        // raised to 1.0 so the group gain is the only variable between phases.
        engine.MasterVolume = 1f;
        Thread.Sleep(350);
        var loud = RunTonePhase(captures, sourceDevice, 1_400);

        // Phase B: master at 50%. The routed copies must be 6 dB quieter.
        foreach (var capture in captures)
        {
            capture.Reset();
        }

        engine.MasterVolume = 0.5f;
        Thread.Sleep(350);
        var quiet = RunTonePhase(captures, sourceDevice, 1_400);

        foreach (var status in engine.GetChannelStatuses())
        {
            Console.WriteLine(
                $"      routed {status.DeviceName}: {status.State}, buffer={status.BufferedMilliseconds:0} ms, " +
                $"underruns={status.UnderrunCount}, drops={status.DroppedFrameCount}");
        }

        for (var index = 0; index < captures.Count; index++)
        {
            var loudRms = loud[index].TrimmedRms;
            var quietRms = quiet[index].TrimmedRms;
            var ratio = loudRms <= 1e-9 ? 0 : quietRms / loudRms;
            Console.WriteLine(
                $"      {captures[index].Name}: rms at master 1.0 = {loudRms:0.000000}, " +
                $"at master 0.5 = {quietRms:0.000000}, ratio {ratio:0.000}");
            Assert(loudRms > 0.0001, $"{captures[index].Name} did not receive the tone");
            Assert(ratio > 0.40f && ratio < 0.60f,
                $"{captures[index].Name}: master volume did not scale the routed samples " +
                $"(expected ratio ~0.5, got {ratio:0.000})");
        }

        // Phase C: with master back at unity, an independent balance trim must
        // attenuate only its own destination and leave the other unchanged. The
        // trimmed RMS (middle 50% of the capture window) is used so silence before
        // and after the tone does not dominate the ratio.
        foreach (var capture in captures)
        {
            capture.Reset();
        }

        engine.SetBalanceGain(targets[0].Id, 0.25f);
        engine.SetBalanceGain(targets[1].Id, 1f);
        engine.BalanceEnabled = true;
        engine.MasterVolume = 1f;
        Thread.Sleep(350);
        var balanced = RunTonePhase(captures, sourceDevice, 1_400);

        for (var index = 0; index < captures.Count; index++)
        {
            var baseline = loud[index].TrimmedRms;
            var balancedRms = balanced[index].TrimmedRms;
            var ratio = baseline <= 1e-9 ? 0 : balancedRms / baseline;
            var expected = index == 0 ? 0.25 : 1.0;
            Console.WriteLine(
                $"      {captures[index].Name}: balance gain {expected:0.00} -> " +
                $"rms {balancedRms:0.000000} vs {baseline:0.000000}, ratio {ratio:0.000}");
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
        // master-volume manager no longer re-applies its logical state.
        try
        {
            defaultEndpoint.MasterVolumeLevelScalar = originalVolume;
            defaultEndpoint.Mute = originalMute;
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

static List<(int Count, double Rms, float Max, double TrimmedRms)> RunTonePhase(
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
        Frequency = 440,
        Gain = 0.04
    };

    using (var toneOutput = new WasapiOut(sourceDevice, AudioClientShareMode.Shared, true, 100))
    {
        toneOutput.Init(generator.ToWaveProvider());
        toneOutput.Play();
        Thread.Sleep(toneMilliseconds);
        toneOutput.Stop();
    }

    Thread.Sleep(250);
    var results = new List<(int Count, double Rms, float Max, double TrimmedRms)>();
    foreach (var capture in captures)
    {
        capture.Stop();
        results.Add(capture.GetResult());
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
        Frequency = 440,
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

    var (count, rms, _, _) = capture.GetResult();
    Assert(count > 0, "loopback volume probe returned no samples");
    return rms;
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
