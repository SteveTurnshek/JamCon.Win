using System.Windows;
using System.Windows.Controls;

namespace JamCon.App;

/// <summary>
/// The small ↺ button beside a settings slider: puts it back to its default, through the slider's
/// own change handler, so the setting is written and saved as if dragged there.
/// </summary>
internal static class SliderReset
{
    public static Button For(Slider slider, double defaultValue, Func<double, string> format)
    {
        var target = Math.Clamp(defaultValue, slider.Minimum, slider.Maximum);
        var button = new Button
        {
            Content = "↺",
            Padding = new Thickness(7, 0, 7, 1),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Reset to default ({format(target)})",
        };
        button.Click += (_, _) => slider.Value = target;

        void Update() => button.IsEnabled = Math.Abs(slider.Value - target) > slider.TickFrequency / 2;
        slider.ValueChanged += (_, _) => Update();
        Update();
        return button;
    }
}
