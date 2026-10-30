namespace JamCon.Core.Output;

/// <summary>A rectangle of valid positions, edges inclusive.</summary>
public readonly record struct DisplayBounds(double Left, double Top, double Right, double Bottom)
{
    public bool IsEmpty => !(Right > Left && Bottom > Top);
}

/// <summary>Ported from DisplayGeometry.swift.</summary>
public static class DisplayGeometry
{
    /// <summary>
    /// The closest valid cursor point on any display. Clamping to the union of the displays'
    /// bounding box would allow unreachable gaps when monitors differ in size or offset.
    /// </summary>
    /// <remarks>
    /// Edges are inclusive, so callers working in pixels should pass the last valid pixel as the
    /// right and bottom edges (monitor right − 1, bottom − 1).
    /// </remarks>
    public static (double X, double Y) ClosestPoint(double x, double y, IReadOnlyList<DisplayBounds> displays)
    {
        var valid = displays.Where(d => !d.IsEmpty).ToArray();
        if (valid.Length == 0) return (x, y);

        if (valid.Any(d => x >= d.Left && x <= d.Right && y >= d.Top && y <= d.Bottom)) return (x, y);

        var closest = Clamp(x, y, valid[0]);
        var closestDistance = SquaredDistance(x, y, closest);
        foreach (var display in valid.Skip(1))
        {
            var candidate = Clamp(x, y, display);
            var distance = SquaredDistance(x, y, candidate);
            if (distance < closestDistance)
            {
                closest = candidate;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private static (double X, double Y) Clamp(double x, double y, DisplayBounds d) =>
        (Math.Clamp(x, d.Left, d.Right), Math.Clamp(y, d.Top, d.Bottom));

    private static double SquaredDistance(double x, double y, (double X, double Y) p) =>
        (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
}
