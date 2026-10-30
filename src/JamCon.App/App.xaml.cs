using System.Windows;
using JamCon.App.Services;
using JamCon.App.Tray;
using JamCon.Bluetooth;
using Microsoft.Win32;

namespace JamCon.App;

/// <summary>
/// Tray-only application: no main window, one instance per user session. Starts the Joy-Con 2
/// link, shows its state in the notification area, and restarts the link after the PC resumes.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;
    private FileLog? _log;
    private JoyCon2Link? _link;
    private TrayController? _tray;

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

        var settings = new SettingsStore(AppPaths.SettingsFile, _log.Write);
        _link = new JoyCon2Link(_log.Write);
        _link.ControllerConnected += settings.Remember;

        _tray = new TrayController(_link, _log, settings);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _link.Start(settings.Controller);
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

        _log?.Write("JamCon.Win exiting.");
        _log?.Dispose();

        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
