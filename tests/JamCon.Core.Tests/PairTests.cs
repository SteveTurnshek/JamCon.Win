using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Both Joy-Cons at once: one controller, aiming with the right. In game mode, laid out like a
/// Switch 2 motion-controls game.
/// </summary>
public class PairTests
{
    private const string Right = "right";
    private const string Left = "left";
    private static readonly KeyCombo W = new(VirtualKeys.W);

    private sealed class Harness
    {
        public RecordingOutputSink Output { get; } = new();
        public ManualHoldScheduler Scheduler { get; } = new();
        public SettingsStore Settings { get; }
        public InputEngine Engine { get; }
        private double _time = 1.0;

        public Harness(InputMode mode)
        {
            Settings = new SettingsStore(InputSettings.Default with { Mode = mode });
            Engine = new InputEngine(Output, Scheduler, Settings.Snapshot);
            var calibration = new JoyCon2StickCalibration(2048, 2048, 1300, 1300, 1300, 1300);
            Engine.DeviceConnected(Right, JoyCon2Handedness.Right, calibration);
            Engine.DeviceConnected(Left, JoyCon2Handedness.Left, calibration);
            Both(); // prime
            Output.Looks.Clear();
            Output.Moves.Clear();
        }

        /// <summary>One report from each side, as the two links deliver them.</summary>
        public void Both(
            uint rightButtons = 0, uint leftButtons = 0,
            short rightGyroX = 0, short rightGyroZ = 0, short leftGyroZ = 0,
            ushort rightStickX = 2048, ushort leftStickY = 2048)
        {
            _time += 1.0 / 66.0;
            Engine.ProcessFrame(Right, Frames.Report(rightButtons, rightStickX, 2048, rightGyroX, 0, rightGyroZ), _time);
            Engine.ProcessFrame(Left, Frames.Report(leftButtons, 2048, leftStickY, 0, 0, leftGyroZ, JoyCon2Handedness.Left), _time);
        }

        public void Repeat(int count, Action frame)
        {
            for (var i = 0; i < count; i++) frame();
        }
    }

    private static uint RightBit(JoyCon2Button button) => Frames.Bit(button);
    private static uint LeftBit(JoyCon2Button button) => Frames.Bit(button, JoyCon2Handedness.Left);

    [Fact]
    public void OnlyTheRightJoyConAims()
    {
        var h = new Harness(InputMode.Game);
        h.Repeat(20, () => h.Both(leftGyroZ: 2000));
        Assert.All(h.Output.Looks, l => Assert.Equal(0, l.Dx, 6));

        h.Repeat(20, () => h.Both(rightGyroZ: 2000));
        Assert.Contains(h.Output.Looks, l => Math.Abs(l.Dx) > 0.5);
    }

    [Fact]
    public void LeftStickWalksAndRightStickTurns()
    {
        var h = new Harness(InputMode.Game);
        h.Repeat(5, () => h.Both(leftStickY: 3348, rightStickX: 3348));

        Assert.Contains(RecordingOutputSink.Down(W), h.Output.Events);
        Assert.Equal(1, h.Output.Events.Count(e => e.IsPressed)); // the right stick pressed no keys
        Assert.Contains(h.Output.Looks, l => l.Dx > 0.5 && l.Dy == 0); // stick right turns right
    }

    [Fact]
    public void YLevelsTheViewBySendingBackTheVerticalAim()
    {
        var h = new Harness(InputMode.Game);
        h.Repeat(30, () => h.Both(rightGyroX: 1500));
        var pitch = h.Output.Looks.Sum(l => l.Dy);
        Assert.True(Math.Abs(pitch) > 5);

        h.Both(rightButtons: RightBit(JoyCon2Button.Y));
        h.Both();

        // Back to level, give or take the filter's last trickle of motion after the turn stopped.
        Assert.Contains(h.Output.Looks, l => l.Dx == 0 && l.Dy < -0.9 * pitch);
        Assert.True(Math.Abs(h.Output.Looks.Sum(l => l.Dy)) < 0.01 * Math.Abs(pitch));
    }

    [Fact]
    public void PairUsesThePairedGameLayout()
    {
        var h = new Harness(InputMode.Game);
        h.Both(rightButtons: RightBit(JoyCon2Button.B), leftButtons: LeftBit(JoyCon2Button.ZL));

        Assert.Contains(RecordingOutputSink.Down(new KeyCombo(VirtualKeys.Space)), h.Output.Events);
        Assert.Contains(RecordingOutputSink.Down(new KeyCombo(VirtualKeys.LeftControl)), h.Output.Events);
    }

    [Fact]
    public void InDesktopModeTheLeftHandsClutchMovesTheCursorWithTheRightGyro()
    {
        var h = new Harness(InputMode.Desktop);
        h.Repeat(20, () => h.Both(rightGyroZ: 2000));
        Assert.Empty(h.Output.Moves); // no clutch held

        h.Repeat(20, () => h.Both(leftButtons: LeftBit(JoyCon2Button.DpadUp), rightGyroZ: 2000, leftGyroZ: -2000));
        // The right's motion only, once per frame. The first frame misses: the right's report is
        // handled before the left's report that presses the clutch.
        Assert.Equal(19, h.Output.Moves.Count);

        // The left's opposite motion changed nothing: identical to a run with the left held still.
        var still = new Harness(InputMode.Desktop);
        still.Repeat(20, () => still.Both(rightGyroZ: 2000));
        still.Repeat(20, () => still.Both(leftButtons: LeftBit(JoyCon2Button.DpadUp), rightGyroZ: 2000));
        Assert.Equal(still.Output.Moves, h.Output.Moves);
    }

    [Fact]
    public void WhenTheLeftDisconnectsTheRightWorksAloneAgain()
    {
        var h = new Harness(InputMode.Game);
        h.Engine.DeviceDisconnected(Left);
        h.Repeat(5, () => h.Engine.ProcessFrame(Right, Frames.Report(0, 2048, 3348), 10.0 + h.Output.Events.Count));

        Assert.Contains(RecordingOutputSink.Down(W), h.Output.Events); // its own stick walks again
    }
}
