using System.Collections.Concurrent;
using JamCon.Core.Output;

namespace JamCon.Core.Engine;

/// <summary>
/// Owns the engine's thread. Frames, hold timers, connection changes and settings changes all
/// arrive here and run one at a time, which is what lets <see cref="InputEngine"/> be lock-free.
/// The equivalent of upstream's serial <c>engineQueue</c>.
/// </summary>
/// <remarks>
/// The thread runs above normal priority, as the plan's Phase 3 notes require: input that waits
/// behind ordinary work shows up as cursor wobble once the gyro smoothing renders it faithfully.
/// Timer resolution (timeBeginPeriod) is a Win32 concern and is set by the output layer.
/// </remarks>
public sealed class EngineHost : IHoldScheduler, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Action<string> _log;

    public EngineHost(IOutputSink output, SettingsStore settings, Action<string> log)
    {
        _log = log;
        Engine = new InputEngine(output, this, settings.Snapshot);
        _thread = new Thread(Run)
        {
            Name = "JamCon engine",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Only touch this from inside <see cref="Post"/>.</summary>
    public InputEngine Engine { get; }

    public void Post(Action<InputEngine> work)
    {
        if (_queue.IsAddingCompleted) return;
        try
        {
            _queue.Add(() => work(Engine));
        }
        catch (InvalidOperationException)
        {
            // Completed between the check and the add, i.e. shutting down. Dropping is correct.
        }
    }

    IHoldTimer IHoldScheduler.Schedule(TimeSpan delay, Action callback) => new QueuedTimer(this, delay, callback);

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // One bad frame must never take down input for the rest of the session.
                _log($"Engine error: {ex}");
            }
        }
    }

    /// <summary>Releases everything held, then stops the thread.</summary>
    public void Dispose()
    {
        Post(engine => engine.Stop());
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(2))) _log("Engine thread did not stop within 2 s.");
        _queue.Dispose();
    }

    /// <summary>Fires on a pool thread, then hops onto the engine thread to run the callback.</summary>
    private sealed class QueuedTimer : IHoldTimer
    {
        private readonly Timer _timer;
        private int _cancelled;

        public QueuedTimer(EngineHost host, TimeSpan delay, Action callback)
        {
            _timer = new Timer(_ =>
            {
                if (Volatile.Read(ref _cancelled) == 0) host.Post(_ =>
                {
                    if (Volatile.Read(ref _cancelled) == 0) callback();
                });
            }, null, delay, Timeout.InfiniteTimeSpan);
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            _timer.Dispose();
        }
    }
}
