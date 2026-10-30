using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Upstream's InputEngineInteractionTests.swift exercised the shared press/hold engine through the
/// Sense controller's trigger. The same cases are translated here onto the Joy-Con 2 path, which is
/// the only one this port has. The analog-trigger hysteresis case has no Joy-Con 2 equivalent.
/// </summary>
public class InputEngineTests
{
    private const string Device = "jc";

    private sealed class Harness
    {
        public RecordingOutputSink Output { get; } = new();
        public ManualHoldScheduler Scheduler { get; } = new();
        public SettingsStore Settings { get; }
        public InputEngine Engine { get; }
        private double _time = 1.0;

        public Harness(Func<ButtonMappingProfile, ButtonMappingProfile>? edit = null, JoyCon2StickCalibration? calibration = null)
        {
            var profile = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);
            if (edit is not null) profile = edit(profile);
            Settings = new SettingsStore(InputSettings.Default with { RightButtons = profile });
            Engine = new InputEngine(Output, Scheduler, Settings.Snapshot);
            Engine.DeviceConnected(Device, JoyCon2Handedness.Right, calibration);
        }

        public void Frame(uint buttons = 0, short gyroZ = 0, ushort stickX = 2048, ushort stickY = 2048)
        {
            _time += 1.0 / 66.0;
            Engine.ProcessFrame(Device, Frames.Report(buttons, stickX, stickY, gyroZ: gyroZ), _time);
        }

        public void Repeat(int count, uint buttons = 0, short gyroZ = 0, ushort stickX = 2048, ushort stickY = 2048)
        {
            for (var i = 0; i < count; i++) Frame(buttons, gyroZ, stickX, stickY);
        }
    }

    private static uint Bit(JoyCon2Button button) => Frames.Bit(button);

    [Fact]
    public void DuplicatePressAndMappingEditStillReleaseCapturedAction()
    {
        var h = new Harness();
        h.Frame();                                  // prime
        h.Frame(Bit(JoyCon2Button.ZR));
        h.Frame(Bit(JoyCon2Button.ZR));             // duplicate snapshot
        h.Settings.Update(s => s with
        {
            RightButtons = s.RightButtons.WithActions(JoyCon2Button.ZR, new ButtonActions(ButtonAction.Click(MouseButton.Right))),
        });
        h.Frame();

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left)], h.Output.Events);
    }

    [Fact]
    public void HoldFiresDeterministicallyAndSuppressesTapAction()
    {
        var copy = new KeyCombo(VirtualKeys.C, KeyModifiers.Control);
        var h = new Harness();                      // A: Escape, hold Ctrl+C

        h.Frame();
        h.Frame(Bit(JoyCon2Button.A));
        Assert.Empty(h.Output.Events);

        h.Scheduler.FireAll();
        h.Frame();

        Assert.Equal([RecordingOutputSink.Down(copy), RecordingOutputSink.Up(copy)], h.Output.Events);
    }

    [Fact]
    public void ShortPressProducesBalancedTap()
    {
        var escape = new KeyCombo(VirtualKeys.Escape);
        var h = new Harness();

        h.Frame();
        h.Frame(Bit(JoyCon2Button.A));
        h.Frame();

        Assert.Equal([RecordingOutputSink.Down(escape), RecordingOutputSink.Up(escape)], h.Output.Events);
    }

    [Fact]
    public void ImmediateMousePressCanAlsoFireAndReleaseHoldAction()
    {
        var space = new KeyCombo(VirtualKeys.Space);
        var h = new Harness(p => p.WithActions(
            JoyCon2Button.ZR,
            new ButtonActions(ButtonAction.Click(MouseButton.Left), ButtonAction.Key(VirtualKeys.Space))));

        h.Frame();
        h.Frame(Bit(JoyCon2Button.ZR));
        h.Scheduler.FireAll();
        h.Frame();

        Assert.Equal(
            [
                RecordingOutputSink.Down(MouseButton.Left),
                RecordingOutputSink.Down(space),
                RecordingOutputSink.Up(MouseButton.Left),
                RecordingOutputSink.Up(space),
            ],
            h.Output.Events);
    }

    [Fact]
    public void GlobalDisableReleasesMouseDownBeforeIgnoringReleaseReport()
    {
        var h = new Harness();

        h.Frame();
        h.Frame(Bit(JoyCon2Button.ZR));
        h.Settings.Update(s => s with { IsEnabled = false });
        h.Engine.SetInputEnabled(false);
        h.Frame();

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left)], h.Output.Events);
    }

    [Fact]
    public void DisconnectReleasesOutputWhenPhysicalReleaseWasLost()
    {
        var h = new Harness();

        h.Frame();
        h.Frame(Bit(JoyCon2Button.Plus));
        h.Engine.DeviceDisconnected(Device);

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Middle), RecordingOutputSink.Up(MouseButton.Middle)], h.Output.Events);
    }

    [Fact]
    public void StopReleasesHeldOutputAndCancelsPendingHold()
    {
        var h = new Harness(p => p.WithActions(
            JoyCon2Button.R,
            new ButtonActions(ButtonAction.Click(MouseButton.Right), ButtonAction.Key(VirtualKeys.Space))));

        h.Frame();
        h.Frame(Bit(JoyCon2Button.R));
        h.Engine.Stop();
        h.Scheduler.FireAll();

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Right), RecordingOutputSink.Up(MouseButton.Right)], h.Output.Events);
        Assert.Equal(0, h.Engine.ActiveOutputCount);
    }

    [Fact]
    public void ButtonsAlreadyHeldAtConnectDoNotFire()
    {
        var h = new Harness();

        h.Frame(Bit(JoyCon2Button.ZR));             // first frame primes with ZR already down
        h.Frame(Bit(JoyCon2Button.ZR));
        h.Frame();

        Assert.Empty(h.Output.Events);
    }

    [Fact]
    public void ReenablingDoesNotFireAButtonHeldThroughTheChange()
    {
        var h = new Harness();
        h.Frame();
        h.Settings.Update(s => s with { IsEnabled = false });
        h.Engine.SetInputEnabled(false);
        h.Frame(Bit(JoyCon2Button.ZR));

        h.Settings.Update(s => s with { IsEnabled = true });
        h.Frame(Bit(JoyCon2Button.ZR));
        h.Frame();

        Assert.Empty(h.Output.Events);
    }

    [Fact]
    public void ZeroFilledStartupReportIsIgnoredEntirely()
    {
        var h = new Harness();
        h.Frame();

        var zeroFilled = new byte[JoyCon2ReportDecoder.MinimumLength];
        BitConverter.TryWriteBytes(zeroFilled.AsSpan(0x04), Bit(JoyCon2Button.ZR));
        Assert.True(JoyCon2ReportDecoder.TryDecode(zeroFilled, out var report));
        h.Engine.ProcessFrame(Device, report, 5.0);

        Assert.Empty(h.Output.Events);
    }

    /// <summary>
    /// Upstream's default maps X to drag: gyro moves the cursor only while it is held. This is a
    /// deliberate clutch, and it is what keeps an idle controller from drifting the cursor.
    /// </summary>
    [Fact]
    public void DragClutchGatesCursorMovement()
    {
        var h = new Harness();
        h.Frame();
        h.Repeat(30, gyroZ: 1000);
        Assert.Empty(h.Output.Moves);

        h.Repeat(30, Bit(JoyCon2Button.X), gyroZ: 1000);
        var movesWhileHeld = h.Output.Moves.Count;
        Assert.Equal(30, movesWhileHeld);
        Assert.Contains(h.Output.Moves, m => Math.Abs(m.Dx) > 0.5);

        h.Repeat(30, gyroZ: 1000);
        Assert.Equal(movesWhileHeld, h.Output.Moves.Count);
    }

    [Fact]
    public void WithoutADragMappingGyroAlwaysMovesTheCursor()
    {
        var h = new Harness(p => p.WithActions(JoyCon2Button.X, ButtonActions.Empty));
        h.Frame();                                  // the priming frame is routed too
        h.Output.Moves.Clear();
        h.Repeat(30, gyroZ: 1000);

        Assert.Equal(30, h.Output.Moves.Count);
    }

    /// <summary>
    /// Right Joy-Con 2: yaw is raw Z, and the processor inverts it, so positive raw Z moves left.
    /// Upstream validated that direction for its one-handed grip; recorded here so it cannot flip silently.
    /// </summary>
    [Fact]
    public void PositiveRawZMovesTheCursorLeftOnARightController()
    {
        var h = new Harness(p => p.WithActions(JoyCon2Button.X, ButtonActions.Empty));
        h.Frame();
        h.Repeat(30, gyroZ: 1000);

        Assert.True(h.Output.Moves.Sum(m => m.Dx) < 0);
        Assert.InRange(h.Output.Moves.Sum(m => m.Dy), -0.001, 0.001);
    }

    [Fact]
    public void ScrollModeRedirectsGyroToScrolling()
    {
        var h = new Harness(p => p.WithPress(JoyCon2Button.B, ButtonAction.Scroll));
        h.Frame();
        h.Repeat(20, Bit(JoyCon2Button.B), gyroZ: 1000);

        Assert.Empty(h.Output.Moves);
        Assert.Equal(20, h.Output.Scrolls.Count);
    }

    [Fact]
    public void CursorControlOffStopsMotionButNotButtons()
    {
        var h = new Harness();
        h.Settings.Update(s => s with { CursorControlEnabled = false });
        h.Frame();
        h.Repeat(10, Bit(JoyCon2Button.X) | Bit(JoyCon2Button.ZR), gyroZ: 1000);
        h.Frame();

        Assert.Empty(h.Output.Moves);
        Assert.Equal([RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left)], h.Output.Events);
    }

    [Fact]
    public void StickScrollingWaitsForCalibrationAndThenFollowsTheStick()
    {
        var uncalibrated = new Harness();
        uncalibrated.Frame();
        uncalibrated.Repeat(10, stickY: 3400);
        Assert.Empty(uncalibrated.Output.Scrolls);

        var calibrated = new Harness(calibration: new JoyCon2StickCalibration(2048, 2048, 1300, 1300, 1300, 1300));
        calibrated.Frame();
        calibrated.Repeat(10, stickY: 3400);

        Assert.Equal(10, calibrated.Output.Scrolls.Count);
        // Upstream inverts Y "for natural scroll direction": a higher raw Y scrolls up (negative dy).
        Assert.All(calibrated.Output.Scrolls, s => Assert.True(s.Dy < 0));
    }

    [Fact]
    public void StickInsideDeadzoneDoesNotScroll()
    {
        var h = new Harness(calibration: new JoyCon2StickCalibration(2048, 2048, 1300, 1300, 1300, 1300));
        h.Frame();
        h.Repeat(10, stickX: 2100, stickY: 2000);

        Assert.Empty(h.Output.Scrolls);
    }
}
