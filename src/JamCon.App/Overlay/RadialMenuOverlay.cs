using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using static JamCon.App.Overlay.OverlayNative;

namespace JamCon.App.Overlay;

/// <summary>
/// The radial menu: a transparent, click-through, always-on-top window over the monitor under the
/// cursor, with the menu drawn centered where the cursor was when it opened.
/// </summary>
/// <remarks>
/// <para>
/// It never takes focus (WS_EX_NOACTIVATE, and it is shown without activation), so the key a slice
/// sends goes to whatever window the user was working in. It is click-through (WS_EX_TRANSPARENT),
/// so it can never intercept the real mouse.
/// </para>
/// <para>
/// Upstream also hid the system cursor while the menu was open. Windows has no supported way to
/// hide another application's cursor, so the cursor simply holds still (the engine routes gyro to
/// the menu instead) while the dot moves.
/// </para>
/// <para>
/// The dot eases toward each new position on every render frame rather than jumping at the
/// controller's ~67 Hz, so it stays smooth on high-refresh displays.
/// </para>
/// </remarks>
internal sealed class RadialMenuOverlay : Window
{
    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush Slice = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Highlight = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x78, 0xD4)));
    private static readonly Brush Divider = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)));
    private static readonly Brush Text = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush DotFill = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Pen DotStroke = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), 2));

    /// <summary>Time constant for the dot's easing; about one controller report interval.</summary>
    private const double DotEasingSeconds = 0.015;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly List<(RadialMenuSelection Selection, Path Shape)> _slices = [];
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, Fill = DotFill, Stroke = DotStroke.Brush, StrokeThickness = 2 };
    private Point _center;
    private (double X, double Y) _target;
    private (double X, double Y) _shown;
    private RadialMenuSelection? _selection;
    private long _lastRender;
    private bool _rendering;

    public RadialMenuOverlay()
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
        Title = "JamCon radial menu";
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = (long)GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, (nint)(style | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered));
        };
    }

    public void Open(RadialMenuConfiguration config)
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        if (!IsVisible) Show();
        var handle = new WindowInteropHelper(this).Handle;
        var r = info.Monitor;
        SetWindowPos(handle, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpNoActivate | SwpNoZOrder);

        // Content is laid out in this monitor's DIPs; the window now covers exactly that monitor.
        _canvas.Width = (r.Right - r.Left) / scale;
        _canvas.Height = (r.Bottom - r.Top) / scale;
        _center = new Point((cursor.X - r.Left) / scale, (cursor.Y - r.Top) / scale);

        Build(config);
        _target = _shown = (0, 0);
        _selection = null;
        PlaceDot();

        if (!_rendering)
        {
            _lastRender = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    public void Move(double x, double y, RadialMenuSelection? selection)
    {
        _target = (x, y);
        if (selection == _selection) return;
        _selection = selection;
        foreach (var (slice, shape) in _slices) shape.Fill = slice == selection ? Highlight : Slice;
    }

    public void Close(RadialMenuItem? selected)
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        Hide();
        _canvas.Children.Clear();
        _slices.Clear();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastRender) / (double)Stopwatch.Frequency;
        _lastRender = now;

        var follow = 1 - Math.Exp(-Math.Max(0, dt) / DotEasingSeconds);
        _shown = (_shown.X + (_target.X - _shown.X) * follow, _shown.Y + (_target.Y - _shown.Y) * follow);
        PlaceDot();
    }

    private void PlaceDot()
    {
        Canvas.SetLeft(_dot, _center.X + _shown.X - _dot.Width / 2);
        Canvas.SetTop(_dot, _center.Y + _shown.Y - _dot.Height / 2);
    }

    private void Build(RadialMenuConfiguration config)
    {
        _canvas.Children.Clear();
        _slices.Clear();

        var radius = config.MenuRadius;
        var backdrop = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = Backdrop };
        Canvas.SetLeft(backdrop, _center.X - radius);
        Canvas.SetTop(backdrop, _center.Y - radius);
        _canvas.Children.Add(backdrop);

        AddRing(config.Items, config.InnerRingRotation, config.DeadzoneSize, config.DeadzoneSize + config.InnerRingSize, outerRing: false);
        if (config.HasOuterRing)
        {
            var start = config.DeadzoneSize + config.InnerRingSize;
            AddRing(config.OuterRingItems, config.OuterRingRotation, start, start + config.OuterRingSize, outerRing: true);
        }

        _canvas.Children.Add(_dot);
    }

    private void AddRing(IReadOnlyList<RadialMenuItem> items, double rotation, double inner, double outer, bool outerRing)
    {
        var count = items.Count;
        if (count == 0) return;
        var sweep = 2 * Math.PI / count;
        const double gap = 2.0; // DIPs between slices

        for (var i = 0; i < count; i++)
        {
            var start = RadialMenuGeometry.SliceStartAngle(i, count, rotation);
            var shape = new Path
            {
                Data = Sector(_center, inner + gap / 2, outer - gap / 2, start + gap / outer, start + sweep - gap / outer),
                Fill = Slice,
                Stroke = Divider,
                StrokeThickness = 1,
            };
            _canvas.Children.Add(shape);
            _slices.Add((new RadialMenuSelection(outerRing, i), shape));

            var middle = start + sweep / 2;
            var labelRadius = (inner + outer) / 2;
            AddLabel(items[i], _center.X + Math.Cos(middle) * labelRadius, _center.Y + Math.Sin(middle) * labelRadius);
        }
    }

    private void AddLabel(RadialMenuItem item, double x, double y)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (!string.IsNullOrEmpty(item.Icon))
        {
            panel.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = Text,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = 12,
            Foreground = Text,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(panel, x - panel.DesiredSize.Width / 2);
        Canvas.SetTop(panel, y - panel.DesiredSize.Height / 2);
        _canvas.Children.Add(panel);
    }

    /// <summary>An annular sector between two radii and two angles (screen radians, clockwise).</summary>
    private static Geometry Sector(Point c, double inner, double outer, double from, double to)
    {
        Point At(double r, double a) => new(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
        var large = to - from > Math.PI;

        var figure = new PathFigure { StartPoint = At(outer, from), IsClosed = true };
        figure.Segments.Add(new ArcSegment(At(outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, to), true));
        figure.Segments.Add(new ArcSegment(At(inner, from), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Bridges the engine thread to the overlay on the UI thread.</summary>
internal sealed class RadialMenuPresenter(RadialMenuOverlay overlay, Action<string> log) : IRadialMenuPresenter
{
    private readonly object _gate = new();
    private (double X, double Y, RadialMenuSelection? Selection) _latest;
    private bool _updateQueued;

    public void Show(RadialMenuConfiguration configuration) =>
        overlay.Dispatcher.BeginInvoke(() => overlay.Open(configuration));

    /// <summary>Coalesced: at most one update waits for the UI thread at a time, and it carries the latest position.</summary>
    public void Update(double offsetX, double offsetY, RadialMenuSelection? selection)
    {
        lock (_gate)
        {
            _latest = (offsetX, offsetY, selection);
            if (_updateQueued) return;
            _updateQueued = true;
        }

        overlay.Dispatcher.BeginInvoke(() =>
        {
            (double X, double Y, RadialMenuSelection? Selection) latest;
            lock (_gate)
            {
                latest = _latest;
                _updateQueued = false;
            }

            overlay.Move(latest.X, latest.Y, latest.Selection);
        });
    }

    public void Hide(RadialMenuItem? selected)
    {
        if (selected is not null) log($"Radial menu: {selected.Label}.");
        overlay.Dispatcher.BeginInvoke(() => overlay.Close(selected));
    }
}
