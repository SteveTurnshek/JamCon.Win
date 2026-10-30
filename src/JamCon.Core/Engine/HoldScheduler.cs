namespace JamCon.Core.Engine;

/// <summary>A scheduled hold action that can be cancelled before it fires.</summary>
public interface IHoldTimer
{
    void Cancel();
}

/// <summary>
/// Schedules hold actions. Implementations must invoke callbacks on the engine's own thread; the
/// engine is not thread-safe. Tests substitute a manual scheduler so holds fire deterministically.
/// </summary>
public interface IHoldScheduler
{
    IHoldTimer Schedule(TimeSpan delay, Action callback);
}
