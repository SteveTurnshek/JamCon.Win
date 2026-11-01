namespace JamCon.Core.Mapping;

/// <summary>One slice of the radial menu.</summary>
/// <param name="Icon">
/// A Segoe Fluent Icons glyph, replacing upstream's SF Symbol names. Optional; the label is always
/// shown.
/// </param>
/// <param name="Action">Run as a tap when the menu closes on this slice. Gyro modes do nothing here.</param>
public sealed record RadialMenuItem(string Label, string Icon, ButtonAction Action);

/// <summary>
/// The radial menu's layout and slices. Ported from RadialMenuConfiguration in RadialMenu.swift.
/// </summary>
/// <remarks>
/// Sizes are in device-independent pixels. Slice 0 starts at straight up and slices run clockwise,
/// turned back by the rotation. Upstream's rotation of 0 put "Up" in the quarter from 12 to 3
/// o'clock; this fork defaults to 45°, which centers each arrow slice on its direction.
/// </remarks>
public sealed record RadialMenuConfiguration
{
    public static RadialMenuConfiguration Default { get; } = new();

    public string Name { get; init; } = "Arrow Keys";

    /// <summary>Upstream's default: the four arrow keys.</summary>
    public IReadOnlyList<RadialMenuItem> Items { get; init; } =
    [
        new("Up", "", ButtonAction.Key(VirtualKeys.Up)),
        new("Right", "", ButtonAction.Key(VirtualKeys.Right)),
        new("Down", "", ButtonAction.Key(VirtualKeys.Down)),
        new("Left", "", ButtonAction.Key(VirtualKeys.Left)),
    ];

    /// <summary>Radius of the empty center. Releasing in here selects nothing.</summary>
    public double DeadzoneSize { get; init; } = 50;

    public double InnerRingSize { get; init; } = 50;

    /// <summary>Rotation of the inner ring, in degrees. 45 centers a four-slice menu on its directions.</summary>
    public double InnerRingRotation { get; init; } = 45;

    public bool OuterRingEnabled { get; init; }
    public IReadOnlyList<RadialMenuItem> OuterRingItems { get; init; } = [];
    public double OuterRingSize { get; init; } = 50;
    public double OuterRingRotation { get; init; }

    /// <summary>Multiplier on gyro motion while the menu is open.</summary>
    public double RadialMovementScale { get; init; } = 2.0;

    public bool HasOuterRing => OuterRingEnabled && OuterRingItems.Count > 0;

    public double MenuRadius => DeadzoneSize + InnerRingSize + (OuterRingEnabled ? OuterRingSize : 0);
}

/// <summary>Which slice is under the pointer.</summary>
public readonly record struct RadialMenuSelection(bool Outer, int Index);

/// <summary>Ported from RadialMenuGeometry.swift.</summary>
public static class RadialMenuGeometry
{
    /// <summary>The pointer stays this far inside the menu's edge.</summary>
    public const double EdgeInset = 10;

    /// <summary>
    /// Clamps the pointer offset to the menu and finds the slice under it. Offsets are in screen
    /// orientation: positive y is down.
    /// </summary>
    public static (double X, double Y, RadialMenuSelection? Selection) Resolve(double x, double y, RadialMenuConfiguration config)
    {
        var magnitude = Math.Sqrt(x * x + y * y);
        var maximumRadius = Math.Max(0, config.MenuRadius - EdgeInset);

        if (magnitude > maximumRadius && magnitude > 0)
        {
            var scale = maximumRadius / magnitude;
            x *= scale;
            y *= scale;
        }

        var clampedMagnitude = Math.Min(magnitude, maximumRadius);
        if (clampedMagnitude < config.DeadzoneSize) return (x, y, null);

        var angle = Math.Atan2(y, x);
        if (config.HasOuterRing && clampedMagnitude >= config.DeadzoneSize + config.InnerRingSize)
        {
            return (x, y, new RadialMenuSelection(true, Index(angle, config.OuterRingItems.Count, config.OuterRingRotation)));
        }

        return config.Items.Count == 0
            ? (x, y, null)
            : (x, y, new RadialMenuSelection(false, Index(angle, config.Items.Count, config.InnerRingRotation)));
    }

    public static RadialMenuItem? ItemFor(RadialMenuSelection? selection, RadialMenuConfiguration config)
    {
        if (selection is not { } s) return null;
        var items = s.Outer ? config.OuterRingItems : config.Items;
        return s.Index >= 0 && s.Index < items.Count ? items[s.Index] : null;
    }

    /// <summary>
    /// Start angle of a slice in screen radians (0 = right, clockwise positive), matching
    /// <see cref="Resolve"/>. For drawing.
    /// </summary>
    public static double SliceStartAngle(int index, int count, double rotationDegrees) =>
        -Math.PI / 2 + 2 * Math.PI / Math.Max(1, count) * index - rotationDegrees * Math.PI / 180;

    private static int Index(double angle, int count, double rotationDegrees)
    {
        if (count <= 0) return 0;
        var rotationRadians = -rotationDegrees * Math.PI / 180;
        var normalized = (angle + Math.PI / 2 - rotationRadians) % (2 * Math.PI);
        if (normalized < 0) normalized += 2 * Math.PI;
        return Math.Min((int)(normalized / (2 * Math.PI / count)), count - 1);
    }
}
