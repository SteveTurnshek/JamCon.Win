using System.Text.Json;
using System.Text.Json.Serialization;
using JamCon.Core.Engine;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Game mode: gyro always aims as relative motion, the stick holds WASD, keys follow their buttons,
/// and holding C switches modes without leaving anything pressed.
/// </summary>
public class GameModeTests
{
    private const string Device = "jc";
    private static readonly KeyCombo W = new(VirtualKeys.W);
    private static readonly KeyCombo Space = new(VirtualKeys.Space);

    private sealed class Harness
    {
        public RecordingOutputSink Output { get; } = new();
        public ManualHoldScheduler Scheduler { get; } = new();
        public SettingsStore Settings { get; }
        public InputEngine Engine { get; }
        public int ToggleRequests { get; private set; }
        private double _time = 1.0;

        public Harness(InputMode mode)
        {
            Settings = new SettingsStore(InputSettings.Default with { Mode = mode });
            Engine = new InputEngine(Output, Scheduler, Settings.Snapshot);
            Engine.ModeToggleRequested += () => ToggleRequests++;
            Engine.DeviceConnected(Device, JoyCon2Handedness.Right, new JoyCon2StickCalibration(2048, 2048, 1300, 1300, 1300, 1300));
            Frame(); // prime
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

        public int Downs(KeyCombo combo) => Output.Events.Count(e => e == RecordingOutputSink.Down(combo));
        public int Ups(KeyCombo combo) => Output.Events.Count(e => e == RecordingOutputSink.Up(combo));
    }

    private static uint Bit(JoyCon2Button button) => Frames.Bit(button);

    [Fact]
    public void GyroAimsWithoutAClutchAndNeverPositionsTheCursor()
    {
        var h = new Harness(InputMode.Game);
        h.Output.Looks.Clear(); // the priming frame's
        h.Repeat(30, gyroZ: 1000);

        Assert.Empty(h.Output.Moves);
        Assert.Equal(30, h.Output.Looks.Count);
        Assert.Contains(h.Output.Looks, l => Math.Abs(l.Dx) > 0.5);
    }

    [Fact]
    public void GameAimIsLinear()
    {
        // No acceleration: twice the rotation speed gives twice the motion.
        static double Total(short gyroZ)
        {
            var h = new Harness(InputMode.Game);
            h.Repeat(60, gyroZ: gyroZ);
            return h.Output.Looks.Skip(30).Sum(l => Math.Abs(l.Dx));
        }

        Assert.Equal(2.0, Total(2000) / Total(1000), 2);
        Assert.Equal(1.0, GyroSettings.GameDefault.SensitivityCap);
    }

    [Fact]
    public void StickHoldsWUntilItReturnsPastTheReleaseThreshold()
    {
        var h = new Harness(InputMode.Game);
        h.Repeat(5, stickY: 2048 + 1000); // well past the press threshold, up
        Assert.Equal(1, h.Downs(W));
        Assert.Equal(0, h.Ups(W));

        h.Repeat(5, stickY: 2048 + 560); // between the thresholds: still held
        Assert.Equal(0, h.Ups(W));

        h.Repeat(5); // centered
        Assert.Equal(1, h.Ups(W));
        Assert.Empty(h.Output.Scrolls);
        Assert.Equal(0, h.Engine.ActiveOutputCount);
    }

    [Fact]
    public void GameKeysFollowTheirButton()
    {
        var h = new Harness(InputMode.Game);
        h.Frame(Bit(JoyCon2Button.A));
        Assert.Equal([RecordingOutputSink.Down(Space)], h.Output.Events); // down at once, not on release

        h.Repeat(20, Bit(JoyCon2Button.A));
        h.Scheduler.FireAll();
        Assert.Single(h.Output.Events); // held, not repeated

        h.Frame();
        Assert.Equal([RecordingOutputSink.Down(Space), RecordingOutputSink.Up(Space)], h.Output.Events);
    }

    [Fact]
    public void HoldingCRequestsAModeSwitchAndATapDoesNot()
    {
        var h = new Harness(InputMode.Desktop);
        h.Frame(Bit(JoyCon2Button.C));
        h.Frame();
        h.Scheduler.FireAll();
        Assert.Equal(0, h.ToggleRequests);

        h.Frame(Bit(JoyCon2Button.C));
        h.Scheduler.FireAll();
        Assert.Equal(1, h.ToggleRequests);
        Assert.Empty(h.Output.Events);
    }

    [Fact]
    public void SwitchingModesReleasesHeldKeysAndTheStillHeldButtonDoesNotFireAgain()
    {
        var h = new Harness(InputMode.Game);
        h.Repeat(3, Bit(JoyCon2Button.C), stickY: 3048);
        h.Scheduler.FireAll(); // C held: switch requested
        Assert.Equal(1, h.ToggleRequests);
        Assert.Equal(1, h.Downs(W));

        h.Settings.Update(s => s with { Mode = InputMode.Desktop }); // what the host does
        h.Repeat(3, Bit(JoyCon2Button.C), stickY: 3048);
        Assert.Equal(1, h.Ups(W));

        h.Frame();
        h.Scheduler.FireAll();
        Assert.Equal(1, h.ToggleRequests);
        Assert.Equal(0, h.Engine.ActiveOutputCount);
    }

    [Fact]
    public void DesktopModeIsUnchanged()
    {
        var h = new Harness(InputMode.Desktop);
        h.Repeat(10, gyroZ: 1000, stickY: 3400);

        Assert.Empty(h.Output.Looks);
        Assert.Empty(h.Output.Moves); // X clutch not held
        Assert.NotEmpty(h.Output.Scrolls);
        Assert.Equal(0, h.Downs(W));
    }

    [Fact]
    public void ModeAndGameSettingsRoundTripAndOldFilesLoadInDesktopMode()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var settings = InputSettings.Default with
        {
            Mode = InputMode.Game,
            GameGyro = GyroSettings.GameDefault with { Sensitivity = 333 },
        };

        var json = JsonSerializer.Serialize(settings, options);
        Assert.DoesNotContain("ActiveGyro", json);
        var restored = JsonSerializer.Deserialize<InputSettings>(json, options)!;
        Assert.Equal(InputMode.Game, restored.Mode);
        Assert.Equal(333, restored.ActiveGyro.Sensitivity);
        Assert.Equal(VirtualKeys.Space, Assert.IsType<KeyPressAction>(restored.ButtonsFor(JoyCon2Handedness.Right).ActionsFor(JoyCon2Button.A).Press).Combo.VirtualKey);

        var old = JsonSerializer.Deserialize<InputSettings>("""{"IsEnabled":true}""", options)!;
        Assert.Equal(InputMode.Desktop, old.Mode);
        Assert.Equal(GyroSettings.GameDefault, old.GameGyro);
    }
}

public class SmoothingLevelTests
{
    [Fact]
    public void DefaultsSitOnTheScale()
    {
        Assert.Equal(5, SmoothingLevel.Of(GyroSettings.Default));
        Assert.Equal(3, SmoothingLevel.Of(GyroSettings.GameDefault));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void EveryLevelRoundTrips(int level) =>
        Assert.Equal(level, SmoothingLevel.Of(SmoothingLevel.Apply(GyroSettings.Default, level)));

    [Fact]
    public void HigherLevelsSmoothMore() =>
        Assert.True(SmoothingLevel.CutoffFor(8) < SmoothingLevel.CutoffFor(2));
}
