using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

public class MotionPacerTests
{
    private const double Spread = 1.0 / 66.0;

    [Fact]
    public void ReleasesADeltaEvenlyAcrossTheWindow()
    {
        var pacer = new MotionPacer();
        pacer.Add(12, -6, now: 0, Spread);

        var quarter = pacer.Take(Spread / 4);
        Assert.Equal(3, quarter.Dx, 1e-9);
        Assert.Equal(-1.5, quarter.Dy, 1e-9);

        var half = pacer.Take(Spread * 3 / 4);
        Assert.Equal(6, half.Dx, 1e-9);
    }

    [Fact]
    public void ConservesTotalMotionExactlyAcrossManyReports()
    {
        var pacer = new MotionPacer();
        var random = new Random(7);
        double addedX = 0, addedY = 0, takenX = 0, takenY = 0, t = 0;

        for (var report = 0; report < 500; report++)
        {
            // Jittery cadence like the real link: 13–18 ms between reports, emitted at 240 Hz.
            var dx = random.NextDouble() * 10 - 5;
            var dy = random.NextDouble() * 10 - 5;
            pacer.Add(dx, dy, t, Spread);
            addedX += dx;
            addedY += dy;

            var next = t + 0.013 + random.NextDouble() * 0.005;
            for (var tick = t + 1.0 / 240; tick < next; tick += 1.0 / 240)
            {
                var (x, y) = pacer.Take(tick);
                takenX += x;
                takenY += y;
            }

            t = next;
        }

        var (fx, fy) = pacer.Take(t + 1);
        Assert.Equal(addedX, takenX + fx, 1e-9);
        Assert.Equal(addedY, takenY + fy, 1e-9);
    }

    [Fact]
    public void ALateReportCarriesTheUnreleasedRemainderForward()
    {
        var pacer = new MotionPacer();
        pacer.Add(10, 0, now: 0, Spread);
        var first = pacer.Take(Spread / 2);                 // half released
        pacer.Add(10, 0, now: Spread / 2, Spread);          // remainder 5 joins the new 10

        var rest = pacer.Take(Spread / 2 + Spread);
        Assert.Equal(20, first.Dx + rest.Dx, 1e-9);
        Assert.False(pacer.HasPending);
    }

    [Fact]
    public void FlushReturnsEverythingPending()
    {
        var pacer = new MotionPacer();
        pacer.Add(7, 3, now: 0, Spread);
        pacer.Take(Spread / 3);

        var flushed = pacer.Flush();
        Assert.Equal(7 * 2.0 / 3, flushed.Dx, 1e-9);
        Assert.False(pacer.HasPending);
        Assert.Equal((0.0, 0.0), pacer.Take(1));
    }
}

public class ActivityDetectorTests
{
    private static JoyCon2Report Report(uint buttons = 0, ushort stickX = 2048, ushort stickY = 2048, short gyroZ = 0) =>
        Frames.Report(buttons, stickX, stickY, gyroZ: gyroZ);

    [Fact]
    public void RestingControllerIsIdle()
    {
        Assert.False(ActivityDetector.IsActive(Report(gyroZ: 10), JoyCon2Handedness.Right)); // ~0.6 °/s
    }

    [Fact]
    public void ButtonsStickAndDeliberateMotionCountAsUse()
    {
        Assert.True(ActivityDetector.IsActive(Report(buttons: Frames.Bit(JoyCon2Button.ZR)), JoyCon2Handedness.Right));
        Assert.True(ActivityDetector.IsActive(Report(stickY: 3000), JoyCon2Handedness.Right));
        Assert.True(ActivityDetector.IsActive(Report(gyroZ: 400), JoyCon2Handedness.Right)); // ~24 °/s
    }

    [Fact]
    public void StatusByteAndFactoryStickOffsetAreNotUse()
    {
        // 0xE0 in byte 0x07 at rest, and a stick resting 45 counts off nominal, as on real hardware.
        Assert.False(ActivityDetector.IsActive(Report(buttons: 0xE000_0000, stickX: 2093), JoyCon2Handedness.Right));
    }

    [Fact]
    public void ZeroFilledStartupReportIsNotUse()
    {
        var zero = new byte[JoyCon2ReportDecoder.MinimumLength];
        BitConverter.TryWriteBytes(zero.AsSpan(0x04), Frames.Bit(JoyCon2Button.ZR));
        Assert.True(JoyCon2ReportDecoder.TryDecode(zero, out var report));
        Assert.False(ActivityDetector.IsActive(report, JoyCon2Handedness.Right));
    }
}

/// <summary>Translated from upstream's RadialMenuGeometryTests.swift, plus drawing consistency.</summary>
public class RadialMenuGeometryTests
{
    [Fact]
    public void ArrowMenuDirectionsMatchVisualOrder()
    {
        var config = RadialMenuConfiguration.Default;
        Assert.Equal(new RadialMenuSelection(false, 0), RadialMenuGeometry.Resolve(0, -60, config).Selection);
        Assert.Equal(new RadialMenuSelection(false, 1), RadialMenuGeometry.Resolve(60, 0, config).Selection);
        Assert.Equal(new RadialMenuSelection(false, 2), RadialMenuGeometry.Resolve(0, 60, config).Selection);
        Assert.Equal(new RadialMenuSelection(false, 3), RadialMenuGeometry.Resolve(-60, 0, config).Selection);
    }

    [Fact]
    public void DeadzoneHasNoSelection()
    {
        Assert.Null(RadialMenuGeometry.Resolve(10, 10, RadialMenuConfiguration.Default).Selection);
    }

    [Fact]
    public void OffsetIsClampedToVisibleGhostRadius()
    {
        var config = RadialMenuConfiguration.Default;
        var (x, y, _) = RadialMenuGeometry.Resolve(1000, 0, config);
        Assert.Equal(config.MenuRadius - 10, x, 0.001);
        Assert.Equal(0, y, 0.001);
    }

    /// <summary>
    /// What is drawn must be what is selected: the middle of every drawn slice resolves to that slice,
    /// at several rotations, on both rings.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(30)]
    [InlineData(-72)]
    public void DrawnSlicesMatchSelection(double rotation)
    {
        var items = Enumerable.Range(0, 5).Select(i => new RadialMenuItem($"{i}", "", ButtonAction.None)).ToArray();
        var config = RadialMenuConfiguration.Default with
        {
            Items = items[..4],
            InnerRingRotation = rotation,
            OuterRingEnabled = true,
            OuterRingItems = items,
            OuterRingRotation = rotation / 2,
        };

        void Check(int count, double rotationDegrees, double radius, bool outer)
        {
            for (var i = 0; i < count; i++)
            {
                var middle = RadialMenuGeometry.SliceStartAngle(i, count, rotationDegrees) + Math.PI / count;
                var (_, _, selection) = RadialMenuGeometry.Resolve(Math.Cos(middle) * radius, Math.Sin(middle) * radius, config);
                Assert.Equal(new RadialMenuSelection(outer, i), selection);
            }
        }

        Check(4, rotation, config.DeadzoneSize + config.InnerRingSize / 2, outer: false);
        Check(5, rotation / 2, config.DeadzoneSize + config.InnerRingSize + config.OuterRingSize / 2 - 5, outer: true);
    }
}

public class RadialMenuEngineTests
{
    private sealed class RecordingPresenter : IRadialMenuPresenter
    {
        public int Shown { get; private set; }
        public List<RadialMenuSelection?> Updates { get; } = [];
        public List<RadialMenuItem?> Hidden { get; } = [];
        public void Show(RadialMenuConfiguration configuration) => Shown++;
        public void Update(double x, double y, RadialMenuSelection? selection) => Updates.Add(selection);
        public void Hide(RadialMenuItem? selected) => Hidden.Add(selected);
    }

    private static (InputEngine Engine, RecordingOutputSink Output, RecordingPresenter Menu) Build()
    {
        var output = new RecordingOutputSink();
        var menu = new RecordingPresenter();
        var engine = new InputEngine(output, new ManualHoldScheduler(), () => InputSettings.Default, menu);
        engine.DeviceConnected("jc", JoyCon2Handedness.Right, null);
        return (engine, output, menu);
    }

    private static uint B => Frames.Bit(JoyCon2Button.B);

    /// <summary>
    /// Right Joy-Con 2: negative raw Z moves right (see the cursor-direction test), so holding B and
    /// turning that way lands on the "Right" slice, and releasing taps the Right arrow.
    /// </summary>
    [Fact]
    public void HoldingBAndPointingRightSelectsRightOnRelease()
    {
        var (engine, output, menu) = Build();
        var t = 1.0;
        engine.ProcessFrame("jc", Frames.Report(), t += 0.015);
        for (var i = 0; i < 30; i++) engine.ProcessFrame("jc", Frames.Report(B, gyroX: -300, gyroZ: -1500), t += 0.015);
        engine.ProcessFrame("jc", Frames.Report(), t += 0.015);

        Assert.Equal(1, menu.Shown);
        Assert.Equal(new RadialMenuSelection(false, 1), menu.Updates[^1]);
        Assert.Equal("Right", Assert.Single(menu.Hidden)!.Label);

        var right = new KeyCombo(VirtualKeys.Right);
        Assert.Equal([RecordingOutputSink.Down(right), RecordingOutputSink.Up(right)], output.Events);
        Assert.Empty(output.Moves); // the cursor holds still while the menu is open
    }

    [Fact]
    public void ReleasingInTheCenterRunsNothing()
    {
        var (engine, output, menu) = Build();
        engine.ProcessFrame("jc", Frames.Report(), 1.0);
        engine.ProcessFrame("jc", Frames.Report(B), 1.015);
        engine.ProcessFrame("jc", Frames.Report(), 1.03);

        Assert.Null(Assert.Single(menu.Hidden));
        Assert.Empty(output.Events);
    }

    [Fact]
    public void DisconnectClosesAnOpenMenuWithoutRunningIt()
    {
        var (engine, output, menu) = Build();
        var t = 1.0;
        engine.ProcessFrame("jc", Frames.Report(), t += 0.015);
        for (var i = 0; i < 30; i++) engine.ProcessFrame("jc", Frames.Report(B, gyroX: -300, gyroZ: -1500), t += 0.015);
        engine.DeviceDisconnected("jc");

        Assert.Null(Assert.Single(menu.Hidden));
        Assert.Empty(output.Events);
    }
}

public class WindowsDefaultsTests
{
    [Fact]
    public void YIsHoldToDictateDoubleTapEnterAndBIsTheRadialMenu()
    {
        var right = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);
        Assert.Equal(
            new ButtonActions(
                ButtonAction.None,
                ButtonAction.HoldToggle(VirtualKeys.Space, KeyModifiers.Control),
                ButtonAction.Key(VirtualKeys.Return),
                ButtonAction.Repeat(VirtualKeys.Back, KeyModifiers.Control, 0.5)),
            right.ActionsFor(JoyCon2Button.Y));
        Assert.Equal(new ButtonActions(ButtonAction.RadialMenu), right.ActionsFor(JoyCon2Button.B));
        Assert.Equal(new KeyCombo(VirtualKeys.H, KeyModifiers.Win), ActionExecutor.ShortcutFor(SystemAction.Dictation));

        var left = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Left);
        Assert.Equal(right.ActionsFor(JoyCon2Button.Y), left.ActionsFor(JoyCon2Button.DpadRight));
        Assert.Equal(new ButtonActions(ButtonAction.RadialMenu), left.ActionsFor(JoyCon2Button.DpadDown));
    }

    [Fact]
    public void SettingsWithARadialMenuRoundTripThroughJson()
    {
        var settings = InputSettings.Default with
        {
            RadialMenu = RadialMenuConfiguration.Default with { InnerRingRotation = 45, Name = "Custom" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<InputSettings>(json)!;

        Assert.Equal(45, restored.RadialMenu.InnerRingRotation);
        Assert.Equal("Custom", restored.RadialMenu.Name);
        Assert.Equal(RadialMenuConfiguration.Default.Items, restored.RadialMenu.Items);
    }
}
