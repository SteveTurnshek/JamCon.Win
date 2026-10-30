using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JamCon.Bluetooth;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;

namespace JamCon.App.Tray;

internal enum TrayTone
{
    Streaming,
    Working,
    Problem,
    Idle,
}

/// <summary>
/// Draws the tray icon: the Joy-Con outline plus a status dot.
/// </summary>
/// <remarks>
/// The bundled artwork is a black outline on transparency, which disappears on a dark taskbar, so
/// it is used as an opacity mask and painted in whichever colour contrasts with the current taskbar
/// theme. Icons are cached per tone and theme, so there are at most eight and their handles never
/// accumulate.
/// </remarks>
internal static class TrayIconRenderer
{
    private const int Size = 32;

    private static readonly BitmapImage Outline = LoadOutline();
    private static readonly Dictionary<(TrayTone, bool), DrawingIcon> Cache = new();

    public static TrayTone ToneFor(JoyCon2LinkState state) => state switch
    {
        JoyCon2LinkState.Streaming => TrayTone.Streaming,
        JoyCon2LinkState.Searching or JoyCon2LinkState.Connecting or JoyCon2LinkState.Reconnecting => TrayTone.Working,
        JoyCon2LinkState.PairedConflict => TrayTone.Problem,
        _ => TrayTone.Idle,
    };

    /// <summary>True when Windows draws a light taskbar, read from the theme setting.</summary>
    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    public static DrawingIcon Get(TrayTone tone, bool lightTaskbar)
    {
        if (!Cache.TryGetValue((tone, lightTaskbar), out var icon))
        {
            icon = Render(tone, lightTaskbar);
            Cache[(tone, lightTaskbar)] = icon;
        }

        return icon;
    }

    private static DrawingIcon Render(TrayTone tone, bool lightTaskbar)
    {
        var ink = lightTaskbar ? Colors.Black : Colors.White;
        var taskbar = lightTaskbar ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The artwork is 39x92; keep its aspect ratio at full icon height.
            var width = Size * Outline.PixelWidth / (double)Outline.PixelHeight;
            dc.PushOpacityMask(new ImageBrush(Outline));
            dc.DrawRectangle(new SolidColorBrush(ink), null, new Rect(3, 0, width, Size));
            dc.Pop();

            // A ring in the taskbar colour keeps the dot legible where it overlaps the outline.
            dc.DrawEllipse(
                new SolidColorBrush(DotColour(tone)),
                new Pen(new SolidColorBrush(taskbar), 2.5),
                new Point(Size - 8, Size - 8),
                7,
                7);
        }

        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var pixels = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        const int stride = Size * 4;
        var buffer = new byte[stride * Size];
        pixels.CopyPixels(buffer, stride, 0);

        using var bitmap = new System.Drawing.Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, Size, Size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        bitmap.UnlockBits(data);

        // The handle is deliberately never destroyed: the icon is cached for the process lifetime.
        return DrawingIcon.FromHandle(bitmap.GetHicon());
    }

    private static Color DotColour(TrayTone tone) => tone switch
    {
        TrayTone.Streaming => Color.FromRgb(0x2E, 0xA0, 0x43),
        TrayTone.Working => Color.FromRgb(0xD2, 0x99, 0x22),
        TrayTone.Problem => Color.FromRgb(0xF8, 0x51, 0x49),
        _ => Color.FromRgb(0x8B, 0x94, 0x9E),
    };

    private static BitmapImage LoadOutline()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/joyconR.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
