using System.Diagnostics;
using JamCon.Core.Output;

namespace JamCon.Output;

/// <summary>
/// Wraps another sink and re-emits cursor and scroll motion at a high fixed rate, so ~67 Hz
/// controller motion looks smooth on high-refresh displays. Buttons and keys pass straight through.
/// </summary>
/// <remarks>
/// A dedicated above-normal-priority thread releases paced motion every
/// <see cref="TickInterval"/> while any is pending, and sleeps on an event otherwise, so an idle
/// controller costs no CPU. Before any button or key event, pending motion is flushed, so a click
/// lands where the cursor was heading rather than up to one report interval behind it.
/// </remarks>
public sealed class PacedOutputSink : IOutputSink, IDisposable
{
    /// <summary>240 Hz: two updates per frame on a 120 Hz display, at least one on 240 Hz.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.0 / 240.0);

    private readonly IOutputSink _inner;
    private readonly object _gate = new();
    private readonly MotionPacer _cursor = new();
    private readonly MotionPacer _scroll = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private double _spreadSeconds;

    /// <param name="spreadSeconds">
    /// How long each delta is spread over: the expected interval between reports.
    /// </param>
    public PacedOutputSink(IOutputSink inner, double spreadSeconds = 1.0 / 66.0)
    {
        _inner = inner;
        _spreadSeconds = spreadSeconds;
        _thread = new Thread(Run)
        {
            Name = "JamCon output pacing",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Changes the spread window, e.g. when the measured report rate settles.</summary>
    public double SpreadSeconds
    {
        get
        {
            lock (_gate) return _spreadSeconds;
        }
        set
        {
            lock (_gate) _spreadSeconds = Math.Clamp(value, 0.002, 0.05);
        }
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void MoveCursor(double dx, double dy)
    {
        lock (_gate) _cursor.Add(dx, dy, Now, _spreadSeconds);
        _wake.Set();
    }

    public void Scroll(double dx, double dy)
    {
        lock (_gate) _scroll.Add(dx, dy, Now, _spreadSeconds);
        _wake.Set();
    }

    public void Post(SyntheticOutputEvent outputEvent)
    {
        lock (_gate)
        {
            var (cx, cy) = _cursor.Flush();
            if (cx != 0 || cy != 0) _inner.MoveCursor(cx, cy);
            var (sx, sy) = _scroll.Flush();
            if (sx != 0 || sy != 0) _inner.Scroll(sx, sy);
            _inner.Post(outputEvent);
        }
    }

    private void Run()
    {
        using var waiter = new PreciseWaiter();
        var next = Stopwatch.GetTimestamp();
        var tick = (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);

        while (_running)
        {
            bool pending;
            lock (_gate) pending = _cursor.HasPending || _scroll.HasPending;

            if (!pending)
            {
                _wake.WaitOne();
                next = Stopwatch.GetTimestamp();
                continue;
            }

            waiter.WaitUntil(next);
            next += tick;
            var now = Stopwatch.GetTimestamp();
            if (now - next > tick * 4) next = now; // fell far behind (e.g. a suspend); do not burst

            lock (_gate)
            {
                var seconds = now / (double)Stopwatch.Frequency;
                var (cx, cy) = _cursor.Take(seconds);
                if (cx != 0 || cy != 0) _inner.MoveCursor(cx, cy);
                var (sx, sy) = _scroll.Take(seconds);
                if (sx != 0 || sy != 0) _inner.Scroll(sx, sy);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            var (cx, cy) = _cursor.Flush();
            if (cx != 0 || cy != 0) _inner.MoveCursor(cx, cy);
        }

        _wake.Dispose();
    }
}
