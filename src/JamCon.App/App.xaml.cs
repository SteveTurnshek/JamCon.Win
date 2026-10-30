using System.Windows;
using JamCon.App.Services;
using JamCon.App.Tray;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
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
    private EngineHost? _engine;
    private JoyCon2Link? _link;
    private TrayController? _tray;
    private string? _streamingDeviceId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        DispatcherUnhandledException += (_, args) => _log.Write($"Unhandled UI exception: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Write($"Unhandled exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        _settingsFile = new AppSettingsFile(AppPaths.SettingsFile, _log.Write);
        _input = new SettingsStore(_settingsFile.Input);
        _input.Changed += _settingsFile.SaveInput;

        _timerResolution = new TimerResolution();
        _log.Write(_timerResolution.IsActive
            ? "Timer resolution raised to 1 ms."
            : "Could not raise timer resolution; cursor timing may be coarser.");

        _output = new SendInputOutputSink(_log.Write);
        _engine = new EngineHost(_output, _input, _log.Write);

        _link = new JoyCon2Link(_log.Write);
        _link.ControllerConnected += _settingsFile.Remember;
        // The link guarantees StreamStarted precedes the stream's first frame and StreamStopped follows
        // its last, so queue order alone keeps the engine's view of the device consistent.
        _link.StreamStarted += (controller, calibration) =>
        {
            var id = JoyCon2Link.Format(controller.Address);
            Volatile.Write(ref _streamingDeviceId, id);
            _engine.Post(engine => engine.DeviceConnected(id, controller.Handedness, calibration));
        };
        _link.StreamStopped += (controller, _) =>
        {
            var id = JoyCon2Link.Format(controller.Address);
            _engine.Post(engine => engine.DeviceDisconnected(id));
        };
        _link.FrameReceived += frame =>
        {
            if (frame.Report is not { } report || Volatile.Read(ref _streamingDeviceId) is not { } id) return;
            _engine.Post(engine => engine.ProcessFrame(id, report, frame.TimestampSeconds));
        };

        _tray = new TrayController(_link, _log, _settingsFile, _input, _engine);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _log.Write($"Mouse control is {(_input.Snapshot().IsEnabled ? "on" : "off")}. Settings: {AppPaths.SettingsFile}");
        _link.Start(_settingsFile.Controller);
    }

    /// <summary>
    /// After resume the radio has been powered down, so existing WinRT device objects may be stale
    /// without reporting it. Restart the link rather than trust them. Suspend is logged too, so the
    /// log shows how long each sleep lasted and what the controller did across it.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _log?.Write($"Power event: {e.Mode}.");
        if (e.Mode == PowerModes.Resume) _link?.RequestReconnect("PC resumed from sleep");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray?.Dispose();

        // Bounded: never hang exit on a WinRT call that will not return.
        _link?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));

        // Releases every key and button JamCon still holds, then stops the engine thread.
        _engine?.Dispose();
        _timerResolution?.Dispose();

        _log?.Write("JamCon.Win exiting.");
        _log?.Dispose();

        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
