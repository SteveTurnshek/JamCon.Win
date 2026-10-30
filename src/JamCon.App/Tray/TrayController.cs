using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using JamCon.App.Services;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;

namespace JamCon.App.Tray;

/// <summary>
/// The notification-area icon: shows link state at a glance, carries the menu, and opens the
/// status window. Owns no connection or input logic of its own.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly JoyCon2Link _link;
    private readonly FileLog _log;
    private readonly AppSettingsFile _settingsFile;
    private readonly SettingsStore _input;
    private readonly EngineHost _engine;
    private readonly TaskbarIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _mouseControlItem;
    private readonly MenuItem _forgetItem;
    private (TrayTone Tone, bool Light)? _shownIcon;
    private StatusWindow? _window;

    public TrayController(JoyCon2Link link, FileLog log, AppSettingsFile settingsFile, SettingsStore input, EngineHost engine)
    {
        _link = link;
        _log = log;
        _settingsFile = settingsFile;
        _input = input;
        _engine = engine;

        _statusItem = new MenuItem { IsEnabled = false };

        _mouseControlItem = new MenuItem { Header = "Mouse control", IsCheckable = true };
        _mouseControlItem.Click += (_, _) => SetMouseControl(_mouseControlItem.IsChecked);

        _forgetItem = new MenuItem { Header = "Forget this Joy-Con" };
        _forgetItem.Click += (_, _) =>
        {
            _settingsFile.Forget();
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
        _input.Changed += _ => _icon.Dispatcher.BeginInvoke(Refresh);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var show = new MenuItem { Header = "Show status" };
        show.Click += (_, _) => ShowStatusWindow();

        var recalibrate = new MenuItem { Header = "Recalibrate gyro" };
        recalibrate.Click += (_, _) =>
        {
            _engine.Post(engine => engine.Recalibrate());
            _log.Write("Gyro recalibration requested. Hold the controller still for a moment.");
        };

        var reconnect = new MenuItem { Header = "Reconnect now" };
        reconnect.Click += (_, _) => _link.RequestReconnect("reconnect requested from the tray menu");

        var logs = new MenuItem { Header = "Open log folder" };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.DirectoryPath}\""));

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_mouseControlItem);
        menu.Items.Add(recalibrate);
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

    /// <summary>
    /// The safety switch. Turning it off releases everything held down at once, on the engine
    /// thread, before any further report can be ignored.
    /// </summary>
    private void SetMouseControl(bool enabled)
    {
        _input.Update(s => s with { IsEnabled = enabled });
        if (!enabled) _engine.Post(engine => engine.SetInputEnabled(false));
        _log.Write($"Mouse control turned {(enabled ? "on" : "off")}.");
    }

    private void Refresh()
    {
        var status = _link.Status;
        var settings = _input.Snapshot();
        var summary = Summarize(status);
        var hint = OutputHint(status, settings);

        // The shell truncates notification-area tooltips at 127 characters.
        var tooltip = hint is null ? $"JamCon.Win\n{summary}" : $"JamCon.Win\n{summary}\n{hint}";
        _icon.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        _statusItem.Header = summary;
        _mouseControlItem.IsChecked = settings.IsEnabled;
        _forgetItem.IsEnabled = _settingsFile.Controller is not null;

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

    /// <summary>What the controller does right now, in one line: off, or how to move the cursor.</summary>
    public static string? OutputHint(JoyCon2LinkStatus status, InputSettings settings)
    {
        if (!settings.IsEnabled) return "Mouse control is off";
        if (status.State != JoyCon2LinkState.Streaming || status.Controller is not { } controller) return null;
        if (!settings.CursorControlEnabled) return "Buttons only; cursor control is off";

        var profile = settings.ButtonsFor(controller.Handedness);
        var clutch = profile.Mappings.FirstOrDefault(m => m.Value.Press is JamCon.Core.Mapping.DragAction).Key;
        return profile.HasDragMapping
            ? $"Hold {JoyCon2ButtonLayout.DisplayName(clutch)} to move the cursor"
            : "Point the controller to move the cursor";
    }

    private void ShowStatusWindow()
    {
        _window ??= new StatusWindow(_link, _log, _input);
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
