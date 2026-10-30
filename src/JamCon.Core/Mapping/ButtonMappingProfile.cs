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
    /// and the side rails are arrow keys.
    ///
    /// Changed for Windows: HOME/Capture opens Task View instead of Mission Control (hold:
    /// Play/Pause); stick click is the Browser Back key instead of ⌘[ (hold: Browser Forward
    /// instead of ⌘]).
    ///
    /// Y/Right is the dictation button. Upstream had ^Space with Enter on hold; Steve's dictation
    /// app (Handy) uses Ctrl+Space in toggle mode, so: hold to dictate (Ctrl+Space as the hold
    /// starts and again as it ends), double tap for Enter, and tap-then-hold to delete a word every
    /// half second (Ctrl+Backspace). A single tap does nothing.
    ///
    /// Holding C (left Joy-Con: Capture, which has no C) switches to game mode; see
    /// <see cref="GameDefaultFor"/>. On the left this replaces Capture's hold, Play/Pause.
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
                [JoyCon2Button.C] = SwitchMode,
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
                [JoyCon2Button.Capture] = new(ButtonAction.System(SystemAction.TaskView), ButtonAction.ToggleMode),
                [JoyCon2Button.StickClick] = backForward,
                [JoyCon2Button.SL] = new(ButtonAction.Key(VirtualKeys.Up)),
                [JoyCon2Button.SR] = new(ButtonAction.Key(VirtualKeys.Down)),
            };

        return new ButtonMappingProfile(mappings);
    }

    /// <summary>
    /// Game mode with both Joy-Cons, laid out like a Switch 2 game with motion controls (Splatoon):
    /// the left stick walks, the right Joy-Con's gyro aims and its stick turns, Y levels the view.
    /// </summary>
    /// <remarks>
    /// The side-rail buttons are left unmapped: they sit against the palms in this grip. HOME is
    /// unmapped as everywhere in game mode.
    /// </remarks>
    public static ButtonMappingProfile GamePairDefaultFor(JoyCon2Handedness handedness)
    {
        ButtonActions Key(ushort virtualKey) => new(ButtonAction.Key(virtualKey));

        var mappings = handedness == JoyCon2Handedness.Right
            ? new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.ZR] = new(ButtonAction.Click(MouseButton.Left)),
                [JoyCon2Button.R] = new(ButtonAction.Click(MouseButton.Right)),
                [JoyCon2Button.B] = Key(VirtualKeys.Space),
                [JoyCon2Button.A] = Key(VirtualKeys.E),
                [JoyCon2Button.X] = Key(VirtualKeys.R),
                [JoyCon2Button.Y] = new(ButtonAction.LevelView),
                [JoyCon2Button.StickClick] = Key(VirtualKeys.V),
                [JoyCon2Button.Plus] = Key(VirtualKeys.Escape),
                [JoyCon2Button.C] = SwitchMode,
            }
            : new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.ZL] = Key(VirtualKeys.LeftControl),
                [JoyCon2Button.L] = Key(VirtualKeys.Q),
                [JoyCon2Button.StickClick] = Key(VirtualKeys.LeftShift),
                [JoyCon2Button.DpadUp] = Key(VirtualKeys.Digit1),
                [JoyCon2Button.DpadRight] = Key(VirtualKeys.Digit2),
                [JoyCon2Button.DpadDown] = Key(VirtualKeys.Digit3),
                [JoyCon2Button.DpadLeft] = Key(VirtualKeys.Digit4),
                [JoyCon2Button.Minus] = Key(VirtualKeys.Tab),
                [JoyCon2Button.Capture] = SwitchMode,
            };

        return new ButtonMappingProfile(mappings);
    }

    /// <summary>Hold to switch between desktop and game mode; a tap does nothing.</summary>
    private static readonly ButtonActions SwitchMode = new(ButtonAction.None, ButtonAction.ToggleMode);

    /// <summary>
    /// Game mode defaults: gyro aims (always on, no clutch), the stick moves with WASD, and the
    /// buttons are common PC shooter keys. Every key is held for as long as its button is.
    /// </summary>
    /// <remarks>
    /// HOME is left unmapped: it wakes a nearby Switch 2. The same button as on the desktop switches
    /// back (C, or Capture on the left).
    /// </remarks>
    public static ButtonMappingProfile GameDefaultFor(JoyCon2Handedness handedness)
    {
        ButtonActions Key(ushort virtualKey) => new(ButtonAction.Key(virtualKey));

        var mappings = handedness == JoyCon2Handedness.Right
            ? new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.ZR] = new(ButtonAction.Click(MouseButton.Left)),
                [JoyCon2Button.R] = new(ButtonAction.Click(MouseButton.Right)),
                [JoyCon2Button.A] = Key(VirtualKeys.Space),
                [JoyCon2Button.B] = Key(VirtualKeys.LeftControl),
                [JoyCon2Button.X] = Key(VirtualKeys.R),
                [JoyCon2Button.Y] = Key(VirtualKeys.E),
                [JoyCon2Button.StickClick] = Key(VirtualKeys.LeftShift),
                [JoyCon2Button.Plus] = Key(VirtualKeys.Escape),
                [JoyCon2Button.SL] = Key(VirtualKeys.Q),
                [JoyCon2Button.SR] = Key(VirtualKeys.F),
                [JoyCon2Button.C] = SwitchMode,
            }
            : new Dictionary<JoyCon2Button, ButtonActions>
            {
                [JoyCon2Button.ZL] = new(ButtonAction.Click(MouseButton.Left)),
                [JoyCon2Button.L] = new(ButtonAction.Click(MouseButton.Right)),
                [JoyCon2Button.DpadRight] = Key(VirtualKeys.Space),
                [JoyCon2Button.DpadDown] = Key(VirtualKeys.LeftControl),
                [JoyCon2Button.DpadUp] = Key(VirtualKeys.R),
                [JoyCon2Button.DpadLeft] = Key(VirtualKeys.E),
                [JoyCon2Button.StickClick] = Key(VirtualKeys.LeftShift),
                [JoyCon2Button.Minus] = Key(VirtualKeys.Escape),
                [JoyCon2Button.SL] = Key(VirtualKeys.Q),
                [JoyCon2Button.SR] = Key(VirtualKeys.F),
                [JoyCon2Button.Capture] = SwitchMode,
            };

        return new ButtonMappingProfile(mappings);
    }
}
