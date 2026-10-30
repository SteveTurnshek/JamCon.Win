using System.Text.Json.Serialization;
using JamCon.Core.Protocol;

namespace JamCon.Core.Mapping;

/// <summary>
/// Button → action assignments for one controller side. Immutable, so the engine's per-frame
/// snapshot can never observe a half-edited profile; edits return a new instance.
/// </summary>
public sealed class ButtonMappingProfile
{
    public const double DefaultHoldThreshold = 0.3;
    public const double DefaultDoubleTapWindow = 0.3;

    private readonly ButtonActions[] _actions = new ButtonActions[JoyCon2ButtonLayout.ButtonCount];

    [JsonConstructor]
    public ButtonMappingProfile(
        IReadOnlyDictionary<JoyCon2Button, ButtonActions>? mappings,
        double holdThreshold = DefaultHoldThreshold,
        double doubleTapWindow = DefaultDoubleTapWindow)
    {
        Mappings = mappings is null
            ? new Dictionary<JoyCon2Button, ButtonActions>()
            : new Dictionary<JoyCon2Button, ButtonActions>(mappings);
        HoldThreshold = holdThreshold;
        DoubleTapWindow = doubleTapWindow;

        Array.Fill(_actions, ButtonActions.Empty);
        foreach (var (button, actions) in Mappings) _actions[(int)button] = actions;

        foreach (var actions in Mappings.Values)
        {
            switch (actions.Press)
            {
                case DragAction: HasDragMapping = true; break;
                case ScrollAction: HasScrollMapping = true; break;
                case RadialMenuAction: HasRadialMenuMapping = true; break;
            }
        }
    }

    public IReadOnlyDictionary<JoyCon2Button, ButtonActions> Mappings { get; }

    /// <summary>Seconds a button must be held before its hold action fires.</summary>
    public double HoldThreshold { get; }

    /// <summary>
    /// Seconds after a short press during which a second press counts as a double tap. Only buttons
    /// with a double-tap action wait for it.
    /// </summary>
    public double DoubleTapWindow { get; }

    [JsonIgnore]
    public bool HasDragMapping { get; }

    [JsonIgnore]
    public bool HasScrollMapping { get; }

    [JsonIgnore]
    public bool HasRadialMenuMapping { get; }

    public ButtonActions ActionsFor(JoyCon2Button button) => _actions[(int)button];

    public ButtonMappingProfile WithActions(JoyCon2Button button, ButtonActions actions)
    {
        var copy = new Dictionary<JoyCon2Button, ButtonActions>(Mappings) { [button] = actions };
        return new ButtonMappingProfile(copy, HoldThreshold, DoubleTapWindow);
    }

    /// <summary>Setting a gyro mode as the press clears the hold, as upstream does.</summary>
    public ButtonMappingProfile WithPress(JoyCon2Button button, ButtonAction press)
    {
        var current = ActionsFor(button);
        return WithActions(button, new ButtonActions(press, press.IsGyroMode ? ButtonAction.None : current.Hold));
    }

    public ButtonMappingProfile WithHold(JoyCon2Button button, ButtonAction hold) =>
        WithActions(button, ActionsFor(button) with { Hold = hold });

    public ButtonMappingProfile WithHoldThreshold(double seconds) => new(Mappings, seconds, DoubleTapWindow);

    /// <summary>
    /// Windows defaults, re-derived from upstream's production one-handed layout.
    /// </summary>
    /// <remarks>
    /// Kept as upstream: triggers and shoulders click (ZR/ZL left, R/L right), Plus/Minus middle
    /// click, X/Up is the drag clutch, B/Down holds the radial menu, A/Left is Escape (hold Ctrl+C),
    /// the side rails are arrow keys, and C is unmapped.
    ///
    /// Changed for Windows: HOME/Capture opens Task View instead of Mission Control (hold:
    /// Play/Pause); stick click is the Browser Back key instead of ⌘[ (hold: Browser Forward
    /// instead of ⌘]).
    ///
    /// Y/Right is the dictation button. Upstream had ^Space with Enter on hold; Steve's dictation
    /// app (Handy) uses Ctrl+Space in toggle mode, so: hold to dictate (Ctrl+Space as the hold
    /// starts and again as it ends), double tap for Enter, and tap-then-hold to delete a word every
    /// half second (Ctrl+Backspace). A single tap does nothing.
    /// </remarks>
    public static ButtonMappingProfile DefaultFor(JoyCon2Handedness handedness)
    {
        var escapeOrCopy = new ButtonActions(
            ButtonAction.Key(VirtualKeys.Escape),
            ButtonAction.Key(VirtualKeys.C, KeyModifiers.Control));
        var dictateOrEnter = new ButtonActions(
            press: ButtonAction.None,
            hold: ButtonAction.HoldToggle(VirtualKeys.Space, KeyModifiers.Control),
            doubleTap: ButtonAction.Key(VirtualKeys.Return),
            doubleTapHold: ButtonAction.Repeat(VirtualKeys.Back, KeyModifiers.Control, 0.5));
        var taskView = new ButtonActions(
            ButtonAction.System(SystemAction.TaskView),
            ButtonAction.System(SystemAction.PlayPause));
        var backForward = new ButtonActions(
            ButtonAction.Key(VirtualKeys.BrowserBack),
            ButtonAction.Key(VirtualKeys.BrowserForward));

        var mappings = handedness == JoyCon2Handedness.Right
            ? new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.A] = escapeOrCopy,
                [JoyCon2Button.X] = new(ButtonAction.Drag),
                [JoyCon2Button.B] = new(ButtonAction.RadialMenu),
                [JoyCon2Button.Y] = dictateOrEnter,
                [JoyCon2Button.R] = new(ButtonAction.Click(MouseButton.Right)),
                [JoyCon2Button.ZR] = new(ButtonAction.Click(MouseButton.Left)),
                [JoyCon2Button.Plus] = new(ButtonAction.Click(MouseButton.Middle)),
                [JoyCon2Button.Home] = taskView,
                [JoyCon2Button.StickClick] = backForward,
                [JoyCon2Button.SL] = new(ButtonAction.Key(VirtualKeys.Down)),
                [JoyCon2Button.SR] = new(ButtonAction.Key(VirtualKeys.Up)),
            }
            : new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.DpadUp] = new(ButtonAction.Drag),
                [JoyCon2Button.DpadDown] = new(ButtonAction.RadialMenu),
                [JoyCon2Button.DpadLeft] = escapeOrCopy,
                [JoyCon2Button.DpadRight] = dictateOrEnter,
                [JoyCon2Button.L] = new(ButtonAction.Click(MouseButton.Right)),
                [JoyCon2Button.ZL] = new(ButtonAction.Click(MouseButton.Left)),
                [JoyCon2Button.Minus] = new(ButtonAction.Click(MouseButton.Middle)),
                [JoyCon2Button.Capture] = taskView,
                [JoyCon2Button.StickClick] = backForward,
                [JoyCon2Button.SL] = new(ButtonAction.Key(VirtualKeys.Up)),
                [JoyCon2Button.SR] = new(ButtonAction.Key(VirtualKeys.Down)),
            };

        return new ButtonMappingProfile(mappings);
    }
}
