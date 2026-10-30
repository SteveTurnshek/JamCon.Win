namespace JamCon.Output;

/// <summary>
/// Converts pixels to the 0–65535 range that <c>MOUSEEVENTF_ABSOLUTE</c> expects.
/// </summary>
/// <remarks>
/// <para>
/// Windows maps a normalized value <c>n</c> back to the pixel <c>origin + n × extent / 65536</c>.
/// The naive inverse, <c>pixel × 65535 / extent</c>, lands one pixel short for much of the screen,
/// which shows up as the cursor creeping left and up during slow gyro motion.
/// </para>
/// <para>
/// Rounding up instead places <c>n × extent / 65536</c> in <c>[pixel, pixel + extent/65536)</c>. That
/// recovers the exact pixel whether Windows truncates or rounds, for any extent below 32768 pixels,
/// so the conversion does not depend on an undocumented rounding mode.
/// </para>
/// </remarks>
public static class AbsoluteCoordinates
{
    public const int Range = 65536;

    public static int Normalize(int pixel, int origin, int extent)
    {
        if (extent <= 0) return 0;
        long offset = Math.Clamp(pixel - origin, 0, extent - 1);
        var normalized = (offset * Range + extent - 1) / extent;
        return (int)Math.Clamp(normalized, 0, Range - 1);
    }

    /// <summary>Windows' documented mapping back to a pixel, truncating. Used by tests.</summary>
    public static int ToPixelTruncating(int normalized, int origin, int extent) =>
        origin + (int)((long)normalized * extent / Range);

    /// <summary>The same mapping with rounding instead of truncation. Used by tests.</summary>
    public static int ToPixelRounding(int normalized, int origin, int extent) =>
        origin + (int)Math.Round((double)normalized * extent / Range, MidpointRounding.AwayFromZero);
}
