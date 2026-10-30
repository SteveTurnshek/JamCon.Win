namespace JamCon.Core.Gyro;

/// <summary>How the One Euro filter's speed coefficient adapts to motion.</summary>
public enum AdaptiveSmoothingMode
{
    /// <summary>Static beta.</summary>
    Off,

    /// <summary>Beta rises with angular velocity.</summary>
    Speed,

    /// <summary>Beta rises with angular velocity or jerk, whichever is larger.</summary>
    SpeedAndJerk,
}

/// <summary>
/// Adaptive low-pass filter: heavy smoothing for slow motion, little for fast motion.
/// Casiez et al., "1€ Filter", CHI 2012. Ported from reference/swift OneEuroFilter.swift.
/// </summary>
public sealed class OneEuroFilter
{
    private const double TwoPi = 2.0 * Math.PI;

    private double? _previousRawValue;
    private double? _previousFilteredValue;
    private double _previousDerivative;
    private double? _previousTime;

    /// <summary>Base cutoff in Hz; higher means less smoothing at low speed.</summary>
    public double MinCutoff { get; set; } = 1.5;

    /// <summary>Speed coefficient; higher means less smoothing when moving fast.</summary>
    public double Beta { get; set; } = 0.35;

    /// <summary>Cutoff for smoothing the derivative itself.</summary>
    public double DerivativeCutoff { get; set; } = 1.0;

    /// <summary>Rate assumed when a timestamp is missing, repeated or regressing.</summary>
    public double FallbackRate { get; set; } = 120.0;

    /// <summary>α = 1 / (1 + τ/dt), τ = 1/(2πf).</summary>
    private static double Alpha(double cutoff, double dt)
    {
        var safeCutoff = Math.Max(0.001, cutoff);
        var safeDt = Math.Max(0.000_001, dt);
        var tau = 1.0 / (TwoPi * safeCutoff);
        return 1.0 / (1.0 + tau / safeDt);
    }

    public double Filter(double value, double timestamp)
    {
        var fallbackDt = 1.0 / Math.Max(1.0, FallbackRate);
        var hasMonotonicTimestamp = double.IsFinite(timestamp)
            && (_previousTime is null || timestamp > _previousTime.Value);
        var dt = _previousTime is { } previous && hasMonotonicTimestamp
            ? timestamp - previous
            : fallbackDt;

        var rawDerivative = _previousRawValue is { } previousRaw ? (value - previousRaw) / dt : 0;

        // Smooth the derivative so noise is not amplified into the cutoff.
        var alphaD = Alpha(DerivativeCutoff, dt);
        var derivative = alphaD * rawDerivative + (1 - alphaD) * _previousDerivative;

        var cutoff = MinCutoff + Beta * Math.Abs(derivative);
        var alphaV = Alpha(cutoff, dt);
        var filtered = alphaV * value + (1 - alphaV) * (_previousFilteredValue ?? value);

        // A regressing timestamp still produces output, but must not move the clock backward.
        if (hasMonotonicTimestamp) _previousTime = timestamp;
        _previousRawValue = value;
        _previousFilteredValue = filtered;
        _previousDerivative = derivative;
        return filtered;
    }

    public void Reset()
    {
        _previousRawValue = null;
        _previousFilteredValue = null;
        _previousDerivative = 0;
        _previousTime = null;
    }
}
