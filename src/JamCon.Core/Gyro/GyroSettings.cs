namespace JamCon.Core.Gyro;

/// <summary>
/// Gyro-to-cursor tuning. Immutable: the engine reads one snapshot per frame and the UI replaces
/// it with <c>with</c> expressions, so a frame never sees a half-applied change.
/// </summary>
/// <remarks>
/// Defaults are upstream's Joy-Con production values (GyroSettingsState.defaultForKind(.joyCon)).
/// Upstream fields its own source marks as legacy and unused in processing (acceleration mode,
/// simple acceleration, curve preset and strength, soft-cutoff and recovery thresholds) and the
/// original Joy-Con's three-sample averaging option are not carried over.
/// </remarks>
public sealed record GyroSettings
{
    /// <summary>
    /// Upstream's shared reference scale (the PS VR2 Sense's 1/16 °/s per count). The user scale is
    /// expressed relative to it, so its default of 1/16 means "use the controller's native scale".
    /// </summary>
    public const double ReferenceScale = 1.0 / 16.0;

    public static GyroSettings Default { get; } = new();

    public double Sensitivity { get; init; } = 40.0;

    /// <summary>
    /// User scale relative to <see cref="ReferenceScale"/>. See <see cref="EffectiveScale"/>; it is
    /// the effective value, not this one, that the processor consumes.
    /// </summary>
    public double GyroScale { get; init; } = ReferenceScale;

    public bool FilterEnabled { get; init; } = true;
    public double MinCutoff { get; init; } = 2.5;
    public double Beta { get; init; } = 1.0;
    public AdaptiveSmoothingMode AdaptiveSmoothingMode { get; init; } = AdaptiveSmoothingMode.SpeedAndJerk;

    /// <summary>Nominal report rate, used for dt clamping and stall handling.</summary>
    public double ExpectedSampleRate { get; init; } = 66.0;

    public bool AutoTuneSampleRate { get; init; }

    /// <summary>Refresh the gyro bias whenever the controller is held very still.</summary>
    public bool AutoNeutralEnabled { get; init; } = true;

    /// <summary>Acceleration curve shape: below 1 concave, 1 linear, above 1 convex.</summary>
    public double CurveExponent { get; init; } = 1.0;

    /// <summary>Angular speed, °/s, at which acceleration reaches <see cref="SensitivityCap"/>.</summary>
    public double RampSpeed { get; init; } = 150.0;

    /// <summary>Maximum acceleration gain.</summary>
    public double SensitivityCap { get; init; } = 20.0;

    /// <summary>
    /// Motion magnitude, raw counts, below which samples feed the startup bias estimate. Joy-Con uses
    /// 30 rather than the Sense's 50 because of its lower noise floor.
    /// </summary>
    public double BiasMotionThreshold { get; init; } = 30.0;

    /// <summary>
    /// The scale the processor actually uses: the controller's native °/s per count, multiplied by
    /// the user's scale relative to <see cref="ReferenceScale"/>, clamped to 0.25×–4×.
    /// Ported from InputEngine+Gyro.swift effectiveGyroScale.
    /// </summary>
    public static double EffectiveScale(double userScale, double nativeScale)
    {
        var multiplier = ReferenceScale != 0 ? userScale / ReferenceScale : 1.0;
        return nativeScale * Math.Clamp(multiplier, 0.25, 4.0);
    }
}
