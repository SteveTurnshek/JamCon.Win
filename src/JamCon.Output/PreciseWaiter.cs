using System.Diagnostics;
using static JamCon.Output.NativeMethods;

namespace JamCon.Output;

/// <summary>
/// Sub-millisecond waits using a high-resolution waitable timer.
/// </summary>
/// <remarks>
/// <c>Thread.Sleep(1)</c> only means 1 ms while the process's timer-resolution request is honored,
/// and Windows 11 may stop honoring it for a process whose windows are not visible, which describes
/// a tray app nearly all the time. Sleeps then quietly become 15.6 ms. A high-resolution waitable
/// timer (Windows 10 1803+) is precise regardless. Falls back to 1 ms sleeps if one cannot be made.
/// </remarks>
public sealed class PreciseWaiter : IDisposable
{
    private nint _timer = CreateWaitableTimerEx(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);

    public bool IsHighResolution => _timer != 0;

    /// <summary>Waits until <paramref name="deadline"/>, a <see cref="Stopwatch"/> timestamp.</summary>
    public void WaitUntil(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;

        if (_timer != 0)
        {
            // Negative due time means relative, in 100 ns units.
            var dueTime = -Math.Max(1, remaining * 10_000_000 / Stopwatch.Frequency);
            if (SetWaitableTimer(_timer, in dueTime, 0, 0, 0, false))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
        }

        while (Stopwatch.GetTimestamp() < deadline) Thread.Sleep(1);
    }

    public void Dispose()
    {
        if (_timer == 0) return;
        CloseHandle(_timer);
        _timer = 0;
    }
}

/// <summary>Process-wide opt-outs that matter for an input tool running from the tray.</summary>
public static class ProcessQualityOfService
{
    /// <summary>
    /// Opts out of Windows power throttling: EcoQoS, which can run a background process's threads
    /// slowly on efficiency cores, and the Windows 11 rule that ignores timer-resolution requests
    /// from processes with no visible window. Both would show up as cursor jitter.
    /// </summary>
    /// <returns>False if Windows refused, which leaves default behaviour.</returns>
    public static bool OptOutOfPowerThrottling()
    {
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution,
            StateMask = 0, // controlled, and off: never throttle, always honor timer resolution
        };

        return SetProcessInformation(
            GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());
    }
}
