using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static JamCon.App.Overlay.OverlayNative;

namespace JamCon.App.Overlay;

/// <summary>
/// A brief "Game mode" / "Desktop mode" label near the bottom of the monitor under the cursor, so a
/// mode switch made from the controller is visible. Click-through and never focused, like the
/// radial menu, so it cannot take input from the game.
/// </summary>
internal sealed class ModeToast : Window
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.3);

    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(22, 10, 22, 12),
    };

    private readonly Border _label;
    private readonly System.Windows.Threading.DispatcherTimer _hideTimer;

    public ModeToast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "JamCon mode";

        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(10),
            Child = _text,
        };
        Content = _label;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = (long)GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, (nint)(style | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered));
        };

        _hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = Visible };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            var fade = new DoubleAnimation(0, Fade);
            fade.Completed += (_, _) =>
            {
                if (!_hideTimer.IsEnabled) Hide();
            };
            _label.BeginAnimation(OpacityProperty, fade);
        };
    }

    public void Flash(string message)
    {
        _text.Text = message;
        _label.BeginAnimation(OpacityProperty, null);
        _label.Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();

        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var r = info.Work;
        var x = r.Left + (r.Right - r.Left - width) / 2;
        var y = r.Bottom - height - (int)(48 * scale);
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, x, y, width, height, SwpNoActivate | SwpNoZOrder);

        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
