using System.Windows;
using JamCon.App.Overlay;
using JamCon.App.Services;
using JamCon.App.Tray;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Protocol;
using JamCon.Core.Output;
using JamCon.Output;
using Microsoft.Win32;

namespace JamCon.App;

/// <summary>
/// Tray-only application: no main window, one instance per user session. Keeps the Joy-Con 2
/// connected, turns its input into mouse and keyboard output, and shows state in the tray.
/// </summary>
/// <remarks>
/// Data flow: <see cref="JoyCon2Link"/> raises frames on WinRT threads → <see cref="EngineHost"/>
/// queues them onto its single engine thread → <see cref="InputEngine"/> → SendInput.
/// </remarks>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;
    private FileLog? _log;
    private AppSettingsFile? _settingsFile;
    private SettingsStore? _input;
    private TimerResolution? _timerResolution;
    private SendInputOutputSink? _output;
    private PacedOutputSink? _pacedOutput;
    private RadialMenuOverlay? _radialOverlay;
    private ModeToast? _modeToast;
    private EngineHost? _engine;
    private JoyCon2Link[] _links = [];
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--preview-radial-menu"))
        {
            PreviewRadialMenu();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\JamCon.Win.SingleInstance", out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            MessageBox.Show(
                "JamCon.Win is already running. Look for its icon in the notification area.",
                "JamCon.Win",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _log = new FileLog(AppPaths.LogDirectory);
        _log.Write($"JamCon.Win starting. Windows {Environment.OSVersion.Version}. Log: {_log.FilePath}");

        // A tray app has no console; without these, a crash during a hardware test leaves no trace.
        // A broken window must not take the controller down with it: the engine runs on its own
        // thread, so the app keeps working after a UI failure.
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write($"Unhandled UI exception (app kept running): {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Write($"Unhandled exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        _log.Write(StartMenuShortcut.Ensure());
        if (StartupRegistration.Refresh() is { } startup) _log.Write(startup);

        _settingsFile = new AppSettingsFile(AppPaths.SettingsFile, _log.Write);
        _input = new SettingsStore(_settingsFile.Input);
        _input.Changed += _settingsFile.SaveInput;

        // A tray app is a background process to Windows, which may otherwise throttle its threads
        // and ignore its timer-resolution request; both show up as cursor jitter.
        _log.Write(ProcessQualityOfService.OptOutOfPowerThrottling()
            ? "Opted out of Windows power throttling."
            : "Windows refused the power-throttling opt-out; timing may be coarser in the background.");

        _timerResolution = new TimerResolution();
        _log.Write(_timerResolution.IsActive
            ? "Timer resolution raised to 1 ms."
            : "Could not raise timer resolution; cursor timing may be coarser.");

        _output = new SendInputOutputSink(_log.Write);
        // Always in the chain; the Smooth motion setting switches it on and off live.
        _pacedOutput = new PacedOutputSink(_output) { Enabled = _settingsFile.SmoothMotion };
        _log.Write(_settingsFile.SmoothMotion
            ? "Smooth motion on: controller motion is re-emitted at 240 Hz."
            : "Smooth motion off: motion is emitted as each report arrives.");

        _radialOverlay = new RadialMenuOverlay();
        _modeToast = new ModeToast();
        _engine = new EngineHost(_pacedOutput, _input, _log.Write, new RadialMenuPresenter(_radialOverlay, _log.Write));
        _engine.Engine.ModeToggleRequested += ToggleMode;
        _log.Write($"Mode: {_input.Snapshot().Mode}.");

        // One link per hand, so a pair connects as two independent links and either can come and go.
        _links = [CreateLink(JoyCon2Handedness.Right, _engine), CreateLink(JoyCon2Handedness.Left, _engine)];
        _log.Write(_settingsFile.IdleSleepMinutes > 0
            ? $"Idle sleep after {_settingsFile.IdleSleepMinutes} min without use."
            : "Idle sleep is off.");

        _tray = new TrayController(_links, _log, _settingsFile, _input, _engine, _pacedOutput);
        if (e.Args.Contains("--show-status")) _tray.ShowStatusWindow(); // dev aids: the same paths as the tray
        if (e.Args.Contains("--show-settings")) _tray.ShowSettingsWindow();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _log.Write($"Mouse control is {(_input.Snapshot().IsEnabled ? "on" : "off")}. Settings: {AppPaths.SettingsFile}");
        foreach (var link in _links) link.Start(_settingsFile.ControllerFor(link.Side));
    }

    private JoyCon2Link CreateLink(JoyCon2Handedness side, EngineHost engine)
    {
        var prefix = side == JoyCon2Handedness.Right ? "R" : "L";
        var link = new JoyCon2Link(m => _log!.Write($"{prefix}: {m}"), side)
        {
            IdleTimeout = _settingsFile!.IdleSleepMinutes > 0 ? TimeSpan.FromMinutes(_settingsFile.IdleSleepMinutes) : null,
        };
        link.ControllerConnected += _settingsFile.Remember;

        // The link guarantees StreamStarted precedes the stream's first frame and StreamStopped follows
        // its last, so queue order alone keeps the engine's view of the device consistent.
        string? streamingDeviceId = null;
        link.StreamStarted += (controller, calibration) =>
        {
            var id = JoyCon2Link.Format(controller.Address);
            Volatile.Write(ref streamingDeviceId, id);
            engine.Post(e => e.DeviceConnected(id, controller.Handedness, calibration));
        };
        link.StreamStopped += (controller, _) =>
        {
            var id = JoyCon2Link.Format(controller.Address);
            engine.Post(e => e.DeviceDisconnected(id));
        };
        link.FrameReceived += frame =>
        {
            if (frame.Report is not { } report || Volatile.Read(ref streamingDeviceId) is not { } id) return;
            engine.Post(e => e.ProcessFrame(id, report, frame.TimestampSeconds));
        };

        return link;
    }

    /// <summary>
    /// Development aid: opens the radial menu at the cursor and sweeps the dot once around it, so the
    /// overlay's drawing can be checked without a controller. Takes no single-instance lock and
    /// touches no input.
    /// </summary>
    private void PreviewRadialMenu()
    {
        var config = Core.Mapping.RadialMenuConfiguration.Default;
        var overlay = new RadialMenuOverlay();
        overlay.Open(config);

        var started = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.Now - started).TotalSeconds;
            if (t > 4.5)
            {
                timer.Stop();
                overlay.Close(null);
                Shutdown();
                return;
            }

            var angle = t / 4.0 * 2 * Math.PI - Math.PI / 2;
            var (x, y, selection) = Core.Mapping.RadialMenuGeometry.Resolve(Math.Cos(angle) * 75, Math.Sin(angle) * 75, config);
            overlay.Move(x, y, selection);
        };
        timer.Start();
    }

    /// <summary>
    /// Raised on the engine thread when the mode button is held. The engine sees the new mode on its
    /// next frame and releases anything held in the old one.
    /// </summary>
    private void ToggleMode()
    {
        _input!.Update(s => s with { Mode = s.Mode == InputMode.Game ? InputMode.Desktop : InputMode.Game });
        var mode = _input.Snapshot().Mode;
        _log!.Write($"Switched to {mode} mode.");
        Dispatcher.BeginInvoke(() => _modeToast?.Flash(mode == InputMode.Game ? "Game mode" : "Desktop mode"));
    }

    /// <summary>
    /// After resume the radio has been powered down, so existing WinRT device objects may be stale
    /// without reporting it. Restart the link rather than trust them. Suspend is logged too, so the
    /// log shows how long each sleep lasted and what the controller did across it.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _log?.Write($"Power event: {e.Mode}.");
        if (e.Mode != PowerModes.Resume) return;
        foreach (var link in _links) link.RequestReconnect("PC resumed from sleep");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray?.Dispose();

        // Bounded: never hang exit on a WinRT call that will not return.
        Task.WaitAll(_links.Select(l => l.DisposeAsync().AsTask()).ToArray(), TimeSpan.FromSeconds(5));

        // Releases every key and button JamCon still holds, then stops the engine thread.
        _engine?.Dispose();
        _pacedOutput?.Dispose();
        _radialOverlay?.Close();
        _modeToast?.Close();
        _timerResolution?.Dispose();

        _log?.Write("JamCon.Win exiting.");
        _log?.Dispose();

        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
