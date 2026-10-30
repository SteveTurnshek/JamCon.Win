using System.Text.Json.Serialization;

namespace JamCon.Core.Mapping;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// Windows virtual-key codes used by defaults and system actions. Plain numbers, so Core stays free
/// of Win32; the output layer turns them into scan codes.
/// </summary>
public static class VirtualKeys
{
    public const ushort Back = 0x08;
    public const ushort Tab = 0x09;
    public const ushort Return = 0x0D;
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;
    public const ushort Digit1 = 0x31;
    public const ushort Digit2 = 0x32;
    public const ushort Digit3 = 0x33;
    public const ushort Digit4 = 0x34;
    public const ushort Left = 0x25;
    public const ushort Up = 0x26;
    public const ushort Right = 0x27;
    public const ushort Down = 0x28;
    public const ushort A = 0x41;
    public const ushort C = 0x43;
    public const ushort D = 0x44;
    public const ushort E = 0x45;
    public const ushort F = 0x46;
    public const ushort H = 0x48;
    public const ushort Q = 0x51;
    public const ushort R = 0x52;
    public const ushort S = 0x53;
    public const ushort V = 0x56;
    public const ushort W = 0x57;
    public const ushort LeftShift = 0xA0;
    public const ushort LeftControl = 0xA2;
    public const ushort BrowserBack = 0xA6;
    public const ushort BrowserForward = 0xA7;
    public const ushort MediaPlayPause = 0xB3;

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [Back] = "Backspace", [Tab] = "Tab", [Return] = "Enter", [Escape] = "Esc", [Space] = "Space",
        [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End", [0x24] = "Home",
        [Left] = "←", [Up] = "↑", [Right] = "→", [Down] = "↓", [0x2E] = "Delete",
        [LeftShift] = "Shift", [LeftControl] = "Ctrl",
        [BrowserBack] = "Browser Back", [BrowserForward] = "Browser Forward",
        [0xAD] = "Mute", [0xAE] = "Volume Down", [0xAF] = "Volume Up",
        [0xB0] = "Next Track", [0xB1] = "Previous Track", [MediaPlayPause] = "Play/Pause",
    };

    public static string DisplayName(ushort virtualKey)
    {
        if (Names.TryGetValue(virtualKey, out var name)) return name;
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x6F}";
        return $"Key 0x{virtualKey:X2}";
    }
}

/// <summary>A key plus modifiers, as a Windows virtual-key code.</summary>
public readonly record struct KeyCombo(ushort VirtualKey, KeyModifiers Modifiers = KeyModifiers.None)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKeys.DisplayName(VirtualKey));
        return string.Join('+', parts);
    }
}

/// <summary>
/// Windows equivalents of upstream's macOS system actions. Re-derived rather than transliterated:
/// Mission Control becomes Task View, Launchpad becomes Start, and so on.
/// </summary>
public enum SystemAction
{
    /// <summary>Win+Tab. Replaces Mission Control.</summary>
    TaskView,

    /// <summary>The Windows key alone. Replaces Launchpad.</summary>
    StartMenu,

    /// <summary>Win+D.</summary>
    ShowDesktop,

    /// <summary>Ctrl+Alt+Tab, which stays open without a held key. Replaces ⌘Tab.</summary>
    AppSwitcher,

    /// <summary>Win+H: Windows voice typing. Replaces the ^Space dictation shortcut upstream used.</summary>
    Dictation,

    /// <summary>The media Play/Pause key.</summary>
    PlayPause,
}

/// <summary>What a button does. Gyro modes change what gyro motion does while the button is held.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoAction), "none")]
[JsonDerivedType(typeof(MouseClickAction), "mouseClick")]
[JsonDerivedType(typeof(KeyPressAction), "keyPress")]
[JsonDerivedType(typeof(SystemShortcutAction), "systemAction")]
[JsonDerivedType(typeof(DragAction), "drag")]
[JsonDerivedType(typeof(ScrollAction), "scroll")]
[JsonDerivedType(typeof(RadialMenuAction), "radialMenu")]
[JsonDerivedType(typeof(HoldToggleAction), "holdToggle")]
[JsonDerivedType(typeof(RepeatKeyAction), "repeatKey")]
[JsonDerivedType(typeof(ToggleModeAction), "toggleMode")]
[JsonDerivedType(typeof(LevelViewAction), "levelView")]
public abstract record ButtonAction
{
    public static ButtonAction None { get; } = new NoAction();
    public static ButtonAction Drag { get; } = new DragAction();
    public static ButtonAction Scroll { get; } = new ScrollAction();
    public static ButtonAction RadialMenu { get; } = new RadialMenuAction();
    public static ButtonAction ToggleMode { get; } = new ToggleModeAction();
    public static ButtonAction LevelView { get; } = new LevelViewAction();

    public static ButtonAction Click(MouseButton button) => new MouseClickAction(button);
    public static ButtonAction Key(ushort virtualKey, KeyModifiers modifiers = KeyModifiers.None) =>
        new KeyPressAction(new KeyCombo(virtualKey, modifiers));
    public static ButtonAction System(SystemAction action) => new SystemShortcutAction(action);
    public static ButtonAction HoldToggle(ushort virtualKey, KeyModifiers modifiers = KeyModifiers.None) =>
        new HoldToggleAction(new KeyCombo(virtualKey, modifiers));
    public static ButtonAction Repeat(ushort virtualKey, KeyModifiers modifiers, double intervalSeconds) =>
        new RepeatKeyAction(new KeyCombo(virtualKey, modifiers), intervalSeconds);

    /// <summary>Drag, scroll and radial menu: modes held for the button's duration, never taps.</summary>
    [JsonIgnore]
    public virtual bool IsGyroMode => false;

    [JsonIgnore]
    public abstract string DisplayName { get; }
}

public sealed record NoAction : ButtonAction
{
    public override string DisplayName => "None";
}

public sealed record MouseClickAction(MouseButton Button) : ButtonAction
{
    public override string DisplayName => $"{Button} Click";
}

public sealed record KeyPressAction(KeyCombo Combo) : ButtonAction
{
    public override string DisplayName => Combo.ToString();
}

public sealed record SystemShortcutAction(SystemAction Action) : ButtonAction
{
    public override string DisplayName => Action switch
    {
        SystemAction.TaskView => "Task View",
        SystemAction.StartMenu => "Start Menu",
        SystemAction.ShowDesktop => "Show Desktop",
        SystemAction.AppSwitcher => "App Switcher",
        SystemAction.Dictation => "Dictation",
        _ => "Play/Pause",
    };
}

/// <summary>Gyro moves the cursor only while held. If any button has this, gyro is otherwise idle.</summary>
public sealed record DragAction : ButtonAction
{
    public override bool IsGyroMode => true;
    public override string DisplayName => "Drag (hold to move)";
}

/// <summary>Gyro scrolls instead of moving the cursor while held.</summary>
public sealed record ScrollAction : ButtonAction
{
    public override bool IsGyroMode => true;
    public override string DisplayName => "Scroll (hold to scroll)";
}

/// <summary>
/// Opens the radial menu while held: gyro moves a dot to pick a slice, and releasing runs it.
/// See <see cref="RadialMenuConfiguration"/>.
/// </summary>
public sealed record RadialMenuAction : ButtonAction
{
    public override bool IsGyroMode => true;
    public override string DisplayName => "Radial Menu";
}

/// <summary>
/// Taps the key when the hold begins and again when it ends: hold-to-talk for an app whose shortcut
/// toggles, such as a dictation app set to toggle mode.
/// </summary>
public sealed record HoldToggleAction(KeyCombo Combo) : ButtonAction
{
    public override string DisplayName => $"{Combo} on press and on release";
}

/// <summary>Taps the key at once, then again every <paramref name="IntervalSeconds"/> while held.</summary>
public sealed record RepeatKeyAction(KeyCombo Combo, double IntervalSeconds) : ButtonAction
{
    public override string DisplayName => $"{Combo}, every {IntervalSeconds:0.##} s while held";
}

/// <summary>
/// Switches between desktop and game mode (<see cref="Engine.InputMode"/>). Sends nothing itself;
/// the engine asks its host to flip the mode.
/// </summary>
public sealed record ToggleModeAction : ButtonAction
{
    public override string DisplayName => "Switch desktop/game mode";
}

/// <summary>
/// Game mode's answer to Splatoon's Y (reset camera): undoes the vertical aim JamCon has sent
/// since the last reset, so the view is level again. Approximate: motion the game clamped at its
/// look limit, or from a real mouse, is not accounted for.
/// </summary>
public sealed record LevelViewAction : ButtonAction
{
    public override string DisplayName => "Level the view";
}

/// <summary>
/// What a button does, by gesture. Press and Hold as upstream; DoubleTap and DoubleTapHold are new.
/// </summary>
/// <remarks>
/// With either double-tap action set, a short press is not acted on at release: it waits up to the
/// profile's double-tap window for a second press. A second short press runs
/// <see cref="DoubleTap"/>; holding the second press runs <see cref="DoubleTapHold"/> for as long
/// as it is held; no second press runs <see cref="Press"/>. Holding the first press runs
/// <see cref="Hold"/> exactly as without double-tap actions.
/// </remarks>
public sealed record ButtonActions
{
    public static ButtonActions Empty { get; } = new(ButtonAction.None, ButtonAction.None);

    /// <remarks>
    /// Annotated so JSON has exactly one constructor to choose. The double-tap parameters are
    /// optional so profiles saved before they existed still load.
    /// </remarks>
    [JsonConstructor]
    public ButtonActions(ButtonAction press, ButtonAction hold, ButtonAction? doubleTap = null, ButtonAction? doubleTapHold = null)
    {
        Press = press;
        Hold = hold;
        DoubleTap = doubleTap ?? ButtonAction.None;
        DoubleTapHold = doubleTapHold ?? ButtonAction.None;
    }

    public ButtonActions(ButtonAction press) : this(press, ButtonAction.None)
    {
    }

    public ButtonAction Press { get; init; }
    public ButtonAction Hold { get; init; }
    public ButtonAction DoubleTap { get; init; }
    public ButtonAction DoubleTapHold { get; init; }

    [JsonIgnore]
    public bool PressIsGyroMode => Press.IsGyroMode;

    /// <summary>True when presses must wait to see whether a second press follows.</summary>
    [JsonIgnore]
    public bool UsesDoubleTap => !PressIsGyroMode && (DoubleTap is not NoAction || DoubleTapHold is not NoAction);
}
