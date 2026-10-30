using System.Text.Json;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// The Y button's dictation gestures: hold to talk with a toggle-mode dictation app, double tap for
/// Enter, tap-then-hold to delete words.
/// </summary>
public class GestureTests
{
    private static readonly KeyCombo CtrlSpace = new(VirtualKeys.Space, KeyModifiers.Control);
    private static readonly KeyCombo CtrlBackspace = new(VirtualKeys.Back, KeyModifiers.Control);
    private static readonly KeyCombo Enter = new(VirtualKeys.Return);

    private sealed class Harness
    {
        public RecordingOutputSink Output { get; } = new();
        public ManualHoldScheduler Scheduler { get; } = new();
        public SettingsStore Settings { get; } = new();
        public InputEngine Engine { get; }
        private double _t = 1.0;

        public Harness()
        {
            Engine = new InputEngine(Output, Scheduler, Settings.Snapshot);
            Engine.DeviceConnected("jc", JoyCon2Handedness.Right, null);
            Frame(false); // prime
        }

        public void Frame(bool yDown) =>
            Engine.ProcessFrame("jc", Frames.Report(yDown ? Frames.Bit(JoyCon2Button.Y) : 0), _t += 0.015);

        public int Taps(KeyCombo combo) => Output.Events.Count(e => e == RecordingOutputSink.Down(combo));
    }

    [Fact]
    public void HoldingYTogglesDictationOnAtTheStartAndOffAtTheEnd()
    {
        var h = new Harness();
        h.Frame(true);
        Assert.Empty(h.Output.Events);   // nothing until it is known to be a hold

        h.Scheduler.FireAll();           // hold threshold reached: dictation starts
        Assert.Equal([RecordingOutputSink.Down(CtrlSpace), RecordingOutputSink.Up(CtrlSpace)], h.Output.Events);

        h.Frame(false);                  // release: dictation stops
        Assert.Equal(
            [
                RecordingOutputSink.Down(CtrlSpace), RecordingOutputSink.Up(CtrlSpace),
                RecordingOutputSink.Down(CtrlSpace), RecordingOutputSink.Up(CtrlSpace),
            ],
            h.Output.Events);
    }

    [Fact]
    public void DoubleTapSendsEnterAndNothingElse()
    {
        var h = new Harness();
        h.Frame(true);
        h.Frame(false);
        h.Frame(true);
        h.Frame(false);
        h.Scheduler.FireAll(); // any stale timers must not fire anything now

        Assert.Equal([RecordingOutputSink.Down(Enter), RecordingOutputSink.Up(Enter)], h.Output.Events);
    }

    [Fact]
    public void TapThenHoldDeletesAWordAtOnceThenEveryIntervalUntilRelease()
    {
        var h = new Harness();
        h.Frame(true);
        h.Frame(false);
        h.Frame(true);
        Assert.Empty(h.Output.Events);

        h.Scheduler.FireAll(); // second press becomes a hold: first delete
        Assert.Equal(1, h.Taps(CtrlBackspace));

        h.Scheduler.FireAll(); // one interval later
        h.Scheduler.FireAll(); // and another
        Assert.Equal(3, h.Taps(CtrlBackspace));

        h.Frame(false);
        h.Scheduler.FireAll(); // nothing more after release
        Assert.Equal(3, h.Taps(CtrlBackspace));
        Assert.Equal(0, h.Taps(Enter));
        Assert.Equal(0, h.Taps(CtrlSpace));
        Assert.Equal(0, h.Engine.ActiveOutputCount);
    }

    [Fact]
    public void DeleteRepeatsEveryHalfSecond()
    {
        var y = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right).ActionsFor(JoyCon2Button.Y);
        var repeat = Assert.IsType<RepeatKeyAction>(y.DoubleTapHold);
        Assert.Equal(CtrlBackspace, repeat.Combo);
        Assert.Equal(0.5, repeat.IntervalSeconds);
    }

    [Fact]
    public void SingleTapDoesNothing()
    {
        var h = new Harness();
        h.Frame(true);
        h.Frame(false);
        h.Scheduler.FireAll(); // double-tap window expires

        Assert.Empty(h.Output.Events);
    }

    [Fact]
    public void DisconnectMidDictationSendsTheClosingToggle()
    {
        var h = new Harness();
        h.Frame(true);
        h.Scheduler.FireAll();
        h.Engine.DeviceDisconnected("jc");

        Assert.Equal(2, h.Taps(CtrlSpace)); // on, then off: the dictation app is not left recording
    }

    [Fact]
    public void DisablingOutputMidDeleteStopsTheRepeat()
    {
        var h = new Harness();
        h.Frame(true);
        h.Frame(false);
        h.Frame(true);
        h.Scheduler.FireAll();
        h.Engine.SetInputEnabled(false);
        h.Scheduler.FireAll();
        h.Scheduler.FireAll();

        Assert.Equal(1, h.Taps(CtrlBackspace));
    }

    [Fact]
    public void ButtonsWithoutDoubleTapActionsStillActOnRelease()
    {
        // ZR has no double-tap actions, so it must not wait out a double-tap window.
        var h = new Harness();
        h.Engine.ProcessFrame("jc", Frames.Report(Frames.Bit(JoyCon2Button.ZR)), 2.0);
        h.Engine.ProcessFrame("jc", Frames.Report(), 2.015);

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left)], h.Output.Events);
    }

    [Fact]
    public void NewActionsRoundTripAndOldProfilesStillLoad()
    {
        var profile = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);
        var restored = JsonSerializer.Deserialize<ButtonMappingProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(profile.ActionsFor(JoyCon2Button.Y), restored.ActionsFor(JoyCon2Button.Y));

        // Saved before double taps existed, when timing was per layout: no DoubleTap fields, and a
        // HoldThreshold that is now ignored.
        const string old = """{"Mappings":{"A":{"Press":{"type":"keyPress","Combo":{"VirtualKey":27,"Modifiers":0}},"Hold":{"type":"none"}}},"HoldThreshold":0.3}""";
        var legacy = JsonSerializer.Deserialize<ButtonMappingProfile>(old)!;
        Assert.Equal(ButtonAction.None, legacy.ActionsFor(JoyCon2Button.A).DoubleTap);
        Assert.False(legacy.ActionsFor(JoyCon2Button.A).UsesDoubleTap);
    }

    [Fact]
    public void TimingIsGlobalAndAdjustable()
    {
        var h = new Harness();
        h.Settings.Update(s => s with { HoldThreshold = 0.8 });
        Assert.Equal(0.8, h.Settings.Snapshot().HoldThreshold);

        h.Frame(true);
        Assert.Equal(TimeSpan.FromSeconds(0.8), Assert.Single(h.Scheduler.PendingDelays));
    }
}
