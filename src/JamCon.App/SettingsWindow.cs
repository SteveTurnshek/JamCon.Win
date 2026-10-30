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
    private readonly IReadOnlyList<JoyCon2Link> _links;
    private readonly PacedOutputSink _paced;
    private readonly FileLog _log;
    private readonly TextBlock _mode = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<Action> _refreshers = [];
    private bool _loading;

    public SettingsWindow(SettingsStore input, AppSettingsFile file, IReadOnlyList<JoyCon2Link> links, PacedOutputSink paced, FileLog log, Action showStatus)
    {
        _input = input;
        _file = file;
        _links = links;
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
        Slider(panel, "Cursor speed", 5, 150, 1, GyroSettings.Default.Sensitivity,
            () => _input.Snapshot().Gyro.Sensitivity,
            v => _input.Update(s => s with { Gyro = s.Gyro with { Sensitivity = v } }),
            v => $"{v:0}");
        Slider(panel, "Smoothing", 0, SmoothingLevel.Max, 1, SmoothingLevel.Of(GyroSettings.Default),
            () => SmoothingLevel.Of(_input.Snapshot().Gyro),
            v => _input.Update(s => s with { Gyro = SmoothingLevel.Apply(s.Gyro, (int)v) }),
            SmoothingLabel,
            "More smoothing steadies the cursor but adds lag.");

        var advanced = new StackPanel();
        panel.Children.Add(new Expander { Header = "Advanced cursor tuning", Content = advanced, Margin = new Thickness(0, 12, 0, 0) });
        advanced.Children.Add(Hint("Upstream JamCon's finer controls, for desktop mode. Game modes stay linear on purpose."));
        Slider(advanced, "Acceleration: maximum boost", 1, 40, 0.5, GyroSettings.Default.SensitivityCap,
            () => _input.Snapshot().Gyro.SensitivityCap,
            v => _input.Update(s => s with { Gyro = s.Gyro with { SensitivityCap = v } }),
            v => v <= 1 ? "Off" : $"{v:0.#}×",
            "How much faster a fast flick moves the cursor than a slow, precise movement.");
        Slider(advanced, "Acceleration: full boost at", 30, 500, 5, GyroSettings.Default.RampSpeed,
            () => _input.Snapshot().Gyro.RampSpeed,
            v => _input.Update(s => s with { Gyro = s.Gyro with { RampSpeed = v } }),
            v => $"{v:0} °/s");
        Slider(advanced, "Acceleration: curve shape", 0.3, 3, 0.05, GyroSettings.Default.CurveExponent,
            () => _input.Snapshot().Gyro.CurveExponent,
            v => _input.Update(s => s with { Gyro = s.Gyro with { CurveExponent = v } }),
            v => $"{v:0.00}",
            "Below 1 the boost comes in early, 1 is even, above 1 it waits for faster motion.");
        Slider(advanced, "Smoothing reactivity", 0, 2, 0.05, GyroSettings.Default.Beta,
            () => _input.Snapshot().Gyro.Beta,
            v => _input.Update(s => s with { Gyro = s.Gyro with { Beta = v } }),
            v => $"{v:0.00}",
            "How quickly smoothing lets go when you move fast. Higher feels snappier, lower steadier.");
        Choice(advanced, "Adaptive smoothing",
            [("Off", AdaptiveSmoothingMode.Off), ("Speed", AdaptiveSmoothingMode.Speed), ("Speed and jerk (default)", AdaptiveSmoothingMode.SpeedAndJerk)],
            () => _input.Snapshot().Gyro.AdaptiveSmoothingMode,
            v => _input.Update(s => s with { Gyro = s.Gyro with { AdaptiveSmoothingMode = v } }));
        Check(advanced, "Re-calibrate drift whenever the controller is held very still",
            () => _input.Snapshot().Gyro.AutoNeutralEnabled,
            v => _input.Update(s => s with { Gyro = s.Gyro with { AutoNeutralEnabled = v } }));
        Check(advanced, "Auto-tune to the measured report rate",
            () => _input.Snapshot().Gyro.AutoTuneSampleRate,
            v => _input.Update(s => s with { Gyro = s.Gyro with { AutoTuneSampleRate = v } }));

        Section(panel, "Mouse & keyboard game", "Gyro always aims, like moving a mouse. The stick walks with W A S D. With both Joy-Cons, play as in a Switch 2 motion-controls game: the right aims, the left walks.");
        Slider(panel, "Aim speed", 2, 80, 0.5, GyroSettings.GameDefault.Sensitivity / 10.0,
            () => _input.Snapshot().GameGyro.Sensitivity / 10.0,
            v => _input.Update(s => s with { GameGyro = s.GameGyro with { Sensitivity = v * 10.0 } }),
            v => $"{v:0.#} counts/°",
            "Mouse counts per degree you turn the controller. Your game's own mouse sensitivity multiplies this.");
        Slider(panel, "Smoothing", 0, SmoothingLevel.Max, 1, SmoothingLevel.Of(GyroSettings.GameDefault),
            () => SmoothingLevel.Of(_input.Snapshot().GameGyro),
            v => _input.Update(s => s with { GameGyro = SmoothingLevel.Apply(s.GameGyro, (int)v) }),
            SmoothingLabel);
        Check(panel, "Stick moves with W A S D",
            () => _input.Snapshot().GameStickMovement,
            v => _input.Update(s => s with { GameStickMovement = v }));
        Slider(panel, "Right stick turn speed (both Joy-Cons)", 60, 720, 10, InputSettings.DefaultGameStickTurnSpeed,
            () => _input.Snapshot().GameStickTurnSpeed,
            v => _input.Update(s => s with { GameStickTurnSpeed = v }),
            v => $"{v:0} °/s",
            "With both Joy-Cons, the right stick turns you left and right for turns too big for your wrist. Y levels the view.");

        Section(panel, "Xbox controller", "Games see a real Xbox controller, with gyro aiming added. Face buttons match by position: the bottom button is A.");
        Choice(panel, "Gyro aims through",
            [("The right stick (works in every game)", ControllerGyroOutput.RightStick), ("The mouse (precise; only games that take both)", ControllerGyroOutput.Mouse)],
            () => _input.Snapshot().ControllerGyroOutput,
            v => _input.Update(s => s with { ControllerGyroOutput = v }));
        Choice(panel, "Gyro aims",
            [("Always", ControllerGyroActivation.Always), ("Only while ZL (left trigger) is held", ControllerGyroActivation.WhileLeftTrigger)],
            () => _input.Snapshot().ControllerGyroActivation,
            v => _input.Update(s => s with { ControllerGyroActivation = v }));
        Slider(panel, "Stick aim: turn speed for a full push", 45, 720, 15, InputSettings.DefaultControllerGyroFullSpeed,
            () => _input.Snapshot().ControllerGyroFullSpeed,
            v => _input.Update(s => s with { ControllerGyroFullSpeed = v }),
            v => $"{v:0} °/s",
            "How fast you must turn the Joy-Con to push the right stick all the way. Lower is more sensitive.");
        Slider(panel, "Stick aim: game's deadzone", 0, 0.4, 0.01, InputSettings.DefaultControllerStickDeadzone,
            () => _input.Snapshot().ControllerStickDeadzone,
            v => _input.Update(s => s with { ControllerStickDeadzone = v }),
            v => $"{v * 100:0}%",
            "Raise it if small, slow turns don't move the view; lower it if the view creeps or jumps when you start turning.");
        Slider(panel, "Mouse aim speed", 2, 80, 0.5, GyroSettings.GameDefault.Sensitivity / 10.0,
            () => _input.Snapshot().ControllerGyro.Sensitivity / 10.0,
            v => _input.Update(s => s with { ControllerGyro = s.ControllerGyro with { Sensitivity = v * 10.0 } }),
            v => $"{v:0.#} counts/°",
            "Used when gyro aims through the mouse.");
        Slider(panel, "Smoothing", 0, SmoothingLevel.Max, 1, SmoothingLevel.Of(GyroSettings.GameDefault),
            () => SmoothingLevel.Of(_input.Snapshot().ControllerGyro),
            v => _input.Update(s => s with { ControllerGyro = SmoothingLevel.Apply(s.ControllerGyro, (int)v) }),
            SmoothingLabel);

        Section(panel, "Scrolling", "In desktop mode the stick scrolls.");
        Check(panel, "Stick scrolls",
            () => _input.Snapshot().JoystickScrollEnabled,
            v => _input.Update(s => s with { JoystickScrollEnabled = v }));
        Slider(panel, "Scroll speed", 1, 30, 0.5, InputSettings.DefaultJoystickScrollSpeed,
            () => _input.Snapshot().JoystickScrollSpeed,
            v => _input.Update(s => s with { JoystickScrollSpeed = v }),
            v => $"{v:0.#}");
        Slider(panel, "Scroll acceleration", 1, 6, 0.1, InputSettings.DefaultJoystickScrollAcceleration,
            () => _input.Snapshot().JoystickScrollAcceleration,
            v => _input.Update(s => s with { JoystickScrollAcceleration = v }),
            v => $"{v:0.0}×",
            "How much faster a full push scrolls than a light one.");

        Section(panel, "Buttons");
        var edit = new Button { Content = "Edit buttons…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        edit.Click += (_, _) => OpenButtonEditor();
        panel.Children.Add(edit);
        panel.Children.Add(Hint("Every button's tap, hold and double-tap actions, for each mode, alone or in a pair."));
        var radial = new Button { Content = "Edit radial menu…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        radial.Click += (_, _) => OpenRadialEditor();
        panel.Children.Add(radial);
        Slider(panel, "Hold time", 0.1, 1.0, 0.05, InputSettings.DefaultHoldThreshold,
            () => _input.Snapshot().HoldThreshold,
            v => _input.Update(s => s with { HoldThreshold = v }),
            v => $"{v:0.00} s",
            "How long a press must last to count as a hold (dictation on Y, switching modes on C, and so on).");
        Slider(panel, "Double-tap window", 0.15, 0.6, 0.05, InputSettings.DefaultDoubleTapWindow,
            () => _input.Snapshot().DoubleTapWindow,
            v => _input.Update(s => s with { DoubleTapWindow = v }),
            v => $"{v:0.00} s",
            "How soon a second tap must follow. Only buttons with a double-tap action (Y) wait for it.");
        Check(panel, "Gyro and sticks active",
            () => _input.Snapshot().CursorControlEnabled,
            v => _input.Update(s => s with { CursorControlEnabled = v }),
            "Off leaves only the buttons working, in every mode.");

        Section(panel, "Battery and motion");
        Slider(panel, "Sleep when unused for", 0, 60, 1, AppSettingsFile.DefaultIdleSleepMinutes,
            () => _file.IdleSleepMinutes,
            v =>
            {
                _file.SetIdleSleepMinutes((int)v);
                foreach (var link in _links) link.IdleTimeout = v > 0 ? TimeSpan.FromMinutes(v) : null;
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

    private ButtonEditorWindow? _buttonEditor;

    private RadialMenuEditorWindow? _radialEditor;

    public void OpenRadialEditor()
    {
        if (_radialEditor is null)
        {
            _radialEditor = new RadialMenuEditorWindow(_input) { Owner = this };
            _radialEditor.Closed += (_, _) => _radialEditor = null;
        }

        _radialEditor.Show();
        _radialEditor.Activate();
    }

    public void OpenButtonEditor()
    {
        if (_buttonEditor is null)
        {
            _buttonEditor = new ButtonEditorWindow(_input) { Owner = this };
            _buttonEditor.Closed += (_, _) => _buttonEditor = null;
        }

        _buttonEditor.Show();
        _buttonEditor.Activate();
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
        _mode.Text = $"Now in {s.Mode.DisplayName().ToLowerInvariant()}. Hold {SwitchButton(s)} on the Joy-Con to change mode: desktop, then mouse & keyboard game, then Xbox controller.";
    }

    /// <summary>The button the current layout uses to switch modes, by name.</summary>
    private string SwitchButton(InputSettings s)
    {
        // The right Joy-Con's button when it is connected (a pair switches with it too), else the left's.
        var connected = _links.Where(l => l.Status.State == JoyCon2LinkState.Streaming).Select(l => l.Side).ToList();
        var handedness = connected.Count == 1 ? connected[0] : JoyCon2Handedness.Right;
        var profile = s.ButtonsFor(handedness, paired: connected.Count == 2);
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
            GameStickTurnSpeed = InputSettings.DefaultGameStickTurnSpeed,
            ControllerGyro = GyroSettings.GameDefault,
            ControllerGyroOutput = ControllerGyroOutput.RightStick,
            ControllerGyroActivation = ControllerGyroActivation.Always,
            ControllerGyroFullSpeed = InputSettings.DefaultControllerGyroFullSpeed,
            ControllerStickDeadzone = InputSettings.DefaultControllerStickDeadzone,
            HoldThreshold = InputSettings.DefaultHoldThreshold,
            DoubleTapWindow = InputSettings.DefaultDoubleTapWindow,
            CursorControlEnabled = true,
            JoystickScrollEnabled = true,
            JoystickScrollSpeed = InputSettings.DefaultJoystickScrollSpeed,
            JoystickScrollAcceleration = InputSettings.DefaultJoystickScrollAcceleration,
        });
        _file.SetIdleSleepMinutes(AppSettingsFile.DefaultIdleSleepMinutes);
        foreach (var link in _links) link.IdleTimeout = TimeSpan.FromMinutes(AppSettingsFile.DefaultIdleSleepMinutes);
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
        Panel panel, string label, double min, double max, double step, double defaultValue,
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

        var reset = SliderReset.For(slider, defaultValue, format);
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Insert(0, reset);

        _refreshers.Add(() =>
        {
            slider.Value = Math.Clamp(read(), min, max);
            value.Text = format(slider.Value);
        });

        panel.Children.Add(header);
        panel.Children.Add(slider);
        if (hint is not null) panel.Children.Add(Hint(hint));
    }

    private void Choice<T>(Panel panel, string label, (string Label, T Value)[] options, Func<T> read, Action<T> write)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 0) });
        var box = new ComboBox { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (text, _) in options) box.Items.Add(text);
        box.SelectionChanged += (_, _) =>
        {
            if (!_loading && box.SelectedIndex >= 0) write(options[box.SelectedIndex].Value);
        };
        _refreshers.Add(() => box.SelectedIndex = Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, read())));
        panel.Children.Add(box);
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
