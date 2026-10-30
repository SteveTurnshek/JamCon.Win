using System.Diagnostics;
using System.Runtime.InteropServices;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using static JamCon.Output.NativeMethods;

namespace JamCon.Output;

/// <summary>
/// Mouse and keyboard output through SendInput. The Windows counterpart of upstream's
/// MouseController and its CGEvent backend.
/// </summary>
/// <remarks>
/// <para>
/// Cursor motion is absolute (<c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c>), as the plan
/// requires: relative <c>MOUSEEVENTF_MOVE</c> passes through Windows pointer ballistics ("Enhance
/// pointer precision" and the speed slider), which would distort the tuned gyro curve and make
/// sensitivity tuning unfalsifiable. Upstream's macOS controller works the same way: it keeps a
/// floating-point position, clamps it to the nearest point on a real display, and posts absolute
/// positions.
/// </para>
/// <para>
/// The fractional position is kept between frames, so slow, precise gyro motion of less than a pixel
/// per report still accumulates instead of vanishing. If the real cursor is found somewhere other
/// than where this sink last put it (the physical mouse moved, or an app warped it), the sink adopts
/// the real position rather than yanking the cursor back.
/// </para>
/// <para>
/// UIPI blocks injection into windows running elevated unless JamCon is elevated too. That is
/// reported, never worked around (plan, Phase 3).
/// </para>
/// <para>Not thread-safe; called only from the engine thread.</para>
/// </remarks>
public sealed unsafe class SendInputOutputSink(Action<string> log) : IOutputSink
{
    /// <summary>Upstream's scroll scale: gyro deltas are screen pixels, scrolling wants smaller steps.</summary>
    private const double ScrollScale = 0.5;

    /// <summary>
    /// Wheel units per pixel of scroll. One notch is 120 units and scrolls three lines by default,
    /// roughly 50–60 px, so about 2 units per pixel. An estimate; tune by feel.
    /// </summary>
    private const double WheelUnitsPerPixel = 2.0;

    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(5);

    private double _x;
    private double _y;
    private int _sentX;
    private int _sentY;
    private bool _hasPosition;
    private IReadOnlyList<DisplayBounds> _displays = [];
    private (int Left, int Top, int Width, int Height) _virtualScreen;
    private long _displaysRefreshedAt = long.MinValue;
    private SubUnitAccumulator _wheel;
    private int _blockedCount;

    /// <summary>Injected events Windows refused, typically because an elevated window has focus.</summary>
    public int BlockedCount => _blockedCount;

    public void MoveCursor(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        RefreshDisplaysIfStale();

        if (GetCursorPos(out var actual)
            && (!_hasPosition || Math.Abs(actual.X - _sentX) > 1 || Math.Abs(actual.Y - _sentY) > 1))
        {
            _x = actual.X;
            _y = actual.Y;
            _sentX = actual.X;
            _sentY = actual.Y;
            _hasPosition = true;
        }

        (_x, _y) = DisplayGeometry.ClosestPoint(_x + dx, _y + dy, _displays);
        var px = (int)Math.Floor(_x);
        var py = (int)Math.Floor(_y);
        if (px == _sentX && py == _sentY) return;

        var (left, top, width, height) = _virtualScreen;
        Send(new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = AbsoluteCoordinates.Normalize(px, left, width),
                    Dy = AbsoluteCoordinates.Normalize(py, top, height),
                    Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
                },
            },
        });

        _sentX = px;
        _sentY = py;
    }

    public void Scroll(double dx, double dy)
    {
        // Positive dy scrolls down, which is a negative wheel delta; positive dx scrolls right,
        // which is a positive horizontal-wheel delta.
        var (horizontal, vertical) = _wheel.Consume(
            dx * ScrollScale * WheelUnitsPerPixel,
            -dy * ScrollScale * WheelUnitsPerPixel);

        if (vertical != 0) SendMouse(MouseEventWheel, vertical);
        if (horizontal != 0) SendMouse(MouseEventHWheel, horizontal);
    }

    public void Post(SyntheticOutputEvent outputEvent)
    {
        switch (outputEvent.Output)
        {
            case MouseButtonOutput mouse:
                SendMouse(MouseButtonFlag(mouse.Button, outputEvent.IsPressed), 0);
                break;
            case KeyOutput key:
                Send(KeyboardInputs.For(key.Combo, outputEvent.IsPressed));
                break;
        }
    }

    private static uint MouseButtonFlag(MouseButton button, bool isPressed) => (button, isPressed) switch
    {
        (MouseButton.Left, true) => MouseEventLeftDown,
        (MouseButton.Left, false) => MouseEventLeftUp,
        (MouseButton.Right, true) => MouseEventRightDown,
        (MouseButton.Right, false) => MouseEventRightUp,
        (_, true) => MouseEventMiddleDown,
        _ => MouseEventMiddleUp,
    };

    private void SendMouse(uint flags, int data) => Send(new Input
    {
        Type = InputMouse,
        Union = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)data) } },
    });

    private void Send(params Input[] inputs)
    {
        fixed (Input* pointer = inputs)
        {
            var sent = SendInput((uint)inputs.Length, pointer, sizeof(Input));
            if (sent == inputs.Length) return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (Interlocked.Increment(ref _blockedCount) is 1 or 100 or 1000)
        {
            log($"SendInput was refused ({_blockedCount} so far, error {error}). " +
                "Most often an elevated window has focus; Windows blocks input from a non-elevated app into it.");
        }
    }

    private void RefreshDisplaysIfStale()
    {
        var now = Stopwatch.GetTimestamp();
        if (_displaysRefreshedAt != long.MinValue && Stopwatch.GetElapsedTime(_displaysRefreshedAt, now) < DisplayRefreshInterval) return;
        _displaysRefreshedAt = now;

        _virtualScreen = (
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
        _displays = Monitors();
    }

    /// <summary>
    /// Each monitor's rectangle of valid cursor pixels, in physical coordinates (the app is
    /// per-monitor DPI aware). Right and bottom are inclusive, as DisplayGeometry expects.
    /// </summary>
    public static IReadOnlyList<DisplayBounds> Monitors()
    {
        var monitors = new List<DisplayBounds>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Rect* rect, nint data)
    {
        var monitors = (List<DisplayBounds>)GCHandle.FromIntPtr(data).Target!;
        monitors.Add(new DisplayBounds(rect->Left, rect->Top, rect->Right - 1, rect->Bottom - 1));
        return 1;
    }
}
