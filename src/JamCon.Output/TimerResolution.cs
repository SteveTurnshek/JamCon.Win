namespace JamCon.Output;

/// <summary>
/// Raises the system timer resolution to 1 ms for this object's lifetime.
/// </summary>
/// <remarks>
/// The default 15.6 ms granularity delays timer-driven work by up to a frame, and the gyro smoothing
/// faithfully renders that jitter as cursor wobble (plan, Phase 3). Windows 11 scopes the request to
/// this process, so the cost is contained.
/// </remarks>
public sealed class TimerResolution : IDisposable
{
    private const uint Period = 1;
    private bool _active;

    public TimerResolution() => _active = NativeMethods.TimeBeginPeriod(Period) == 0;

    public bool IsActive => _active;

    public void Dispose()
    {
        if (!_active) return;
        NativeMethods.TimeEndPeriod(Period);
        _active = false;
    }
}
