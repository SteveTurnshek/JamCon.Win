using System.Text.Json.Serialization;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Engine;

/// <summary>
/// Everything the engine reads, as one immutable snapshot. Ported from SettingsStore.InputSettings,
/// reduced to what a Joy-Con 2 uses.
/// </summary>
public sealed record InputSettings
{
    public const double DefaultJoystickScrollSpeed = 8.0;
    public const double DefaultJoystickScrollAcceleration = 3.0;
    public const double DefaultGameStickTurnSpeed = 270.0;
    public const double DefaultControllerGyroFullSpeed = 180.0;
    public const double DefaultControllerStickDeadzone = 0.2;

    public static InputSettings Default { get; } = new();

    /// <summary>Master switch for all output. Turning it off releases anything held down.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Desktop moves the cursor; game mode aims with relative mouse motion. See <see cref="InputMode"/>.</summary>
    public InputMode Mode { get; init; } = InputMode.Desktop;

    /// <summary>Desktop gyro tuning.</summary>
    public GyroSettings Gyro { get; init; } = GyroSettings.Default;

    public GyroSettings GameGyro { get; init; } = GyroSettings.GameDefault;

    public ButtonMappingProfile RightButtons { get; init; } = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);
    public ButtonMappingProfile LeftButtons { get; init; } = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Left);
    public ButtonMappingProfile GameRightButtons { get; init; } = ButtonMappingProfile.GameDefaultFor(JoyCon2Handedness.Right);
    public ButtonMappingProfile GameLeftButtons { get; init; } = ButtonMappingProfile.GameDefaultFor(JoyCon2Handedness.Left);

    /// <summary>Game mode with both Joy-Cons connected; see <see cref="ButtonMappingProfile.GamePairDefaultFor"/>.</summary>
    public ButtonMappingProfile GamePairRightButtons { get; init; } = ButtonMappingProfile.GamePairDefaultFor(JoyCon2Handedness.Right);
    public ButtonMappingProfile GamePairLeftButtons { get; init; } = ButtonMappingProfile.GamePairDefaultFor(JoyCon2Handedness.Left);

    /// <summary>Xbox controller mode; see <see cref="ButtonMappingProfile.ControllerDefaultFor"/>.</summary>
    public ButtonMappingProfile ControllerRightButtons { get; init; } = ButtonMappingProfile.ControllerDefaultFor(JoyCon2Handedness.Right, paired: false);
    public ButtonMappingProfile ControllerLeftButtons { get; init; } = ButtonMappingProfile.ControllerDefaultFor(JoyCon2Handedness.Left, paired: false);
    public ButtonMappingProfile ControllerPairRightButtons { get; init; } = ButtonMappingProfile.ControllerDefaultFor(JoyCon2Handedness.Right, paired: true);
    public ButtonMappingProfile ControllerPairLeftButtons { get; init; } = ButtonMappingProfile.ControllerDefaultFor(JoyCon2Handedness.Left, paired: true);

    /// <summary>Gyro tuning for Xbox controller mode when it aims with the mouse.</summary>
    public GyroSettings ControllerGyro { get; init; } = GyroSettings.GameDefault;

    public ControllerGyroOutput ControllerGyroOutput { get; init; } = ControllerGyroOutput.RightStick;
    public ControllerGyroActivation ControllerGyroActivation { get; init; } = ControllerGyroActivation.Always;

    /// <summary>Rotation speed, °/s, that pushes the right stick all the way when gyro aims through it.</summary>
    public double ControllerGyroFullSpeed { get; init; } = DefaultControllerGyroFullSpeed;

    /// <summary>
    /// The game's own right-stick deadzone, as a fraction of full travel. Gyro output starts just past
    /// it, so slow, small turns still register instead of vanishing inside the game's deadzone.
    /// </summary>
    public double ControllerStickDeadzone { get; init; } = DefaultControllerStickDeadzone;

    /// <summary>With both Joy-Cons in game mode, how fast a full right-stick push turns, in degrees a second.</summary>
    public double GameStickTurnSpeed { get; init; } = DefaultGameStickTurnSpeed;

    /// <summary>In game mode, the stick holds W, A, S and D instead of scrolling.</summary>
    public bool GameStickMovement { get; init; } = true;

    /// <summary>Whether gyro and stick may move the cursor or scroll. Buttons still work when off.</summary>
    public bool CursorControlEnabled { get; init; } = true;

    public bool JoystickScrollEnabled { get; init; } = true;
    public double JoystickScrollSpeed { get; init; } = DefaultJoystickScrollSpeed;
    public double JoystickScrollAcceleration { get; init; } = DefaultJoystickScrollAcceleration;

    public RadialMenuConfiguration RadialMenu { get; init; } = RadialMenuConfiguration.Default;

    /// <summary>
    /// The buttons in effect for that side: the current mode's profile, and in game mode the paired
    /// layout when both Joy-Cons are connected.
    /// </summary>
    public ButtonMappingProfile ButtonsFor(JoyCon2Handedness handedness, bool paired = false) => (Mode, handedness, paired) switch
    {
        (InputMode.Controller, JoyCon2Handedness.Right, true) => ControllerPairRightButtons,
        (InputMode.Controller, _, true) => ControllerPairLeftButtons,
        (InputMode.Controller, JoyCon2Handedness.Right, _) => ControllerRightButtons,
        (InputMode.Controller, _, _) => ControllerLeftButtons,
        (InputMode.Game, JoyCon2Handedness.Right, true) => GamePairRightButtons,
        (InputMode.Game, _, true) => GamePairLeftButtons,
        (InputMode.Game, JoyCon2Handedness.Right, _) => GameRightButtons,
        (InputMode.Game, _, _) => GameLeftButtons,
        (_, JoyCon2Handedness.Right, _) => RightButtons,
        _ => LeftButtons,
    };

    /// <summary>The gyro tuning in effect for the current mode.</summary>
    [JsonIgnore]
    public GyroSettings ActiveGyro => Mode switch
    {
        InputMode.Game => GameGyro,
        InputMode.Controller => ControllerGyro,
        _ => Gyro,
    };
}

/// <summary>What the controller is being used for.</summary>
public enum InputMode
{
    /// <summary>Gyro moves the cursor (while the clutch is held); stick scrolls.</summary>
    Desktop,

    /// <summary>
    /// Gyro always aims, as relative mouse motion that games read as mouse look; the stick holds
    /// WASD. Absolute cursor positioning cannot work here: games recentre the cursor every frame.
    /// Shown as "mouse and keyboard game" mode; the stored name predates the controller mode.
    /// </summary>
    Game,

    /// <summary>
    /// A virtual Xbox controller (ViGEmBus), with gyro aiming through the right stick or the mouse.
    /// </summary>
    Controller,
}

/// <summary>
/// The one-way bridge from UI to engine: the UI replaces the snapshot, the engine reads it once per
/// frame. Reads are lock-free; writes are serialized.
/// </summary>
public sealed class SettingsStore(InputSettings initial)
{
    private readonly object _writeGate = new();
    private InputSettings _current = initial;

    public SettingsStore() : this(InputSettings.Default)
    {
    }

    public event Action<InputSettings>? Changed;

    public InputSettings Snapshot() => Volatile.Read(ref _current);

    public void Update(Func<InputSettings, InputSettings> transform)
    {
        InputSettings next;
        lock (_writeGate)
        {
            next = transform(_current);
            Volatile.Write(ref _current, next);
        }

        Changed?.Invoke(next);
    }
}

/// <summary>Ported from NumericSettingNormalizer in SliderWithLabel.swift.</summary>
public static class NumericSettingNormalizer
{
    /// <summary>
    /// Clamps to the range and snaps to the step, measured from the lower bound.
    /// Non-finite values become the lower bound.
    /// </summary>
    /// <remarks>
    /// Halfway values round away from zero, matching Swift's <c>rounded()</c>. C#'s default
    /// banker's rounding would silently snap some values to a different step.
    /// </remarks>
    public static double Normalize(double value, double min, double max, double step)
    {
        if (!double.IsFinite(value)) return min;
        var clamped = Math.Clamp(value, min, max);
        if (!double.IsFinite(step) || step <= 0) return clamped;

        var steps = Math.Round((clamped - min) / step, MidpointRounding.AwayFromZero);
        return Math.Clamp(min + steps * step, min, max);
    }
}

public static class InputModes
{
    /// <summary>The order holding the mode button steps through.</summary>
    public static InputMode Next(InputMode mode, bool controllerAvailable) => mode switch
    {
        InputMode.Desktop => InputMode.Game,
        InputMode.Game when controllerAvailable => InputMode.Controller,
        _ => InputMode.Desktop,
    };

    public static string DisplayName(this InputMode mode) => mode switch
    {
        InputMode.Game => "Mouse & keyboard game mode",
        InputMode.Controller => "Xbox controller mode",
        _ => "Desktop mode",
    };
}
