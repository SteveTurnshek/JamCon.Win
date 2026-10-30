using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamCon.App.Overlay;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;

namespace JamCon.App;

/// <summary>
/// Edits the radial menu: the inner ring's slices, the optional outer ring, rotation, sizes and
/// pointer speed, with a live preview. Changes apply at once. Upstream had the same one menu and
/// the same controls (its Radial Menu tab).
/// </summary>
internal sealed class RadialMenuEditorWindow : Window
{
    private const int MaxSlices = 12;

    private readonly SettingsStore _input;
    private readonly StackPanel _inner = new();
    private readonly StackPanel _outer = new();
    private readonly CheckBox _outerEnabled = new() { Content = "Outer ring (more slices, further out)", Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<Action> _refreshers = [];
    private bool _loading;

    public RadialMenuEditorWindow(SettingsStore input)
    {
        _input = input;
#pragma warning disable WPF0001 // Fluent theme; see SettingsWindow.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "JamCon radial menu";
        Width = 560;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        panel.Children.Add(new TextBlock { Text = "Radial menu", FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Hint("Hold B (d-pad down on a left Joy-Con), point at a slice, and let go to run it. Slices go clockwise from the top."));

        Header(panel, "Inner ring");
        panel.Children.Add(_inner);
        panel.Children.Add(AddButton(outer: false));
        Slider(panel, "Rotation", 0, 359, 1, c => c.InnerRingRotation, (c, v) => c with { InnerRingRotation = v }, v => $"{v:0}°");
        Slider(panel, "Ring thickness", 30, 150, 5, c => c.InnerRingSize, (c, v) => c with { InnerRingSize = v }, v => $"{v:0} px");
        Slider(panel, "Center (does nothing)", 20, 150, 5, c => c.DeadzoneSize, (c, v) => c with { DeadzoneSize = v }, v => $"{v:0} px");

        panel.Children.Add(_outerEnabled);
        _outerEnabled.Click += (_, _) => Update(c => c with { OuterRingEnabled = _outerEnabled.IsChecked == true });
        panel.Children.Add(_outer);
        panel.Children.Add(AddButton(outer: true));
        Slider(panel, "Outer rotation", 0, 359, 1, c => c.OuterRingRotation, (c, v) => c with { OuterRingRotation = v }, v => $"{v:0}°");
        Slider(panel, "Outer thickness", 30, 150, 5, c => c.OuterRingSize, (c, v) => c with { OuterRingSize = v }, v => $"{v:0} px");

        Header(panel, "Pointing");
        Slider(panel, "Dot speed", 0.5, 5, 0.1, c => c.RadialMovementScale, (c, v) => c with { RadialMovementScale = v }, v => $"{v:0.0}×");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
        var preview = new Button { Content = "Preview", Padding = new Thickness(14, 6, 14, 6) };
        preview.Click += (_, _) => Preview();
        var reset = new Button { Content = "Reset to arrow keys", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) =>
        {
            _input.Update(s => s with { RadialMenu = RadialMenuConfiguration.Default });
            Refresh();
        };
        buttons.Children.Add(preview);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    private RadialMenuConfiguration Config => _input.Snapshot().RadialMenu;

    private void Update(Func<RadialMenuConfiguration, RadialMenuConfiguration> edit) =>
        _input.Update(s => s with { RadialMenu = edit(s.RadialMenu) });

    private void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var refresh in _refreshers) refresh();
            _outerEnabled.IsChecked = Config.OuterRingEnabled;
            Rows(_inner, outer: false);
            Rows(_outer, outer: true);
        }
        finally
        {
            _loading = false;
        }
    }

    private static IReadOnlyList<RadialMenuItem> ItemsOf(RadialMenuConfiguration c, bool outer) => outer ? c.OuterRingItems : c.Items;

    private static RadialMenuConfiguration WithItems(RadialMenuConfiguration c, bool outer, IReadOnlyList<RadialMenuItem> items) =>
        outer ? c with { OuterRingItems = items } : c with { Items = items };

    private void EditItems(bool outer, Func<List<RadialMenuItem>, List<RadialMenuItem>> edit) =>
        Update(c => WithItems(c, outer, edit([.. ItemsOf(c, outer)])));

    private void Rows(StackPanel host, bool outer)
    {
        host.Children.Clear();
        var items = ItemsOf(Config, outer);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var item = items[i];
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };

            var number = new TextBlock { Text = $"{i + 1}", Width = 24, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var remove = new Button { Content = "✕", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove this slice" };
            remove.Click += (_, _) =>
            {
                EditItems(outer, list =>
                {
                    list.RemoveAt(index);
                    return list;
                });
                Refresh();
            };
            var action = new Button
            {
                Content = item.Action is NoAction ? "Nothing" : item.Action.DisplayName,
                Width = 190,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = item.Action.DisplayName,
            };
            action.Click += (_, _) =>
            {
                var picker = new ActionPickerWindow($"Slice {index + 1}", item.Action, allowGyroModes: false) { Owner = this };
                if (picker.ShowDialog() != true || picker.Result is not { } chosen) return;
                EditItems(outer, list =>
                {
                    list[index] = list[index] with { Action = chosen };
                    return list;
                });
                Refresh();
            };
            var label = new TextBox { Text = item.Label, Padding = new Thickness(6, 3, 6, 3), ToolTip = "Label shown on the slice" };
            label.TextChanged += (_, _) =>
            {
                if (_loading) return;
                EditItems(outer, list =>
                {
                    list[index] = list[index] with { Label = label.Text };
                    return list;
                });
            };

            DockPanel.SetDock(number, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(number);
            row.Children.Add(remove);
            row.Children.Add(action);
            row.Children.Add(label);
            host.Children.Add(row);
        }
    }

    private Button AddButton(bool outer)
    {
        var add = new Button { Content = "Add slice", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            if (ItemsOf(Config, outer).Count >= MaxSlices) return;
            EditItems(outer, list =>
            {
                list.Add(new RadialMenuItem($"Slice {list.Count + 1}", "", ButtonAction.None));
                return list;
            });
            if (outer) Update(c => c with { OuterRingEnabled = true });
            Refresh();
        };
        return add;
    }

    /// <summary>Opens the real overlay at the cursor and sweeps the dot once around each ring.</summary>
    private void Preview()
    {
        var config = Config;
        var overlay = new RadialMenuOverlay();
        overlay.Open(config);

        var rings = config.HasOuterRing ? 2 : 1;
        var duration = 3.0 * rings;
        var started = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.Now - started).TotalSeconds;
            if (t > duration + 0.4)
            {
                timer.Stop();
                overlay.Close(null);
                return;
            }

            var ring = Math.Min(rings - 1, (int)(t / 3.0));
            var radius = config.DeadzoneSize + config.InnerRingSize / 2 + ring * (config.InnerRingSize / 2 + config.OuterRingSize / 2);
            var angle = (t % 3.0) / 3.0 * 2 * Math.PI - Math.PI / 2;
            var (x, y, selection) = RadialMenuGeometry.Resolve(Math.Cos(angle) * radius, Math.Sin(angle) * radius, config);
            overlay.Move(x, y, selection);
        };
        timer.Start();
    }

    private void Slider(
        Panel panel, string label, double min, double max, double step,
        Func<RadialMenuConfiguration, double> read, Func<RadialMenuConfiguration, double, RadialMenuConfiguration> write,
        Func<double, string> format)
    {
        var value = new TextBlock { MinWidth = 70, TextAlignment = TextAlignment.Right };
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(new TextBlock { Text = label });
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, TickFrequency = step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (!_loading) Update(c => write(c, e.NewValue));
        };
        _refreshers.Add(() =>
        {
            slider.Value = Math.Clamp(read(Config), min, max);
            value.Text = format(slider.Value);
        });
        panel.Children.Add(header);
        panel.Children.Add(slider);
    }

    private static void Header(Panel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 0) });

    private static TextBlock Hint(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
}
