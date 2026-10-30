using JamCon.Core.Protocol;

namespace JamCon.Core.Mapping;

/// <summary>
/// Logical Joy-Con 2 buttons. Values are stable array indices for hot-path state, matching
/// upstream's JoyConLogicalButton.index, and also the persisted names.
/// </summary>
public enum JoyCon2Button
{
    A = 0,
    B = 1,
    X = 2,
    Y = 3,
    L = 4,
    R = 5,
    ZL = 6,
    ZR = 7,
    Plus = 8,
    Minus = 9,
    Home = 10,
    Capture = 11,
    StickClick = 12,
    SL = 13,
    SR = 14,
    DpadUp = 15,
    DpadDown = 16,
    DpadLeft = 17,
    DpadRight = 18,
    C = 19,
}

/// <summary>
/// Where each button lives in the four-byte field at report offset 0x04, as bit indices of
/// <see cref="JoyCon2Report.Buttons"/>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream copies report bytes 0x04–0x06 into the original Joy-Con HID layout (bytes 3–5) and
/// reads them with that layout's positions. Reading the same bits straight from the report is
/// equivalent: byte 0x04 holds the right-side buttons (bits 0–7), byte 0x05 the shared system
/// buttons (bits 8–15) and byte 0x06 the left-side buttons (bits 16–23). Byte 0x07 is not buttons:
/// it reads 0xE0 at rest and never changed during any press (docs/Windows-Port.md).
/// </para>
/// <para>
/// Verified on Windows with a right Joy-Con 2 by pressing each button alone: A, B, X, Y, R, ZR,
/// Plus, stick click, SL, SR and C. HOME (bit 12) and every left-controller position are
/// upstream's values and still unverified on this platform.
/// </para>
/// </remarks>
public static class JoyCon2ButtonLayout
{
    private const int Unavailable = -1;

    private static readonly int[] RightBits = BuildRight();
    private static readonly int[] LeftBits = BuildLeft();

    public static IReadOnlyList<JoyCon2Button> RightButtons { get; } =
    [
        JoyCon2Button.A, JoyCon2Button.B, JoyCon2Button.X, JoyCon2Button.Y, JoyCon2Button.R,
        JoyCon2Button.ZR, JoyCon2Button.Plus, JoyCon2Button.Home, JoyCon2Button.StickClick,
        JoyCon2Button.SL, JoyCon2Button.SR, JoyCon2Button.C,
    ];

    public static IReadOnlyList<JoyCon2Button> LeftButtons { get; } =
    [
        JoyCon2Button.DpadUp, JoyCon2Button.DpadDown, JoyCon2Button.DpadLeft, JoyCon2Button.DpadRight,
        JoyCon2Button.L, JoyCon2Button.ZL, JoyCon2Button.Minus, JoyCon2Button.Capture,
        JoyCon2Button.StickClick, JoyCon2Button.SL, JoyCon2Button.SR,
    ];

    public const int ButtonCount = 20;

    public static IReadOnlyList<JoyCon2Button> AvailableButtons(JoyCon2Handedness handedness) =>
        handedness == JoyCon2Handedness.Right ? RightButtons : LeftButtons;

    /// <summary>Bit index within the button field, or null if the button is not on this side.</summary>
    public static int? BitFor(JoyCon2Button button, JoyCon2Handedness handedness)
    {
        var bit = (handedness == JoyCon2Handedness.Right ? RightBits : LeftBits)[(int)button];
        return bit == Unavailable ? null : bit;
    }

    public static bool IsPressed(uint buttons, JoyCon2Button button, JoyCon2Handedness handedness) =>
        BitFor(button, handedness) is { } bit && (buttons & (1u << bit)) != 0;

    public static string DisplayName(JoyCon2Button button) => button switch
    {
        JoyCon2Button.Plus => "+",
        JoyCon2Button.Minus => "−",
        JoyCon2Button.StickClick => "Stick",
        JoyCon2Button.DpadUp => "Up",
        JoyCon2Button.DpadDown => "Down",
        JoyCon2Button.DpadLeft => "Left",
        JoyCon2Button.DpadRight => "Right",
        _ => button.ToString(),
    };

    private static int[] BuildRight()
    {
        var bits = Enumerable.Repeat(Unavailable, ButtonCount).ToArray();
        // Byte 0x04.
        bits[(int)JoyCon2Button.Y] = 0;
        bits[(int)JoyCon2Button.X] = 1;
        bits[(int)JoyCon2Button.B] = 2;
        bits[(int)JoyCon2Button.A] = 3;
        bits[(int)JoyCon2Button.SR] = 4;
        bits[(int)JoyCon2Button.SL] = 5;
        bits[(int)JoyCon2Button.R] = 6;
        bits[(int)JoyCon2Button.ZR] = 7;
        // Byte 0x05.
        bits[(int)JoyCon2Button.Plus] = 9;
        bits[(int)JoyCon2Button.StickClick] = 10;
        bits[(int)JoyCon2Button.Home] = 12;
        bits[(int)JoyCon2Button.C] = 14;
        return bits;
    }

    private static int[] BuildLeft()
    {
        var bits = Enumerable.Repeat(Unavailable, ButtonCount).ToArray();
        // Byte 0x05.
        bits[(int)JoyCon2Button.Minus] = 8;
        bits[(int)JoyCon2Button.StickClick] = 11;
        bits[(int)JoyCon2Button.Capture] = 13;
        // Byte 0x06.
        bits[(int)JoyCon2Button.DpadDown] = 16;
        bits[(int)JoyCon2Button.DpadUp] = 17;
        bits[(int)JoyCon2Button.DpadRight] = 18;
        bits[(int)JoyCon2Button.DpadLeft] = 19;
        bits[(int)JoyCon2Button.SR] = 20;
        bits[(int)JoyCon2Button.SL] = 21;
        bits[(int)JoyCon2Button.L] = 22;
        bits[(int)JoyCon2Button.ZL] = 23;
        return bits;
    }
}
