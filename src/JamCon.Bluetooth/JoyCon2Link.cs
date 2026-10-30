using System.Diagnostics;
using JamCon.Core.Diagnostics;
using JamCon.Core.Protocol;

namespace JamCon.Bluetooth;

public enum JoyCon2LinkState
{
    Stopped,
    BluetoothOff,

    /// <summary>No controller has connected yet this run; waiting for one in SYNC mode.</summary>
    Searching,

    Connecting,
    Streaming,

    /// <summary>A controller was streaming and dropped; waiting for it to come back.</summary>
    Reconnecting,

    /// <summary>Windows has the controller paired, which is known to stop GATT working.</summary>
    PairedConflict,
}

/// <summary>A controller worth remembering across restarts.</summary>
public readonly record struct KnownJoyCon2(ulong Address, JoyCon2Handedness Handedness)
{
    public string DisplayName => JoyCon2Advertisement.DisplayName(Handedness);
}

/// <summary>Point-in-time view of the link, safe to read from any thread.</summary>
public sealed record JoyCon2LinkStatus(
    JoyCon2LinkState State,
    string Detail,
    KnownJoyCon2? Controller,
    double RateHz,
    long Frames,
    int Reconnects,
    DateTimeOffset? StreamingSince,
    string? LastDropReason,
    TimeSpan? LastReconnectDuration,
    JoyCon2Report? LastReport);

/// <summary>
/// Keeps one Joy-Con 2 connected: finds it, connects, initializes, watches for the stream
/// stopping, and reconnects. Every transition is logged, because how Windows behaves across
/// range loss and sleep/wake is still an open question (docs/Windows-Port.md, question 4).
/// </summary>
/// <remarks>
/// <para>
/// Reconnection is advertisement-driven: after a drop, wait for the controller to advertise and
/// connect then, which is what CoreBluetooth's pending connect does for upstream. Direct connection
/// attempts without an advertisement are made only occasionally. Upstream found that the Joy-Con 2
/// firmware "enters a connection cooldown when hosts retry repeatedly", so hammering it would make
/// recovery slower, not faster.
/// </para>
/// <para>
/// The timings below are starting points for measurement, not ported values. Re-derive them from
/// the logs this produces.
/// </para>
/// </remarks>
public sealed class JoyCon2Link : IAsyncDisposable
{
    private static readonly TimeSpan QuietAfterDrop = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AdvertisementWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DirectAttemptInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RadioPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PairedConflictRecheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Service-discovery attempts per connection, about three seconds apart. Short, because the
    /// loop itself retries; the probe uses a long window instead so a person can wake the controller.
    /// </summary>
    private const int SessionDiscoveryAttempts = 3;

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CadenceEstimator _cadence = new();

    private Task? _loop;
    private CancellationTokenSource? _attempt;
    private string? _restartReason;

    private KnownJoyCon2? _known;
    private JoyCon2LinkState _state = JoyCon2LinkState.Stopped;
    private string _detail = "Not started.";
    private long _frames;
    private long _lastFrameTimestamp;
    private JoyCon2Report? _lastReport;
    private int _reconnects;
    private DateTimeOffset? _streamingSince;
    private KnownJoyCon2? _streamingController;
    private DateTimeOffset? _droppedAt;
    private string? _lastDropReason;
    private TimeSpan? _lastReconnectDuration;

    public JoyCon2Link(Action<string> log) => _log = log;

    /// <summary>Raised on every state or detail change, not per frame.</summary>
    public event Action<JoyCon2LinkStatus>? StatusChanged;

    /// <summary>Raised each time a controller reaches streaming, so callers can persist it.</summary>
    public event Action<KnownJoyCon2>? ControllerConnected;

    /// <summary>
    /// A controller began streaming, with its factory stick calibration if one was read. Raised
    /// before the first frame of the new stream is delivered.
    /// </summary>
    public event Action<KnownJoyCon2, JoyCon2StickCalibration?>? StreamStarted;

    /// <summary>
    /// A stream ended, with the reason. Raised after the old session stops delivering frames, so a
    /// consumer can release everything that controller was holding down.
    /// </summary>
    public event Action<KnownJoyCon2, string>? StreamStopped;

    /// <summary>Raised for every input frame while streaming.</summary>
    public event Action<Jc2Frame>? FrameReceived;

    public JoyCon2LinkStatus Status
    {
        get
        {
            lock (_gate) return BuildStatus();
        }
    }

    /// <summary>Starts the connection loop, preferring <paramref name="known"/> if given.</summary>
    public void Start(KnownJoyCon2? known)
    {
        lock (_gate)
        {
            if (_loop is not null) throw new InvalidOperationException("Already started.");
            _known = known;
        }

        _log(known is { } k
            ? $"Link starting. Remembered controller: {k.DisplayName} {Format(k.Address)}."
            : "Link starting. No remembered controller.");
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>
    /// Drops the current connection or search and starts over, e.g. after the PC resumes from
    /// sleep, when the existing WinRT objects may silently be stale.
    /// </summary>
    public void RequestReconnect(string reason)
    {
        lock (_gate)
        {
            _restartReason = reason;
            _attempt?.Cancel();
        }
    }

    /// <summary>Forgets the remembered controller and searches for any Joy-Con 2 instead.</summary>
    public void Forget()
    {
        lock (_gate) _known = null;
        RequestReconnect("remembered controller forgotten");
    }

    private async Task RunAsync(CancellationToken lifetime)
    {
        var failures = 0;
        var lastDirectAttempt = DateTimeOffset.MinValue;

        while (!lifetime.IsCancellationRequested)
        {
            var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            lock (_gate) _attempt = attempt;
            var ct = attempt.Token;

            try
            {
                try
                {
                    var radio = await BluetoothRadioState.CheckAsync();
                    if (!radio.IsReady)
                    {
                        SetState(JoyCon2LinkState.BluetoothOff, radio.Detail);
                        await Task.Delay(RadioPollInterval, ct);
                        continue;
                    }

                    var known = Known;
                    var waiting = HasDropped ? JoyCon2LinkState.Reconnecting : JoyCon2LinkState.Searching;
                    SetState(waiting, known is { } k
                        ? $"Waiting for {k.DisplayName}. Press any button except HOME to wake it."
                        : "Hold SYNC on a Joy-Con 2 until its lights chase.");

                    var target = await FindAsync(known, lastDirectAttempt, ct);
                    if (target is null) continue;
                    if (target.Value.Via == ConnectVia.Direct) lastDirectAttempt = DateTimeOffset.Now;

                    var outcome = await ConnectAndStreamAsync(target.Value.Controller, target.Value.Via, ct);
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.PairedConflict:
                            SetState(JoyCon2LinkState.PairedConflict,
                                $"Windows has {target.Value.Controller.DisplayName} paired, which stops it working. " +
                                "Remove it in Settings > Bluetooth & devices.");
                            await Task.Delay(PairedConflictRecheck, ct);
                            break;

                        case OutcomeKind.Failed:
                            failures++;
                            var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), MaxFailureBackoff.TotalSeconds));
                            _log($"Connection attempt failed ({failures} in a row); next attempt in {backoff.TotalSeconds:F0} s.");
                            SetState(waiting, $"Couldn't connect; retrying in {backoff.TotalSeconds:F0} s.");
                            await Task.Delay(backoff, ct);
                            break;

                        case OutcomeKind.Dropped:
                            failures = 0;
                            RecordDrop(outcome.Reason!);
                            SetState(JoyCon2LinkState.Reconnecting, $"Connection lost: {outcome.Reason}.");
                            await Task.Delay(QuietAfterDrop, ct);
                            break;
                    }
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    string reason;
                    bool wasStreaming;
                    lock (_gate)
                    {
                        reason = _restartReason ?? "restart requested";
                        _restartReason = null;
                        wasStreaming = _state == JoyCon2LinkState.Streaming;
                    }

                    _log($"Restarting: {reason}.");
                    if (wasStreaming) RecordDrop(reason);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let an unexpected WinRT failure kill the loop; log it and go round again.
                    _log($"Unexpected error in connection loop: {ex}");
                    try
                    {
                        await Task.Delay(RadioPollInterval, lifetime);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Clear before disposing so RequestReconnect never cancels a disposed source.
                lock (_gate)
                {
                    if (ReferenceEquals(_attempt, attempt)) _attempt = null;
                }

                attempt.Dispose();
            }
        }

        SetState(JoyCon2LinkState.Stopped, "Stopped.");
    }

    private enum ConnectVia { Advertisement, Direct }

    private readonly record struct Target(KnownJoyCon2 Controller, ConnectVia Via);

    /// <summary>
    /// Waits for the controller to advertise. With a remembered controller and no advertisement,
    /// occasionally tries a direct connection instead, in case it reconnects without advertising
    /// in a form the watcher reports. Returns null to let the loop re-check the radio and go again.
    /// </summary>
    private async Task<Target?> FindAsync(KnownJoyCon2? known, DateTimeOffset lastDirectAttempt, CancellationToken ct)
    {
        using var scanner = new JoyCon2Scanner { WatchAddress = known?.Address };
        var found = new TaskCompletionSource<JoyCon2Discovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.Discovered += d =>
        {
            if (known is null || d.BluetoothAddress == known.Value.Address) found.TrySetResult(d);
        };

        try
        {
            scanner.Start();
        }
        catch (BluetoothUnavailableException ex)
        {
            _log(ex.Message);
            return null;
        }

        try
        {
            var window = Task.Delay(AdvertisementWindow, ct);
            if (await Task.WhenAny(found.Task, window) == found.Task)
            {
                var d = found.Task.Result;
                _log($"Advertisement from {d.DisplayName} {Format(d.BluetoothAddress)} (rssi {d.SignalStrengthDbm} dBm).");
                return new Target(new KnownJoyCon2(d.BluetoothAddress, d.Identity.Handedness), ConnectVia.Advertisement);
            }

            await window; // surfaces cancellation
        }
        finally
        {
            scanner.Stop();
        }

        if (known is { } k && DateTimeOffset.Now - lastDirectAttempt >= DirectAttemptInterval)
        {
            _log($"No advertisement from {k.DisplayName} in {AdvertisementWindow.TotalSeconds:F0} s; trying a direct connection.");
            return new Target(k, ConnectVia.Direct);
        }

        return null;
    }

    private enum OutcomeKind { Failed, PairedConflict, Dropped }

    private readonly record struct Outcome(OutcomeKind Kind, string? Reason = null);

    private async Task<Outcome> ConnectAndStreamAsync(KnownJoyCon2 controller, ConnectVia via, CancellationToken ct)
    {
        SetState(JoyCon2LinkState.Connecting,
            $"Connecting to {controller.DisplayName} (found by {(via == ConnectVia.Advertisement ? "advertisement" : "direct attempt")}).");

        var session = new JoyCon2Session(controller.Address, m => _log($"  session: {m}"), SessionDiscoveryAttempts);
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameReceived += OnFrame;
        session.ConnectionChanged += connected =>
        {
            if (!connected) dropped.TrySetResult("Windows reported the link disconnected");
        };

        try
        {
            bool ok;
            try
            {
                ok = await session.ConnectAsync(controller.Handedness, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Connect threw: {ex.Message}");
                ok = false;
            }

            if (!ok)
            {
                return new Outcome(session.DeviceWasPaired ? OutcomeKind.PairedConflict : OutcomeKind.Failed);
            }

            BeginStreaming(controller, via, session.Calibration);
            ControllerConnected?.Invoke(controller);

            while (true)
            {
                var tick = Task.Delay(250, ct);
                if (await Task.WhenAny(dropped.Task, tick) == dropped.Task)
                {
                    return new Outcome(OutcomeKind.Dropped, dropped.Task.Result);
                }

                await tick; // surfaces cancellation

                if (SinceLastFrame() > StallTimeout)
                {
                    return new Outcome(OutcomeKind.Dropped, $"no reports for {StallTimeout.TotalSeconds:F0} s");
                }
            }
        }
        finally
        {
            session.FrameReceived -= OnFrame;
            await session.DisposeAsync();
        }
    }

    private void BeginStreaming(KnownJoyCon2 controller, ConnectVia via, JoyCon2StickCalibration? calibration)
    {
        string summary;
        lock (_gate)
        {
            _known = controller;
            _frames = 0;
            _lastReport = null;
            _cadence.Reset();
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _streamingSince = DateTimeOffset.Now;

            if (_droppedAt is { } droppedAt)
            {
                _lastReconnectDuration = _streamingSince - droppedAt;
                _reconnects++;
                _droppedAt = null;
                summary = $"Reconnected after {_lastReconnectDuration.Value.TotalSeconds:F1} s " +
                          $"(reconnect #{_reconnects}, via {via}).";
            }
            else
            {
                summary = $"Connected (via {via}).";
            }
        }

        _log(summary);
        StreamStarted?.Invoke(controller, calibration);

        // Open the frame gate only now, so no frame can be delivered ahead of StreamStarted.
        lock (_gate)
        {
            _streamingController = controller;
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
        }

        SetState(JoyCon2LinkState.Streaming, $"{controller.DisplayName} connected.");
    }

    private void RecordDrop(string reason)
    {
        KnownJoyCon2? stopped;
        lock (_gate)
        {
            _droppedAt ??= DateTimeOffset.Now;
            _lastDropReason = reason;
            _streamingSince = null;
            stopped = _streamingController;
            _streamingController = null;
        }

        _log($"Dropped: {reason}.");
        if (stopped is { } controller) StreamStopped?.Invoke(controller, reason);
    }

    private void OnFrame(Jc2Frame frame)
    {
        lock (_gate)
        {
            // Notifications begin inside ConnectAsync, a moment before the stream is announced.
            // Withholding them keeps StreamStarted strictly ahead of the stream's first frame.
            if (_streamingController is null) return;

            _frames++;
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _cadence.Record(frame.TimestampSeconds);
            if (frame.Report is { } report) _lastReport = report;
        }

        FrameReceived?.Invoke(frame);
    }

    private TimeSpan SinceLastFrame()
    {
        lock (_gate) return Stopwatch.GetElapsedTime(_lastFrameTimestamp);
    }

    private KnownJoyCon2? Known
    {
        get
        {
            lock (_gate) return _known;
        }
    }

    private bool HasDropped
    {
        get
        {
            lock (_gate) return _droppedAt is not null;
        }
    }

    private void SetState(JoyCon2LinkState state, string detail)
    {
        JoyCon2LinkStatus snapshot;
        lock (_gate)
        {
            if (_state == state && _detail == detail) return;
            _state = state;
            _detail = detail;
            snapshot = BuildStatus();
        }

        _log($"State: {state}. {detail}");
        StatusChanged?.Invoke(snapshot);
    }

    private JoyCon2LinkStatus BuildStatus() => new(
        _state,
        _detail,
        _known,
        _state == JoyCon2LinkState.Streaming && _cadence.SampleCount > 0 ? _cadence.EstimatedRateHz : 0,
        _frames,
        _reconnects,
        _streamingSince,
        _lastDropReason,
        _lastReconnectDuration,
        _lastReport);

    public static string Format(ulong address) => string.Join(
        ':',
        Enumerable.Range(0, 6).Select(i => ((byte)(address >> ((5 - i) * 8))).ToString("X2")));

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }
}
