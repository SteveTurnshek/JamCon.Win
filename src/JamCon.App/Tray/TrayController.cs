using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using JamCon.App.Services;
using JamCon.Bluetooth;

namespace JamCon.App.Tray;

/// <summary>
/// The notification-area icon: shows link state at a glance, carries the menu, and opens the
/// status window. Owns no connection logic; it only reads <see cref="JoyCon2Link.Status"/>.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly JoyCon2Link _link;
    private readonly FileLog _log;
    private readonly SettingsStore _settings;
    private readonly TaskbarIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _forgetItem;
    private (TrayTone Tone, bool Light)? _shownIcon;
    private StatusWindow? _window;

    public TrayController(JoyCon2Link link, FileLog log, SettingsStore settings)
    {
        _link = link;
        _log = log;
        _settings = settings;

        _statusItem = new MenuItem { IsEnabled = false };
        _forgetItem = new MenuItem { Header = "Forget this Joy-Con" };
        _forgetItem.Click += (_, _) =>
        {
            _settings.Forget();
            _link.Forget();
        };

        _icon = new TaskbarIcon
        {
            ContextMenu = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowStatusWindow();

        Refresh();

        // Efficiency Mode (EcoQoS) is H.NotifyIcon's default here and would throttle a process
        // that has to keep up with 67 reports a second, so it is explicitly declined.
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _link.StatusChanged += _ => _icon.Dispatcher.BeginInvoke(Refresh);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var show = new MenuItem { Header = "Show status" };
        show.Click += (_, _) => ShowStatusWindow();

        var reconnect = new MenuItem { Header = "Reconnect now" };
        reconnect.Click += (_, _) => _link.RequestReconnect("reconnect requested from the tray menu");

        var logs = new MenuItem { Header = "Open log folder" };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.DirectoryPath}\""));

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(show);
        menu.Items.Add(reconnect);
        menu.Items.Add(_forgetItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(logs);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void Refresh()
    {
        var status = _link.Status;
        var summary = Summarize(status);

        // The shell truncates notification-area tooltips at 127 characters.
        var tooltip = $"JamCon.Win\n{summary}";
        _icon.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        _statusItem.Header = summary;
        _forgetItem.IsEnabled = _settings.Controller is not null;

        var wanted = (TrayIconRenderer.ToneFor(status.State), TrayIconRenderer.TaskbarIsLight());
        if (_shownIcon != wanted)
        {
            _icon.Icon = TrayIconRenderer.Get(wanted.Item1, wanted.Item2);
            _shownIcon = wanted;
        }
    }

    public static string Summarize(JoyCon2LinkStatus status)
    {
        var name = status.Controller?.DisplayName ?? "Joy-Con 2";
        return status.State switch
        {
            JoyCon2LinkState.Streaming => $"{name}: connected, {status.RateHz:F1} Hz",
            JoyCon2LinkState.Connecting => $"Connecting to {name}…",
            JoyCon2LinkState.Searching => "Hold SYNC on a Joy-Con 2",
            JoyCon2LinkState.Reconnecting => $"Reconnecting to {name}…",
            JoyCon2LinkState.BluetoothOff => "Bluetooth is off",
            JoyCon2LinkState.PairedConflict => $"Remove {name} in Windows Bluetooth settings",
            _ => "Stopped",
        };
    }

    private void ShowStatusWindow()
    {
        _window ??= new StatusWindow(_link, _log);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.CloseForExit();
        _icon.Dispose();
    }
}
