using static JamCon.Output.NativeMethods;

namespace JamCon.Output.Tests;

/// <summary>
/// Drives the real cursor through SendInput to prove absolute positioning lands on exact pixels on
/// this machine's actual monitor layout. Moves the user's pointer, so it runs only when the
/// JAMCON_INTERACTIVE_TESTS environment variable is 1, and restores the pointer afterwards. Without
/// the variable it returns immediately; CI has no desktop to meaningfully test against.
/// </summary>
public class InteractiveCursorTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("JAMCON_INTERACTIVE_TESTS") == "1";

    private static (int X, int Y) Cursor()
    {
        Assert.True(GetCursorPos(out var point));
        return (point.X, point.Y);
    }

    [Fact]
    public void AbsoluteMovesLandOnExactPixelsOnEveryMonitor()
    {
        if (!Enabled) return;
        using var dpi = new DpiScope();

        var original = Cursor();
        var sink = new SendInputOutputSink(_ => { });
        try
        {
            foreach (var monitor in SendInputOutputSink.Monitors())
            {
                int left = (int)monitor.Left, top = (int)monitor.Top, right = (int)monitor.Right, bottom = (int)monitor.Bottom;
                (int X, int Y)[] targets =
                [
                    (left, top), (right, bottom), (left, bottom), (right, top),
                    ((left + right) / 2, (top + bottom) / 2), (left + 1, top + 1), (right - 1, bottom - 1),
                    (left + 333, top + 217),
                ];

                foreach (var (tx, ty) in targets)
                {
                    var (cx, cy) = Cursor();
                    sink.MoveCursor(tx - cx, ty - cy);
                    Thread.Sleep(15);
                    Assert.Equal((tx, ty), Cursor());
                }
            }
        }
        finally
        {
            var (cx, cy) = Cursor();
            sink.MoveCursor(original.X - cx, original.Y - cy);
        }
    }

    [Fact]
    public void SubPixelMotionAccumulatesInsteadOfVanishing()
    {
        if (!Enabled) return;
        using var dpi = new DpiScope();

        var original = Cursor();
        var sink = new SendInputOutputSink(_ => { });
        try
        {
            // Start away from any screen edge so clamping cannot interfere.
            var monitor = SendInputOutputSink.Monitors()[0];
            var startX = (int)((monitor.Left + monitor.Right) / 2);
            var startY = (int)((monitor.Top + monitor.Bottom) / 2);
            var (cx, cy) = Cursor();
            sink.MoveCursor(startX - cx, startY - cy);
            Thread.Sleep(15);

            // Twelve quarter-pixel steps: no single step reaches a pixel, together they make three.
            for (var i = 0; i < 12; i++)
            {
                sink.MoveCursor(0.25, 0);
                Thread.Sleep(5);
            }

            Thread.Sleep(15);
            Assert.Equal((startX + 3, startY), Cursor());
        }
        finally
        {
            var (cx, cy) = Cursor();
            sink.MoveCursor(original.X - cx, original.Y - cy);
        }
    }
}
