using System.Diagnostics;
using JamCon.Core.Output;

namespace JamCon.Output;

/// <summary>
/// Wraps another sink and re-emits cursor, scroll and look motion at a high fixed rate, so ~67 Hz
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
    private readonly MotionPacer _look = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private double _spreadSeconds;
    private volatile bool _enabled = true;

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

    /// <summary>
    /// Off passes motion straight through as it arrives. Switchable at any time: turning it off
    /// flushes whatever was still being spread out.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                _enabled = value;
                if (!value) FlushLocked();
            }
        }
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void MoveCursor(double dx, double dy) => Add(_cursor, dx, dy, _inner.MoveCursor);

    public void Scroll(double dx, double dy) => Add(_scroll, dx, dy, _inner.Scroll);

    public void Look(double dx, double dy) => Add(_look, dx, dy, _inner.Look);

    private void Add(MotionPacer pacer, double dx, double dy, Action<double, double> direct)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                direct(dx, dy);
                return;
            }

            pacer.Add(dx, dy, Now, _spreadSeconds);
        }

        _wake.Set();
    }

    public void Post(SyntheticOutputEvent outputEvent)
    {
        lock (_gate)
        {
            FlushLocked();
            _inner.Post(outputEvent);
        }
    }

    private void FlushLocked()
    {
        var (cx, cy) = _cursor.Flush();
        if (cx != 0 || cy != 0) _inner.MoveCursor(cx, cy);
        var (sx, sy) = _scroll.Flush();
        if (sx != 0 || sy != 0) _inner.Scroll(sx, sy);
        var (lx, ly) = _look.Flush();
        if (lx != 0 || ly != 0) _inner.Look(lx, ly);
    }

    private void Run()
    {
        using var waiter = new PreciseWaiter();
        var next = Stopwatch.GetTimestamp();
        var tick = (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);

        while (_running)
        {
            bool pending;
            lock (_gate) pending = _cursor.HasPending || _scroll.HasPending || _look.HasPending;

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
                var (lx, ly) = _look.Take(seconds);
                if (lx != 0 || ly != 0) _inner.Look(lx, ly);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        lock (_gate) FlushLocked();

        _wake.Dispose();
    }
}
