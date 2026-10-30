namespace JamCon.Core.Gyro;

/// <summary>
/// The settings screen's 0–10 smoothing scale, over the One Euro filter's minimum cutoff.
/// </summary>
/// <remarks>
/// 0 turns the filter off. Each step up halves the cutoff every two steps (more smoothing, more
/// lag); level 5 is upstream's tuned 2.5 Hz, and game mode's default 5 Hz is level 3.
/// </remarks>
public static class SmoothingLevel
{
    public const int Max = 10;
    private const int Reference = 5;
    private const double ReferenceCutoff = 2.5;

    public static int Of(GyroSettings gyro)
    {
        if (!gyro.FilterEnabled) return 0;
        var level = Reference - 2 * Math.Log2(Math.Max(0.01, gyro.MinCutoff) / ReferenceCutoff);
        return (int)Math.Clamp(Math.Round(level, MidpointRounding.AwayFromZero), 1, Max);
    }

    public static GyroSettings Apply(GyroSettings gyro, int level) => level <= 0
        ? gyro with { FilterEnabled = false }
        : gyro with { FilterEnabled = true, MinCutoff = CutoffFor(Math.Min(level, Max)) };

    public static double CutoffFor(int level) => ReferenceCutoff * Math.Pow(2, (Reference - level) / 2.0);
}
