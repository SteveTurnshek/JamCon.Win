using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JamCon.Core.Mapping;
using DragAction = JamCon.Core.Mapping.DragAction;
using MouseButton = JamCon.Core.Mapping.MouseButton;

namespace JamCon.App;

/// <summary>
/// Picks one button action: a key combination (captured by pressing it), a click, a Windows or Xbox
/// button, or one of JamCon's own actions. <see cref="Result"/> is set when the user confirms.
/// </summary>
internal sealed class ActionPickerWindow : Window
{
    private enum Kind { None, Key, MouseClick, Windows, Xbox, Drag, Scroll, Radial, NextMode, LevelView, HoldToggle, Repeat }

    private static readonly (Kind Kind, string Label)[] AllKinds =
    [
        (Kind.None, "Nothing"),
        (Kind.Key, "Key or shortcut"),
        (Kind.MouseClick, "Mouse click"),
        (Kind.Windows, "Windows action"),
        (Kind.Xbox, "Xbox controller button"),
        (Kind.HoldToggle, "Shortcut at start and end of the hold (toggle-mode apps)"),
        (Kind.Repeat, "Shortcut repeated while held"),
        (Kind.Drag, "Move the cursor while held (clutch)"),
        (Kind.Scroll, "Scroll with the gyro while held"),
        (Kind.Radial, "Radial menu"),
        (Kind.NextMode, "Next mode"),
        (Kind.LevelView, "Level the view (game mode)"),
    ];

    private static readonly GamepadButtons[] XboxButtons =
        Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None).ToArray();

    private readonly ComboBox _kind = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly (Kind Kind, string Label)[] _kinds;
    private KeyCombo? _combo;
    private readonly CheckBox _win = new() { Content = "Also hold the Windows key", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _capture = new() { IsReadOnly = true, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
    private readonly ComboBox _choice = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Slider _interval = new() { Minimum = 0.1, Maximum = 2, TickFrequency = 0.05, IsSnapToTickEnabled = true, Value = 0.5 };
    private readonly TextBlock _intervalText = new();

    public ButtonAction? Result { get; private set; }

    /// <param name="allowGyroModes">
    /// The clutch, scroll and radial menu work only as a button's tap action, which they take over.
    /// </param>
    public ActionPickerWindow(string title, ButtonAction current, bool allowGyroModes)
    {
#pragma warning disable WPF0001 // Fluent theme; see SettingsWindow.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _kinds = AllKinds.Where(k => allowGyroModes || k.Kind is not (Kind.Drag or Kind.Scroll or Kind.Radial)).ToArray();
        foreach (var (_, label) in _kinds) _kind.Items.Add(label);
        _kind.SelectionChanged += (_, _) => ShowDetail();

        _capture.PreviewKeyDown += OnCaptureKey;
        _capture.GotKeyboardFocus += (_, _) => _capture.Text = _combo is { } c ? c.ToString() : "Press the key or shortcut now…";
        _interval.ValueChanged += (_, _) => _intervalText.Text = $"Every {_interval.Value:0.00} s";

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(12, 5, 12, 5) };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Action" });
        panel.Children.Add(_kind);
        panel.Children.Add(_detail);
        panel.Children.Add(buttons);
        Content = panel;

        Load(current);
    }

    private void Load(ButtonAction current)
    {
        var (kind, combo) = current switch
        {
            KeyPressAction k => (Kind.Key, k.Combo),
            HoldToggleAction t => (Kind.HoldToggle, t.Combo),
            RepeatKeyAction r => (Kind.Repeat, r.Combo),
            MouseClickAction => (Kind.MouseClick, (KeyCombo?)null),
            SystemShortcutAction => (Kind.Windows, null),
            GamepadButtonAction => (Kind.Xbox, null),
            DragAction => (Kind.Drag, null),
            ScrollAction => (Kind.Scroll, null),
            RadialMenuAction => (Kind.Radial, null),
            ToggleModeAction => (Kind.NextMode, null),
            LevelViewAction => (Kind.LevelView, null),
            _ => (Kind.None, null),
        };

        _combo = combo is { } c ? c with { Modifiers = c.Modifiers & ~KeyModifiers.Win } : null;
        _win.IsChecked = combo?.Modifiers.HasFlag(KeyModifiers.Win) == true;
        if (current is RepeatKeyAction repeat) _interval.Value = repeat.IntervalSeconds;
        _intervalText.Text = $"Every {_interval.Value:0.00} s";

        var index = Array.FindIndex(_kinds, k => k.Kind == kind);
        _kind.SelectedIndex = Math.Max(0, index);
        SelectChoice(current);
    }

    private Kind Selected => _kind.SelectedIndex >= 0 ? _kinds[_kind.SelectedIndex].Kind : Kind.None;

    private void ShowDetail()
    {
        _detail.Children.Clear();
        _choice.Items.Clear();
        switch (Selected)
        {
            case Kind.Key or Kind.HoldToggle or Kind.Repeat:
                _detail.Children.Add(new TextBlock { Text = "Click the box, then press the key or shortcut (e.g. Ctrl+Space)" });
                _capture.Text = _combo is { } c ? c.ToString() : "";
                _detail.Children.Add(_capture);
                _detail.Children.Add(_win);
                if (Selected == Kind.Repeat)
                {
                    _detail.Children.Add(new TextBlock { Text = "Repeat", Margin = new Thickness(0, 10, 0, 0) });
                    _detail.Children.Add(_interval);
                    _detail.Children.Add(_intervalText);
                }

                break;
            case Kind.MouseClick:
                foreach (var b in Enum.GetValues<MouseButton>()) _choice.Items.Add($"{b} button");
                _choice.SelectedIndex = 0;
                _detail.Children.Add(_choice);
                break;
            case Kind.Windows:
                foreach (var a in Enum.GetValues<SystemAction>()) _choice.Items.Add(new SystemShortcutAction(a).DisplayName);
                _choice.SelectedIndex = 0;
                _detail.Children.Add(_choice);
                break;
            case Kind.Xbox:
                foreach (var b in XboxButtons) _choice.Items.Add(b.ToString());
                _choice.SelectedIndex = 0;
                _detail.Children.Add(_choice);
                break;
        }
    }

    private void SelectChoice(ButtonAction current)
    {
        _choice.SelectedIndex = current switch
        {
            MouseClickAction m => (int)m.Button,
            SystemShortcutAction s => (int)s.Action,
            GamepadButtonAction g => Math.Max(0, Array.IndexOf(XboxButtons, g.Button)),
            _ => _choice.SelectedIndex,
        };
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // A modifier on its own waits for the key that goes with it. The Windows key opens Start
        // before an app can see it, hence the checkbox instead.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= KeyModifiers.Shift;

        _combo = new KeyCombo((ushort)KeyInterop.VirtualKeyFromKey(key), modifiers);
        _capture.Text = _combo.Value.ToString();
    }

    private void Confirm()
    {
        KeyCombo? Combo() => _combo is { } c
            ? c with { Modifiers = _win.IsChecked == true ? c.Modifiers | KeyModifiers.Win : c.Modifiers }
            : null;

        ButtonAction? result = Selected switch
        {
            Kind.None => ButtonAction.None,
            Kind.Key => Combo() is { } k ? new KeyPressAction(k) : null,
            Kind.HoldToggle => Combo() is { } t ? new HoldToggleAction(t) : null,
            Kind.Repeat => Combo() is { } r ? new RepeatKeyAction(r, Math.Round(_interval.Value, 2)) : null,
            Kind.MouseClick => ButtonAction.Click((MouseButton)Math.Max(0, _choice.SelectedIndex)),
            Kind.Windows => ButtonAction.System((SystemAction)Math.Max(0, _choice.SelectedIndex)),
            Kind.Xbox => ButtonAction.Pad(XboxButtons[Math.Max(0, _choice.SelectedIndex)]),
            Kind.Drag => ButtonAction.Drag,
            Kind.Scroll => ButtonAction.Scroll,
            Kind.Radial => ButtonAction.RadialMenu,
            Kind.NextMode => ButtonAction.ToggleMode,
            _ => ButtonAction.LevelView,
        };

        if (result is null)
        {
            _capture.Text = "Press the key or shortcut first";
            _capture.Focus();
            return;
        }

        Result = result;
        DialogResult = true;
    }
}
