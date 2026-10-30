using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using JamCon.App.Services;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;
using JamCon.Output;

namespace JamCon.App.Tray;

/// <summary>
/// The notification-area icon: shows link state at a glance, carries the menu, and opens the
/// settings window (left click) and status window. Owns no connection or input logic of its own.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly IReadOnlyList<JoyCon2Link> _links;
    private readonly FileLog _log;
    private readonly AppSettingsFile _settingsFile;
    private readonly SettingsStore _input;
    private readonly EngineHost _engine;
    private readonly PacedOutputSink _pacedOutput;
    private readonly TaskbarIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _mouseControlItem;
    private readonly MenuItem _forgetItem;
    private readonly MenuItem _sleepItem;
    private (TrayTone Tone, bool Light)? _shownIcon;
    private StatusWindow? _window;
    private SettingsWindow? _settingsWindow;

    public TrayController(
        IReadOnlyList<JoyCon2Link> links, FileLog log, AppSettingsFile settingsFile, SettingsStore input, EngineHost engine, PacedOutputSink pacedOutput)
    {
        _pacedOutput = pacedOutput;
        _links = links;
        _log = log;
        _settingsFile = settingsFile;
        _input = input;
        _engine = engine;

        _statusItem = new MenuItem { IsEnabled = false };

        _mouseControlItem = new MenuItem { Header = "Mouse control", IsCheckable = true };
        _mouseControlItem.Click += (_, _) => SetMouseControl(_mouseControlItem.IsChecked);

        _forgetItem = new MenuItem { Header = "Forget remembered Joy-Cons" };

        _sleepItem = new MenuItem { Header = "Put Joy-Cons to sleep" };
        _sleepItem.Click += (_, _) =>
        {
            foreach (var link in _links) link.SleepNow();
        };
        _forgetItem.Click += (_, _) =>
        {
            _settingsFile.Forget();
            foreach (var link in _links) link.Forget();
        };

        _icon = new TaskbarIcon
        {
            ContextMenu = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowSettingsWindow();

        Refresh();

        // Efficiency Mode (EcoQoS) is H.NotifyIcon's default here and would throttle a process
        // that has to keep up with 67 reports a second, so it is explicitly declined.
        _icon.ForceCreate(enablesEfficiencyMode: false);

        foreach (var link in _links) link.StatusChanged += _ => _icon.Dispatcher.BeginInvoke(Refresh);
        _input.Changed += _ => _icon.Dispatcher.BeginInvoke(Refresh);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private ContextMenu BuildMenu()
    {
        var settings = new MenuItem { Header = "Settings…", FontWeight = FontWeights.SemiBold };
        settings.Click += (_, _) => ShowSettingsWindow();

        var show = new MenuItem { Header = "Show status" };
        show.Click += (_, _) => ShowStatusWindow();

        var recalibrate = new MenuItem { Header = "Recalibrate gyro" };
        recalibrate.Click += (_, _) =>
        {
            _engine.Post(engine => engine.Recalibrate());
            _log.Write("Gyro recalibration requested. Hold the controller still for a moment.");
        };

        var reconnect = new MenuItem { Header = "Reconnect now" };
        reconnect.Click += (_, _) =>
        {
            foreach (var link in _links) link.RequestReconnect("reconnect requested from the tray menu");
        };

        var logs = new MenuItem { Header = "Open log folder" };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.DirectoryPath}\""));

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(_mouseControlItem);
        menu.Items.Add(recalibrate);
        menu.Items.Add(new Separator());
        menu.Items.Add(show);
        menu.Items.Add(reconnect);
        menu.Items.Add(_sleepItem);
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
        var statuses = Statuses();
        var settings = _input.Snapshot();
        var summary = Summarize(statuses);
        var hint = OutputHint(statuses, settings);

        // The shell truncates notification-area tooltips at 127 characters.
        var tooltip = hint is null ? $"JamCon.Win\n{summary}" : $"JamCon.Win\n{summary}\n{hint}";
        _icon.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        _statusItem.Header = summary;
        _mouseControlItem.IsChecked = settings.IsEnabled;
        _forgetItem.IsEnabled = _settingsFile.HasControllers;
        _sleepItem.IsEnabled = statuses.Any(s => s.State == JoyCon2LinkState.Streaming);

        var wanted = (TrayIconRenderer.ToneFor(Primary(statuses).State), TrayIconRenderer.TaskbarIsLight());
        if (_shownIcon != wanted)
        {
            _icon.Icon = TrayIconRenderer.Get(wanted.Item1, wanted.Item2);
            _shownIcon = wanted;
        }
    }

    private JoyCon2LinkStatus[] Statuses() => _links.Select(l => l.Status).ToArray();

    /// <summary>The link that best represents the app: a streaming one, else one with a remembered controller.</summary>
    public static JoyCon2LinkStatus Primary(IReadOnlyList<JoyCon2LinkStatus> statuses) =>
        statuses.FirstOrDefault(s => s.State == JoyCon2LinkState.Streaming)
        ?? statuses.FirstOrDefault(s => s.Controller is not null)
        ?? statuses[0];

    /// <summary>
    /// One line for both hands. A side with no remembered controller is left out unless neither has
    /// one, so a lone right Joy-Con does not read as a missing left one.
    /// </summary>
    public static string Summarize(IReadOnlyList<JoyCon2LinkStatus> statuses)
    {
        if (statuses.Count(s => s.State == JoyCon2LinkState.Streaming) == 2)
        {
            return $"Both Joy-Cons connected, {statuses.Min(s => s.RateHz):F1} Hz";
        }

        var known = statuses.Where(s => s.Controller is not null || s.State == JoyCon2LinkState.Streaming).Select(Summarize).ToList();
        return known.Count == 0 ? Summarize(statuses[0]) : string.Join("; ", known);
    }

    public static string Summarize(JoyCon2LinkStatus status)
    {
        var name = status.Controller?.DisplayName ?? "Joy-Con 2";
        return status.State switch
        {
            JoyCon2LinkState.Streaming => $"{name}: connected, {status.RateHz:F1} Hz",
            JoyCon2LinkState.Connecting => $"Connecting to {name}…",
            JoyCon2LinkState.Searching => status.Controller is null ? "Hold SYNC on a Joy-Con 2" : $"Waiting for {name}",
            JoyCon2LinkState.Reconnecting => $"Reconnecting to {name}…",
            JoyCon2LinkState.BluetoothOff => "Bluetooth is off",
            JoyCon2LinkState.PairedConflict => $"Remove {name} in Windows Bluetooth settings",
            JoyCon2LinkState.Asleep => $"{name} is asleep; hold SYNC to wake it",
            _ => "Stopped",
        };
    }

    /// <summary>What the controllers do right now, in one line: off, or how to move the cursor.</summary>
    public static string? OutputHint(IReadOnlyList<JoyCon2LinkStatus> statuses, InputSettings settings)
    {
        if (!settings.IsEnabled) return "Mouse control is off";
        var connected = statuses
            .Where(s => s.State == JoyCon2LinkState.Streaming && s.Controller is not null)
            .Select(s => s.Controller!.Value)
            .ToList();
        if (connected.Count == 0) return null;
        if (!settings.CursorControlEnabled) return "Buttons only; cursor control is off";

        // A pair aims with the right Joy-Con.
        var paired = connected.Count == 2;
        var aiming = connected.FirstOrDefault(c => c.Handedness == JoyCon2Handedness.Right, connected[0]);
        var profile = settings.ButtonsFor(aiming.Handedness, paired);
        if (settings.Mode == InputMode.Game)
        {
            var toggle = profile.Mappings.FirstOrDefault(m => m.Value.Hold is ToggleModeAction).Key;
            var back = $"Hold {JoyCon2ButtonLayout.DisplayName(toggle)} for desktop";
            return paired ? $"Game mode: right Joy-Con aims, Y levels the view. {back}" : $"Game mode: gyro aims. {back}";
        }

        var clutch = profile.Mappings.FirstOrDefault(m => m.Value.Press is JamCon.Core.Mapping.DragAction).Key;
        return profile.HasDragMapping
            ? $"Hold {JoyCon2ButtonLayout.DisplayName(clutch)} to move the cursor"
            : "Point the controller to move the cursor";
    }

    public void ShowSettingsWindow()
    {
        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(_input, _settingsFile, _links, _pacedOutput, _log, ShowStatusWindow);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _log.Write($"Couldn't open the settings window: {ex}");
            _settingsWindow = null;
        }
    }

    public void ShowStatusWindow()
    {
        try
        {
            _window ??= new StatusWindow(_links, _log, _input);
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }
        catch (Exception ex)
        {
            _log.Write($"Couldn't open the status window: {ex}");
            _window = null; // a half-built window is not reused
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.CloseForExit();
        _settingsWindow?.Close();
        _icon.Dispose();
    }
}
