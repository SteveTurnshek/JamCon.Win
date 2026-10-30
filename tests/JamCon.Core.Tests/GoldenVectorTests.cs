using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using JamCon.Core.Engine;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>One captured notification: its receive time and the payload exactly as the radio delivered it.</summary>
internal sealed record CapturedFrame(double Timestamp, byte[] Payload);

/// <summary>
/// Reads the probe's binary capture format, version 1: a 16-byte header (magic "1CJ2", version,
/// eight reserved bytes), then per frame a 4-byte length, an 8-byte timestamp in microseconds and
/// the raw payload. All little-endian. See tools/Jc2Probe/FrameRecorder.cs.
/// </summary>
internal static class CaptureFile
{
    public const uint Magic = 0x324A4331;

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static (uint Magic, uint Version, IReadOnlyList<CapturedFrame> Frames) Read(string name)
    {
        var bytes = File.ReadAllBytes(PathOf(name));
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));

        var frames = new List<CapturedFrame>();
        var offset = 16;
        while (offset < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            var micros = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 4));
            frames.Add(new CapturedFrame(micros / 1_000_000.0, bytes.AsSpan(offset + 12, length).ToArray()));
            offset += 12 + length;
        }

        return (magic, version, frames);
    }
}

/// <summary>
/// Checks against the five-minute right Joy-Con 2 capture from the Phase 1 run.
/// </summary>
/// <remarks>
/// <para>
/// The plan called for asserting that the C# decoder matches the validated Swift decoder frame by
/// frame. That comparison is not possible here: there is no Swift toolchain on Windows. These tests
/// do the next-best thing and check against what physically happened during the capture: the button
/// held at each moment (docs/Windows-Port.md, "Golden-vector fixture"), the measured cadence, and
/// the gyro settling at rest.
/// </para>
/// <para>
/// The sidecar comparison is a regression pin, not independent verification: the sidecar was written
/// by this same decoder, so it catches the decoder changing, not the decoder having been wrong.
/// </para>
/// </remarks>
public class GoldenVectorTests
{
    private const string Capture = "jc2-right-5min.bin";
    private const string Sidecar = "jc2-right-5min.jsonl";

    /// <summary>Factory calibration the controller reported during the capture.</summary>
    private static readonly JoyCon2StickCalibration CaptureCalibration = new(2077, 2093, 1215, 1179, 1213, 1194);

    /// <summary>The deliberate single-button presses, as recorded in docs/Windows-Port.md.</summary>
    private static readonly (double Start, JoyCon2Button Button)[] PressTimeline =
    [
        (38.2, JoyCon2Button.A), (41.1, JoyCon2Button.B), (46.1, JoyCon2Button.X), (50.5, JoyCon2Button.Y),
        (54.8, JoyCon2Button.R), (58.9, JoyCon2Button.ZR), (63.3, JoyCon2Button.Plus),
        (68.7, JoyCon2Button.StickClick), (84.7, JoyCon2Button.C), (193.9, JoyCon2Button.SL),
        (198.1, JoyCon2Button.SR), (203.2, JoyCon2Button.C),
    ];

    private static readonly Lazy<IReadOnlyList<CapturedFrame>> Frames = new(() => CaptureFile.Read(Capture).Frames);

    [Fact]
    public void CaptureHeaderAndFramesAreWellFormed()
    {
        var (magic, version, frames) = CaptureFile.Read(Capture);

        Assert.Equal(CaptureFile.Magic, magic);
        Assert.Equal(1u, version);
        Assert.Equal(20_088, frames.Count);
        Assert.All(frames, f => Assert.True(f.Payload.Length >= JoyCon2ReportDecoder.MinimumLength));

        for (var i = 1; i < frames.Count; i++) Assert.True(frames[i].Timestamp > frames[i - 1].Timestamp);
    }

    [Fact]
    public void EveryFrameDecodes()
    {
        Assert.All(Frames.Value, f => Assert.True(JoyCon2ReportDecoder.TryDecode(f.Payload, out _)));
    }

    /// <summary>Regression pin: the decoder still produces exactly what it wrote into the sidecar.</summary>
    [Fact]
    public void DecoderReproducesTheSidecarFrameForFrame()
    {
        var frames = Frames.Value;
        var index = 0;
        foreach (var line in File.ReadLines(CaptureFile.PathOf(Sidecar)))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("type").GetString() != "frame") continue;

            var frame = frames[index++];
            Assert.Equal(root.GetProperty("raw").GetString(), Convert.ToHexString(frame.Payload));
            Assert.Equal(
                double.Parse(root.GetProperty("t").GetRawText(), CultureInfo.InvariantCulture),
                frame.Timestamp,
                0.000_002);

            Assert.True(JoyCon2ReportDecoder.TryDecode(frame.Payload, out var report));
            Assert.Equal(root.GetProperty("buttons").GetUInt32(), report.Buttons);
            Assert.Equal(root.GetProperty("rightStick").GetProperty("x").GetUInt16(), report.RightStick.X);
            Assert.Equal(root.GetProperty("rightStick").GetProperty("y").GetUInt16(), report.RightStick.Y);
            Assert.Equal(root.GetProperty("gyro").GetProperty("x").GetInt16(), report.Imu.GyroX);
            Assert.Equal(root.GetProperty("gyro").GetProperty("y").GetInt16(), report.Imu.GyroY);
            Assert.Equal(root.GetProperty("gyro").GetProperty("z").GetInt16(), report.Imu.GyroZ);
            Assert.Equal(root.GetProperty("accel").GetProperty("z").GetInt16(), report.Imu.AccelZ);
        }

        Assert.Equal(frames.Count, index);
    }

    [Fact]
    public void CadenceMatchesTheMeasuredSixtySixHertz()
    {
        var frames = Frames.Value;
        var average = (frames.Count - 1) / (frames[^1].Timestamp - frames[0].Timestamp);
        Assert.InRange(average, 66.0, 67.5);

        var estimator = new Diagnostics.CadenceEstimator();
        foreach (var frame in frames) estimator.Record(frame.Timestamp);
        Assert.InRange(estimator.EstimatedRateHz, 64.0, 70.0);
    }

    /// <summary>
    /// Physical ground truth: during each deliberate press, the decoded field shows that button and
    /// only that button, and nothing is pressed between presses.
    /// </summary>
    [Fact]
    public void DecodedButtonsMatchThePhysicalPressTimeline()
    {
        var frames = Frames.Value;
        uint ButtonsAt(double t)
        {
            var frame = frames.First(f => f.Timestamp >= t);
            Assert.True(JoyCon2ReportDecoder.TryDecode(frame.Payload, out var report));
            return report.Buttons & 0x00FF_FFFF; // byte 0x07 is status, not buttons
        }

        foreach (var (start, button) in PressTimeline)
        {
            Assert.Equal(Tests.Frames.Bit(button), ButtonsAt(start + 0.8));
            Assert.Equal(0u, ButtonsAt(start - 0.8));
        }
    }

    [Fact]
    public void StatusByteIsConstantThroughoutTheCapture()
    {
        Assert.All(Frames.Value, f => Assert.Equal(0xE0, f.Payload[0x07]));
    }

    /// <summary>
    /// The gyro processor finds a still window in real data and settles on a plausible bias. A wrong
    /// gyro offset or scale would show up here as no calibration, or as an implausible bias.
    /// </summary>
    [Fact]
    public void GyroAutoNeutralSettlesOnRealData()
    {
        var processor = new GyroProcessor();
        var settings = GyroSettings.Default;
        foreach (var frame in Frames.Value)
        {
            Assert.True(JoyCon2ReportDecoder.TryDecode(frame.Payload, out var report));
            if (report.Imu.IsZeroFilled) continue;
            var axes = GyroRemapper.Remap(report.Imu.GyroX, report.Imu.GyroY, report.Imu.GyroZ, JoyCon2Handedness.Right);
            processor.Process(axes.Pitch, axes.Yaw, axes.Roll, frame.Timestamp, settings, GyroConstants.DegreesPerSecondPerCount);
        }

        Assert.True(processor.IsCalibrated);
        var bias = processor.LastDebugState!;
        Assert.InRange(Math.Sqrt(bias.BiasX * bias.BiasX + bias.BiasY * bias.BiasY + bias.BiasZ * bias.BiasZ), 0, 3);
    }

    /// <summary>
    /// The whole engine, replayed over the real capture on its own clock with the default Windows
    /// profile, produces exactly the outputs those physical presses should.
    /// </summary>
    [Fact]
    public void EngineReplayProducesTheExpectedOutputsForThePhysicalPresses()
    {
        var output = new RecordingOutputSink();
        var scheduler = new ManualHoldScheduler();
        var engine = new InputEngine(output, scheduler, () => InputSettings.Default);
        engine.DeviceConnected("jc", JoyCon2Handedness.Right, CaptureCalibration);

        var timedEvents = new List<(double Time, SyntheticOutputEvent Event)>();
        var movesOutsideClutch = 0;
        var movesInsideClutch = 0;

        foreach (var frame in Frames.Value)
        {
            Assert.True(JoyCon2ReportDecoder.TryDecode(frame.Payload, out var report));

            var before = output.Events.Count;
            var movesBefore = output.Moves.Count;

            scheduler.Advance(frame.Timestamp);
            engine.ProcessFrame("jc", report, frame.Timestamp);
            scheduler.StampPending(frame.Timestamp);

            for (var i = before; i < output.Events.Count; i++) timedEvents.Add((frame.Timestamp, output.Events[i]));

            if (output.Moves.Count > movesBefore)
            {
                if (JoyCon2ButtonLayout.IsPressed(report.Buttons, JoyCon2Button.X, JoyCon2Handedness.Right)) movesInsideClutch++;
                else movesOutsideClutch++;
            }
        }

        engine.Stop();

        // X is the drag clutch in the default profile: no motion without it, plenty with it.
        Assert.Equal(0, movesOutsideClutch);
        Assert.True(movesInsideClutch > 100, $"only {movesInsideClutch} frames moved the cursor");

        var deliberate = timedEvents.Where(e => e.Time is > 37.5 and < 215).Select(e => e.Event).ToArray();
        var copy = new KeyCombo(VirtualKeys.C, KeyModifiers.Control);
        var enter = new KeyCombo(VirtualKeys.Return);
        var forward = new KeyCombo(VirtualKeys.BrowserForward);
        var down = new KeyCombo(VirtualKeys.Down);
        var up = new KeyCombo(VirtualKeys.Up);

        Assert.Equal(
            [
                RecordingOutputSink.Down(copy), RecordingOutputSink.Up(copy),              // A held: hold action
                // B: unmapped. X: drag clutch, no output.
                RecordingOutputSink.Down(enter), RecordingOutputSink.Up(enter),            // Y held: hold action
                RecordingOutputSink.Down(MouseButton.Right), RecordingOutputSink.Up(MouseButton.Right),
                RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left),
                RecordingOutputSink.Down(MouseButton.Middle), RecordingOutputSink.Up(MouseButton.Middle),
                RecordingOutputSink.Down(forward), RecordingOutputSink.Up(forward),        // stick held: hold action
                // C: unmapped.
                RecordingOutputSink.Down(down), RecordingOutputSink.Up(down),              // SL: tap on release
                RecordingOutputSink.Down(up), RecordingOutputSink.Up(up),                  // SR: tap on release
            ],
            deliberate);

        Assert.Equal(0, engine.ActiveOutputCount);
    }
}
