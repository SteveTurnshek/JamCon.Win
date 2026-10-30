namespace JamCon.Core.Mapping;

/// <summary>
/// Xbox controller buttons, with XInput's bit values. The triggers are analog on a real pad; a
/// Joy-Con's ZL/ZR are digital, so here they are buttons that pull the trigger fully.
/// </summary>
[Flags]
public enum GamepadButtons : uint
{
    None = 0,
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LeftTrigger = 0x1_0000,
    RightTrigger = 0x2_0000,
}

/// <summary>
/// One complete virtual Xbox controller state. Sticks run -1 to 1 with positive Y up, as XInput;
/// triggers 0 to 1.
/// </summary>
public readonly record struct GamepadReport(
    GamepadButtons Buttons,
    double LeftX,
    double LeftY,
    double RightX,
    double RightY)
{
    public static GamepadReport Neutral { get; } = new(GamepadButtons.None, 0, 0, 0, 0);

    public double LeftTrigger => Buttons.HasFlag(GamepadButtons.LeftTrigger) ? 1.0 : 0.0;
    public double RightTrigger => Buttons.HasFlag(GamepadButtons.RightTrigger) ? 1.0 : 0.0;
}

/// <summary>How gyro aim reaches the game in Xbox controller mode.</summary>
public enum ControllerGyroOutput
{
    /// <summary>Blended into the right stick: works in any game that takes a controller.</summary>
    RightStick,

    /// <summary>
    /// Relative mouse motion beside the controller: precise, but only for games that accept mouse
    /// and controller input at once.
    /// </summary>
    Mouse,
}

/// <summary>When gyro aims in Xbox controller mode.</summary>
public enum ControllerGyroActivation
{
    Always,

    /// <summary>Only while the left trigger is held: "gyro when aiming down sights".</summary>
    WhileLeftTrigger,
}
