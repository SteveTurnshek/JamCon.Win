using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

internal sealed class RecordingGamepad : IGamepadOutput
{
    public bool IsAvailable { get; set; } = true;
    public List<GamepadReport> Reports { get; } = [];
    public int Releases { get; private set; }
    public GamepadReport Last => Reports.Count == 0 ? GamepadReport.Neutral : Reports[^1];

    public void Send(GamepadReport report) => Reports.Add(report);

    public void Release()
    {
        Releases++;
        Reports.Add(GamepadReport.Neutral);
    }
}

/// <summary>Xbox controller mode: Joy-Cons as one virtual Xbox pad, with gyro aim.</summary>
public class ControllerModeTests
{
    private const string Right = "right";
    private const string Left = "left";
    private static readonly JoyCon2StickCalibration Calibration = new(2048, 2048, 1300, 1300, 1300, 1300);

    private sealed class Harness
    {
        public RecordingOutputSink Output { get; } = new();
        public RecordingGamepad Pad { get; } = new();
        public ManualHoldScheduler Scheduler { get; } = new();
        public SettingsStore Settings { get; }
        public InputEngine Engine { get; }
        private double _time = 1.0;

        public Harness(bool paired = false, Func<InputSettings, InputSettings>? edit = null)
        {
            var settings = InputSettings.Default with { Mode = InputMode.Controller };
            Settings = new SettingsStore(edit?.Invoke(settings) ?? settings);
            Engine = new InputEngine(Output, Scheduler, Settings.Snapshot, gamepad: Pad);
            Engine.DeviceConnected(Right, JoyCon2Handedness.Right, Calibration);
            if (paired) Engine.DeviceConnected(Left, JoyCon2Handedness.Left, Calibration);
            Frame();
        }

        public void Frame(
            uint rightButtons = 0, short gyroZ = 0, ushort rightStickX = 2048, ushort rightStickY = 2048,
            uint leftButtons = 0, ushort leftStickX = 2048)
        {
            _time += 1.0 / 66.0;
            Engine.ProcessFrame(Right, Frames.Report(rightButtons, rightStickX, rightStickY, gyroZ: gyroZ), _time);
            Engine.ProcessFrame(Left, Frames.Report(leftButtons, leftStickX, 2048, handedness: JoyCon2Handedness.Left), _time);
        }

        public void Repeat(int count, uint rightButtons = 0, short gyroZ = 0, ushort rightStickX = 2048, ushort leftStickX = 2048)
        {
            for (var i = 0; i < count; i++) Frame(rightButtons, gyroZ, rightStickX, leftStickX: leftStickX);
        }
    }

    private static uint Bit(JoyCon2Button button) => Frames.Bit(button);

    [Fact]
    public void FaceButtonsMapByPosition()
    {
        var h = new Harness();
        h.Frame(Bit(JoyCon2Button.B));
        Assert.Equal(GamepadButtons.A, h.Pad.Last.Buttons); // the bottom button is Xbox A, at once

        h.Frame(Bit(JoyCon2Button.A) | Bit(JoyCon2Button.ZR));
        Assert.Equal(GamepadButtons.B | GamepadButtons.RightTrigger, h.Pad.Last.Buttons);
        Assert.Equal(1.0, h.Pad.Last.RightTrigger);

        h.Frame();
        Assert.Equal(GamepadButtons.None, h.Pad.Last.Buttons);
    }

    [Fact]
    public void ALoneJoyConsStickIsTheLeftStickAndItsGyroTheRight()
    {
        var h = new Harness();
        h.Repeat(10, rightStickX: 3348, gyroZ: 1500);

        Assert.True(h.Pad.Last.LeftX > 0.9);
        Assert.True(Math.Abs(h.Pad.Last.RightX) >= InputSettings.DefaultControllerStickDeadzone);
        Assert.Empty(h.Output.Looks);
    }

    [Fact]
    public void AStillControllerLeavesTheRightStickCentred()
    {
        var h = new Harness();
        h.Repeat(30);
        Assert.Equal(0, h.Pad.Last.RightX);
        Assert.Equal(0, h.Pad.Last.RightY);
    }

    [Fact]
    public void PairedEachStickIsItsOwnSide()
    {
        var h = new Harness(paired: true);
        h.Repeat(5, rightStickX: 3348, leftStickX: 748);

        Assert.True(h.Pad.Last.RightX > 0.9);
        Assert.True(h.Pad.Last.LeftX < -0.9);
    }

    [Fact]
    public void GyroWhileLeftTriggerAimsOnlyWithTheTriggerHeld()
    {
        var h = new Harness(edit: s => s with { ControllerGyroActivation = ControllerGyroActivation.WhileLeftTrigger });
        h.Repeat(10, gyroZ: 1500);
        Assert.Equal(0, h.Pad.Last.RightX);

        h.Repeat(10, Bit(JoyCon2Button.SL), gyroZ: 1500); // a lone right Joy-Con's SL is the left trigger
        Assert.NotEqual(0, h.Pad.Last.RightX);
    }

    [Fact]
    public void MouseGyroOutputLeavesTheStickToTheThumb()
    {
        var h = new Harness(edit: s => s with { ControllerGyroOutput = ControllerGyroOutput.Mouse });
        h.Repeat(10, gyroZ: 1500);

        Assert.Contains(h.Output.Looks, l => Math.Abs(l.Dx) > 0.5);
        Assert.Equal(0, h.Pad.Last.RightX);
    }

    [Fact]
    public void ATapIsHeldUntilTheGameCanSeeIt()
    {
        var h = new Harness(); // a lone right Joy-Con: tapping C is Back, holding it switches modes
        h.Frame(Bit(JoyCon2Button.C));
        h.Frame();
        Assert.Equal(GamepadButtons.Back, h.Pad.Last.Buttons);

        h.Scheduler.FireAll();
        Assert.Equal(GamepadButtons.None, h.Pad.Last.Buttons);
    }

    [Fact]
    public void LeavingTheModeUnplugsThePad()
    {
        var h = new Harness();
        h.Frame(Bit(JoyCon2Button.B));
        h.Settings.Update(s => s with { Mode = InputMode.Desktop });
        h.Frame(Bit(JoyCon2Button.B));

        Assert.Equal(1, h.Pad.Releases);
        Assert.Equal(GamepadReport.Neutral, h.Pad.Last);
        Assert.Equal(0, h.Engine.ActiveOutputCount);
    }

    [Fact]
    public void DisconnectingReleasesThatJoyConsButtons()
    {
        var h = new Harness(paired: true);
        h.Frame(leftButtons: Frames.Bit(JoyCon2Button.ZL, JoyCon2Handedness.Left));
        Assert.Equal(GamepadButtons.LeftTrigger, h.Pad.Last.Buttons);

        h.Engine.DeviceDisconnected(Left);
        Assert.Equal(GamepadButtons.None, h.Pad.Last.Buttons);
    }

    [Fact]
    public void NoDriverMeansNoReports()
    {
        var h = new Harness();
        h.Pad.IsAvailable = false;
        var before = h.Pad.Reports.Count;
        h.Frame(Bit(JoyCon2Button.B));
        Assert.Equal(before, h.Pad.Reports.Count);
    }
}

public class ModeCycleTests
{
    [Fact]
    public void HoldingTheModeButtonCyclesThroughAllThreeModes()
    {
        Assert.Equal(InputMode.Game, InputModes.Next(InputMode.Desktop, controllerAvailable: true));
        Assert.Equal(InputMode.Controller, InputModes.Next(InputMode.Game, controllerAvailable: true));
        Assert.Equal(InputMode.Desktop, InputModes.Next(InputMode.Controller, controllerAvailable: true));
    }

    [Fact]
    public void WithoutTheDriverXboxModeIsSkipped() =>
        Assert.Equal(InputMode.Desktop, InputModes.Next(InputMode.Game, controllerAvailable: false));
}

public class ButtonLayoutsTests
{
    [Theory]
    [InlineData(InputMode.Desktop, false)]
    [InlineData(InputMode.Game, false)]
    [InlineData(InputMode.Game, true)]
    [InlineData(InputMode.Controller, false)]
    [InlineData(InputMode.Controller, true)]
    public void EachLayoutIsReplacedIndependently(InputMode mode, bool paired)
    {
        foreach (var side in new[] { JoyCon2Handedness.Right, JoyCon2Handedness.Left })
        {
            var edited = ButtonLayouts.Default(mode, side, paired).WithActions(JoyCon2Button.SL, new ButtonActions(ButtonAction.Key(VirtualKeys.F)));
            var s = ButtonLayouts.With(InputSettings.Default, mode, side, paired, edited);

            Assert.Same(edited, ButtonLayouts.Get(s, mode, side, paired));
            Assert.Same(edited, (s with { Mode = mode }).ButtonsFor(side, paired));

            // Nothing else changed.
            var other = side == JoyCon2Handedness.Right ? JoyCon2Handedness.Left : JoyCon2Handedness.Right;
            Assert.Same(ButtonLayouts.Get(InputSettings.Default, mode, other, paired), ButtonLayouts.Get(s, mode, other, paired));
        }
    }
}
