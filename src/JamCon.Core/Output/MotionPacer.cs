namespace JamCon.Core.Output;

/// <summary>
/// Spreads each motion delta evenly over a time window, so motion arriving in ~67 Hz steps can be
/// emitted at a much higher rate.
/// </summary>
/// <remarks>
/// <para>
/// A Joy-Con 2 delivers about 67 reports a second. Emitting each report's cursor delta at once moves
/// the cursor in 15 ms jumps, which looks coarse on a 120 Hz or faster display next to a mouse
/// reporting at 125–1000 Hz. Instead, each delta is released gradually over the expected interval
/// until the next report, and an output loop takes from it at a high rate.
/// </para>
/// <para>
/// The cost is latency: on average half the spread window, about 7.5 ms at 67 Hz. Total motion is
/// conserved exactly, so pacing changes when the cursor moves, never how far.
/// </para>
/// <para>
/// If a new delta arrives before the previous one has been fully released, the unreleased remainder
/// joins it and both are spread over the new window, so a late report never leaves motion stranded.
/// </para>
/// <para>Not thread-safe; callers serialize access.</para>
/// </remarks>
public sealed class MotionPacer
{
    private double _pendingX;
    private double _pendingY;
    private double _windowStart;
    private double _windowEnd;

    public bool HasPending => _pendingX != 0 || _pendingY != 0;

    /// <summary>Queue a delta to be released evenly between <paramref name="now"/> and now + spread.</summary>
    public void Add(double dx, double dy, double now, double spreadSeconds)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        _pendingX += dx;
        _pendingY += dy;
        _windowStart = now;
        _windowEnd = now + Math.Max(0, spreadSeconds);
    }

    /// <summary>The share of pending motion due between the last take and <paramref name="now"/>.</summary>
    public (double Dx, double Dy) Take(double now)
    {
        if (!HasPending) return (0, 0);

        var remaining = _windowEnd - _windowStart;
        if (now >= _windowEnd || remaining <= 0) return Flush();
        if (now <= _windowStart) return (0, 0);

        var fraction = (now - _windowStart) / remaining;
        var dx = _pendingX * fraction;
        var dy = _pendingY * fraction;
        _pendingX -= dx;
        _pendingY -= dy;
        _windowStart = now;
        return (dx, dy);
    }

    /// <summary>Everything still pending, immediately. Used before a click so it lands where the cursor is going.</summary>
    public (double Dx, double Dy) Flush()
    {
        var result = (_pendingX, _pendingY);
        _pendingX = 0;
        _pendingY = 0;
        return result;
    }
}
