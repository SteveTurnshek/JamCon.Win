using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JamCon.App.Services;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;
using JamCon.Output;

namespace JamCon.App;

/// <summary>
/// The control panel: tuning for each mode, scrolling, sleep, smooth motion and startup. Every
/// change applies at once and is saved; nothing needs a restart. Button remapping is still to come.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly SettingsStore _input;
    private readonly AppSettingsFile _file;
    private readonly JoyCon2Link _link;
    private readonly PacedOutputSink _paced;
    private readonly FileLog _log;
    private readonly TextBlock _mode = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<Action> _refreshers = [];
    private bool _loading;

    public SettingsWindow(SettingsStore input, AppSettingsFile file, JoyCon2Link link, PacedOutputSink paced, FileLog log, Action showStatus)
    {
        _input = input;
        _file = file;
        _link = link;
        _paced = paced;
        _log = log;

#pragma warning disable WPF0001 // Fluent theme: experimental in .NET 9, but it is what makes this look like Windows 11.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "JamCon settings";
        Width = 480;
        Height = 760;
        MinWidth = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };

        panel.Children.Add(new TextBlock { Text = "JamCon", FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_mode);

        Section(panel, "Desktop", "Hold X and point the controller to move the cursor.");
        Slider(panel, "Cursor speed", 5, 150, 1,
            () => _input.Snapshot().Gyro.Sensitivity,
            v => _input.Update(s => s with { Gyro = s.Gyro with { Sensitivity = v } }),
            v => $"{v:0}");
        Slider(panel, "Smoothing", 0, SmoothingLevel.Max, 1,
            () => SmoothingLevel.Of(_input.Snapshot().Gyro),
            v => _input.Update(s => s with { Gyro = SmoothingLevel.Apply(s.Gyro, (int)v) }),
            SmoothingLabel,
            "More smoothing steadies the cursor but adds lag.");

        Section(panel, "Game", "Gyro always aims, like moving a mouse. The stick walks with W A S D.");
        Slider(panel, "Aim speed", 2, 80, 0.5,
            () => _input.Snapshot().GameGyro.Sensitivity / 10.0,
            v => _input.Update(s => s with { GameGyro = s.GameGyro with { Sensitivity = v * 10.0 } }),
            v => $"{v:0.#} counts/°",
            "Mouse counts per degree you turn the controller. Your game's own mouse sensitivity multiplies this.");
        Slider(panel, "Smoothing", 0, SmoothingLevel.Max, 1,
            () => SmoothingLevel.Of(_input.Snapshot().GameGyro),
            v => _input.Update(s => s with { GameGyro = SmoothingLevel.Apply(s.GameGyro, (int)v) }),
            SmoothingLabel);
        Check(panel, "Stick moves with W A S D",
            () => _input.Snapshot().GameStickMovement,
            v => _input.Update(s => s with { GameStickMovement = v }));

        Section(panel, "Scrolling", "In desktop mode the stick scrolls.");
        Slider(panel, "Scroll speed", 1, 30, 0.5,
            () => _input.Snapshot().JoystickScrollSpeed,
            v => _input.Update(s => s with { JoystickScrollSpeed = v }),
            v => $"{v:0.#}");
        Slider(panel, "Scroll acceleration", 1, 6, 0.1,
            () => _input.Snapshot().JoystickScrollAcceleration,
            v => _input.Update(s => s with { JoystickScrollAcceleration = v }),
            v => $"{v:0.0}×",
            "How much faster a full push scrolls than a light one.");

        Section(panel, "Battery and motion");
        Slider(panel, "Sleep when unused for", 0, 60, 1,
            () => _file.IdleSleepMinutes,
            v =>
            {
                _file.SetIdleSleepMinutes((int)v);
                _link.IdleTimeout = v > 0 ? TimeSpan.FromMinutes(v) : null;
            },
            v => v == 0 ? "Never" : $"{v:0} min",
            "Disconnects so the Joy-Con can sleep. Hold SYNC to wake it.");
        Check(panel, "Smooth motion (240 Hz)",
            () => _file.SmoothMotion,
            v =>
            {
                _file.SetSmoothMotion(v);
                _paced.Enabled = v;
            },
            "Spreads the controller's ~67 updates a second over 240, for smooth movement on fast displays. Adds about 7 ms of lag.");

        Section(panel, "Startup");
        Check(panel, "Start JamCon when I sign in to Windows",
            () => StartupRegistration.IsEnabled,
            StartupRegistration.Set);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
        var restore = new Button { Content = "Restore defaults", Padding = new Thickness(14, 6, 14, 6) };
        restore.Click += (_, _) => RestoreDefaults();
        var status = new Button { Content = "Status and log", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        status.Click += (_, _) => showStatus();
        var folder = new Button { Content = "Settings folder", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        folder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\""));
        buttons.Children.Add(restore);
        buttons.Children.Add(status);
        buttons.Children.Add(folder);
        panel.Children.Add(buttons);

        panel.Children.Add(Hint("Buttons can't be remapped here yet. For now, settings.json in the settings folder can be edited while JamCon is closed."));

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Refresh();
        _input.Changed += OnInputChanged;
        Closed += (_, _) =>
        {
            _input.Changed -= OnInputChanged;
            var s = _input.Snapshot();
            _log.Write($"Settings: cursor speed {s.Gyro.Sensitivity:0}, smoothing {SmoothingLevel.Of(s.Gyro)}; " +
                       $"aim {s.GameGyro.Sensitivity / 10.0:0.#} counts/°, smoothing {SmoothingLevel.Of(s.GameGyro)}, WASD {s.GameStickMovement}; " +
                       $"scroll {s.JoystickScrollSpeed:0.#}/{s.JoystickScrollAcceleration:0.0}; sleep {_file.IdleSleepMinutes} min; " +
                       $"smooth motion {_file.SmoothMotion}; start with Windows {StartupRegistration.IsEnabled}.");
        };
    }

    private static string SmoothingLabel(double level) => level switch
    {
        0 => "Off",
        <= 3 => $"{level:0} (light)",
        <= 6 => $"{level:0}",
        _ => $"{level:0} (heavy)",
    };

    private void OnInputChanged(InputSettings settings) => Dispatcher.BeginInvoke(ShowMode);

    private void ShowMode()
    {
        var s = _input.Snapshot();
        var game = s.Mode == InputMode.Game;
        _mode.Text = $"Now in {(game ? "game" : "desktop")} mode. Hold {SwitchButton(s)} on the Joy-Con to switch to {(game ? "desktop" : "game")} mode.";
    }

    /// <summary>The button the current layout uses to switch modes, by name.</summary>
    private string SwitchButton(InputSettings s)
    {
        var handedness = _link.Status.Controller?.Handedness ?? JoyCon2Handedness.Right;
        var profile = s.ButtonsFor(handedness);
        var button = profile.Mappings.FirstOrDefault(m => m.Value.Hold is ToggleModeAction || m.Value.Press is ToggleModeAction);
        return button.Value is null ? "the mode button" : JoyCon2ButtonLayout.DisplayName(button.Key);
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var refresh in _refreshers) refresh();
        }
        finally
        {
            _loading = false;
        }

        ShowMode();
    }

    private void RestoreDefaults()
    {
        _input.Update(s => s with
        {
            Gyro = GyroSettings.Default,
            GameGyro = GyroSettings.GameDefault,
            GameStickMovement = true,
            JoystickScrollSpeed = InputSettings.DefaultJoystickScrollSpeed,
            JoystickScrollAcceleration = InputSettings.DefaultJoystickScrollAcceleration,
        });
        _file.SetIdleSleepMinutes(AppSettingsFile.DefaultIdleSleepMinutes);
        _link.IdleTimeout = TimeSpan.FromMinutes(AppSettingsFile.DefaultIdleSleepMinutes);
        _file.SetSmoothMotion(true);
        _paced.Enabled = true;
        _log.Write("Settings restored to defaults (mode, buttons and startup unchanged).");
        Refresh();
    }

    private static void Section(Panel panel, string title, string? description = null)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 0) });
        if (description is not null) panel.Children.Add(Hint(description));
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private void Slider(
        Panel panel, string label, double min, double max, double step,
        Func<double> read, Action<double> write, Func<double, string> format, string? hint = null)
    {
        var value = new TextBlock { MinWidth = 90, TextAlignment = TextAlignment.Right };
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(new TextBlock { Text = label });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 4, 0, 0),
        };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (!_loading) write(e.NewValue);
        };

        _refreshers.Add(() =>
        {
            slider.Value = Math.Clamp(read(), min, max);
            value.Text = format(slider.Value);
        });

        panel.Children.Add(header);
        panel.Children.Add(slider);
        if (hint is not null) panel.Children.Add(Hint(hint));
    }

    private void Check(Panel panel, string label, Func<bool> read, Action<bool> write, string? hint = null)
    {
        var box = new CheckBox { Content = label, Margin = new Thickness(0, 12, 0, 0) };
        box.Click += (_, _) => write(box.IsChecked == true);
        _refreshers.Add(() => box.IsChecked = read());
        panel.Children.Add(box);
        if (hint is not null) panel.Children.Add(Hint(hint));
    }
}
