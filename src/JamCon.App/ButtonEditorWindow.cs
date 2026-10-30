using System.Windows;
using System.Windows.Controls;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.App;

/// <summary>
/// Remaps buttons: pick a mode and a layout (a Joy-Con alone, or one side of a pair), then each
/// button's tap, hold, double-tap and tap-then-hold actions. Changes apply at once.
/// </summary>
internal sealed class ButtonEditorWindow : Window
{
    private readonly SettingsStore _input;
    private readonly ComboBox _mode = new() { MinWidth = 220 };
    private readonly ComboBox _layout = new() { MinWidth = 200, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Grid _grid = new() { Margin = new Thickness(0, 16, 0, 0) };

    private static readonly InputMode[] Modes = [InputMode.Desktop, InputMode.Game, InputMode.Controller];

    private static readonly (string Label, JoyCon2Handedness Side, bool Paired)[] Layouts =
    [
        ("Right Joy-Con", JoyCon2Handedness.Right, false),
        ("Left Joy-Con", JoyCon2Handedness.Left, false),
        ("Right, in a pair", JoyCon2Handedness.Right, true),
        ("Left, in a pair", JoyCon2Handedness.Left, true),
    ];

    private static readonly string[] Columns = ["Tap", "Hold", "Double tap", "Tap, then hold"];

    public ButtonEditorWindow(SettingsStore input)
    {
        _input = input;
#pragma warning disable WPF0001 // Fluent theme; see SettingsWindow.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "JamCon buttons";
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var mode in Modes) _mode.Items.Add(mode.DisplayName());
        _mode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, input.Snapshot().Mode));
        _mode.SelectionChanged += (_, _) => FillLayouts();
        _layout.SelectionChanged += (_, _) => Rebuild();

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_mode);
        top.Children.Add(_layout);

        var reset = new Button { Content = "Reset this layout", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            var (mode, side, paired) = Current;
            _input.Update(s => ButtonLayouts.With(s, mode, side, paired, ButtonLayouts.Default(mode, side, paired)));
            Rebuild();
        };

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        panel.Children.Add(new TextBlock { Text = "Buttons", FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Click an action to change it. A Joy-Con on its own uses its own layout; with both connected, " +
                   "game modes use the pair layouts. Hold and double-tap timing are in settings.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 12),
        });
        panel.Children.Add(top);
        panel.Children.Add(_grid);
        panel.Children.Add(reset);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        FillLayouts();
    }

    private (InputMode Mode, JoyCon2Handedness Side, bool Paired) Current
    {
        get
        {
            var mode = Modes[Math.Max(0, _mode.SelectedIndex)];
            var layout = Layouts[Math.Max(0, _layout.SelectedIndex)];
            return (mode, layout.Side, layout.Paired);
        }
    }

    private void FillLayouts()
    {
        var mode = Modes[Math.Max(0, _mode.SelectedIndex)];
        var count = ButtonLayouts.HasPairLayouts(mode) ? Layouts.Length : 2;
        var previous = Math.Max(0, _layout.SelectedIndex);
        _layout.Items.Clear();
        foreach (var layout in Layouts.Take(count)) _layout.Items.Add(layout.Label);
        _layout.SelectedIndex = Math.Min(previous, count - 1);
    }

    private void Rebuild()
    {
        if (_layout.SelectedIndex < 0) return;
        var (mode, side, paired) = Current;
        var profile = ButtonLayouts.Get(_input.Snapshot(), mode, side, paired);

        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in Columns) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow();
        for (var c = 0; c < Columns.Length; c++) Place(new TextBlock { Text = Columns[c], FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) }, 0, c + 1);

        foreach (var button in JoyCon2ButtonLayout.AvailableButtons(side))
        {
            var row = AddRow();
            var actions = profile.ActionsFor(button);
            Place(new TextBlock { Text = JoyCon2ButtonLayout.DisplayName(button), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }, row, 0);

            ButtonAction[] cells = [actions.Press, actions.Hold, actions.DoubleTap, actions.DoubleTapHold];
            for (var c = 0; c < cells.Length; c++)
            {
                var column = c;

                // A clutch, scroll or radial menu takes the whole button: the other columns do nothing.
                var takenOver = column > 0 && actions.PressIsGyroMode;
                var cell = new Button
                {
                    Content = takenOver ? "—" : cells[c] is NoAction ? "—" : cells[c].DisplayName,
                    IsEnabled = !takenOver,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2),
                    Padding = new Thickness(8, 4, 8, 4),
                    ToolTip = takenOver ? $"{actions.Press.DisplayName} uses the whole button" : cells[c] is NoAction ? null : cells[c].DisplayName,
                };
                cell.Click += (_, _) => Edit(button, column);
                Place(cell, row, c + 1);
            }
        }
    }

    private void Edit(JoyCon2Button button, int column)
    {
        var (mode, side, paired) = Current;
        var actions = ButtonLayouts.Get(_input.Snapshot(), mode, side, paired).ActionsFor(button);
        ButtonAction[] cells = [actions.Press, actions.Hold, actions.DoubleTap, actions.DoubleTapHold];

        var picker = new ActionPickerWindow(
            $"{JoyCon2ButtonLayout.DisplayName(button)}: {Columns[column].ToLowerInvariant()}", cells[column], allowGyroModes: column == 0)
        {
            Owner = this,
        };
        if (picker.ShowDialog() != true || picker.Result is not { } chosen) return;

        var updated = column switch
        {
            0 when chosen.IsGyroMode => new ButtonActions(chosen),
            0 => actions with { Press = chosen },
            1 => actions with { Hold = chosen },
            2 => actions with { DoubleTap = chosen },
            _ => actions with { DoubleTapHold = chosen },
        };

        _input.Update(s =>
        {
            var profile = ButtonLayouts.Get(s, mode, side, paired).WithActions(button, updated);
            return ButtonLayouts.With(s, mode, side, paired, profile);
        });
        Rebuild();
    }

    private int AddRow()
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _grid.RowDefinitions.Count - 1;
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _grid.Children.Add(element);
    }
}
