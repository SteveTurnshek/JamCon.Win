using JamCon.Core.Mapping;

namespace JamCon.Core.Output;

/// <summary>A held-down output: a mouse button or a key combination.</summary>
public abstract record SyntheticOutput
{
    public static SyntheticOutput Mouse(MouseButton button) => new MouseButtonOutput(button);
    public static SyntheticOutput Key(KeyCombo combo) => new KeyOutput(combo);
}

public sealed record MouseButtonOutput(MouseButton Button) : SyntheticOutput;

public sealed record KeyOutput(KeyCombo Combo) : SyntheticOutput;

/// <summary>A single down or up transition sent to the operating system.</summary>
public readonly record struct SyntheticOutputEvent(SyntheticOutput Output, bool IsPressed);

public enum SyntheticOutputRole
{
    Press,
    Hold,
    RadialMenu,
    SystemAction,
    DoubleTap,
}

/// <summary>Who is holding an output down: a control on a device, in a given role.</summary>
public readonly record struct SyntheticOutputOwner(string? DeviceId, string Control, SyntheticOutputRole Role)
{
    public SyntheticOutputOwner WithRole(SyntheticOutputRole role, string? control = null) =>
        new(DeviceId, control ?? Control, role);
}

/// <summary>
/// Everything the engine emits. The mouse-and-keyboard implementation lives in JamCon.Output;
/// a virtual-gamepad sink (Phase 4) implements the same interface, which is what lets the two
/// be swapped.
/// </summary>
public interface IOutputSink
{
    /// <summary>Press or release a mouse button or key combination.</summary>
    void Post(SyntheticOutputEvent outputEvent);

    /// <summary>Move the cursor by a fractional pixel delta. Positive dy is down.</summary>
    void MoveCursor(double dx, double dy);

    /// <summary>Scroll by a fractional pixel delta. Positive dx scrolls right; positive dy scrolls down.</summary>
    void Scroll(double dx, double dy);

    /// <summary>
    /// Relative mouse motion for game mode, in fractional mouse counts. Positive dy is down. Unlike
    /// <see cref="MoveCursor"/> it never positions the cursor, which games recentre themselves.
    /// </summary>
    void Look(double dx, double dy);
}

/// <summary>
/// Turns fractional deltas into whole units without losing the remainder. Without this, slow
/// precise motion below one unit per frame would vanish entirely.
/// </summary>
public struct SubUnitAccumulator
{
    private double _remainderX;
    private double _remainderY;

    public (int X, int Y) Consume(double dx, double dy)
    {
        _remainderX += double.IsFinite(dx) ? dx : 0;
        _remainderY += double.IsFinite(dy) ? dy : 0;
        var x = IntegralPart(_remainderX);
        var y = IntegralPart(_remainderY);
        _remainderX -= x;
        _remainderY -= y;
        return (x, y);
    }

    public void Reset()
    {
        _remainderX = 0;
        _remainderY = 0;
    }

    private static int IntegralPart(double value) => (int)Math.Clamp(Math.Truncate(value), int.MinValue, int.MaxValue);
}

/// <summary>
/// A virtual Xbox controller. The ViGEmBus implementation lives in JamCon.Output. The pad should
/// appear to games only while it is in use, so it connects on the first report and disconnects on
/// <see cref="Release"/>.
/// </summary>
public interface IGamepadOutput
{
    /// <summary>Whether a virtual controller can be created at all (the driver is installed).</summary>
    bool IsAvailable { get; }

    void Send(GamepadReport report);

    /// <summary>Returns everything to neutral and unplugs the virtual controller.</summary>
    void Release();
}

/// <summary>What happened to an action the engine runs itself; see <see cref="ActionExecutor"/>.</summary>
public enum EngineActionPhase
{
    Press,
    Release,

    /// <summary>A press and release in one: the engine holds it briefly so a game polling the pad sees it.</summary>
    Tap,
}
