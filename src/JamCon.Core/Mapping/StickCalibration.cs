using JamCon.Core.Gyro;
using JamCon.Core.Protocol;

namespace JamCon.Core.Mapping;

/// <summary>
/// Live stick calibration: the factory record as an immutable anchor, plus a bounded neutral
/// correction. Ported from JoyConStickCalibration in JoyConButtonMapping.swift.
/// </summary>
/// <remarks>
/// The property that matters, and that the tests guard: a stable sample can move the center only
/// while it sits inside <see cref="GyroConstants.NeutralCorrectionRadiusCounts"/> of the anchor.
/// Measuring against the anchor rather than the last learned center means five minutes of a
/// held scroll position cannot walk the center toward it.
/// </remarks>
public sealed class StickCalibration
{
    public const double AutoCalibMinDuration = 0.5;
    public const int AutoCalibMinSamples = 30;
    public const double AutoCalibCooldown = 2.0;

    /// <summary>Maximum standard deviation, raw counts, for a window to count as still.</summary>
    public const double AutoCalibMaxStdDev = 20.0;

    public const double MinEffectiveRange = 400.0;

    private const double Nominal = GyroConstants.NominalStickCenter;

    private double _anchorCenterX = Nominal;
    private double _anchorCenterY = Nominal;
    private JoyCon2StickCalibration? _hardware;
    private bool _requiresNeutralValidation;

    private double? _autoCalibStart;
    private double _accumX, _accumY, _sumSqX, _sumSqY;
    private int _autoCalibCount;
    private double? _lastAutoCalibUpdate;

    public double CenterX { get; private set; } = Nominal;
    public double CenterY { get; private set; } = Nominal;
    public double MinX { get; private set; } = Nominal;
    public double MaxX { get; private set; } = Nominal;
    public double MinY { get; private set; } = Nominal;
    public double MaxY { get; private set; } = Nominal;

    /// <summary>Stick scrolling stays off until this is true.</summary>
    public bool IsCalibrated { get; private set; }

    public double EffectiveRangeX => Math.Max(Math.Max(CenterX - MinX, MaxX - CenterX), MinEffectiveRange);
    public double EffectiveRangeY => Math.Max(Math.Max(CenterY - MinY, MaxY - CenterY), MinEffectiveRange);

    public void UpdateAutoCalibration(ushort rawX, ushort rawY, double timestamp)
    {
        double x = rawX, y = rawY;

        // Factory ranges are authoritative; only a stick without a record learns its range.
        if (_hardware is null)
        {
            MinX = Math.Min(MinX, x);
            MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y);
            MaxY = Math.Max(MaxY, y);
        }

        if (Math.Abs(x - _anchorCenterX) > GyroConstants.NeutralCorrectionRadiusCounts
            || Math.Abs(y - _anchorCenterY) > GyroConstants.NeutralCorrectionRadiusCounts)
        {
            ResetAccumulator();
            return;
        }

        _autoCalibStart ??= timestamp;
        _accumX += x;
        _accumY += y;
        _sumSqX += x * x;
        _sumSqY += y * y;
        _autoCalibCount++;

        if (timestamp - _autoCalibStart.Value < AutoCalibMinDuration
            || _autoCalibCount < AutoCalibMinSamples
            || (_lastAutoCalibUpdate is { } last && timestamp - last < AutoCalibCooldown))
        {
            return;
        }

        var inv = 1.0 / _autoCalibCount;
        var avgX = _accumX * inv;
        var avgY = _accumY * inv;
        var stdX = Math.Sqrt(Math.Max(0, _sumSqX * inv - avgX * avgX));
        var stdY = Math.Sqrt(Math.Max(0, _sumSqY * inv - avgY * avgY));

        if (stdX < AutoCalibMaxStdDev && stdY < AutoCalibMaxStdDev)
        {
            CenterX = avgX;
            CenterY = avgY;
            IsCalibrated = true;
            _requiresNeutralValidation = false;
            _lastAutoCalibUpdate = timestamp;
        }

        ResetAccumulator();
    }

    public void ApplyHardwareCalibration(JoyCon2StickCalibration calibration)
    {
        if (_hardware == calibration) return;

        _hardware = calibration;
        _anchorCenterX = calibration.CenterX;
        _anchorCenterY = calibration.CenterY;
        CenterX = _anchorCenterX;
        CenterY = _anchorCenterY;
        MinX = CenterX - calibration.NegativeRangeX;
        MaxX = CenterX + calibration.PositiveRangeX;
        MinY = CenterY - calibration.NegativeRangeY;
        MaxY = CenterY + calibration.PositiveRangeY;
        IsCalibrated = !_requiresNeutralValidation;
        ResetAccumulator();
        _lastAutoCalibUpdate = null;
    }

    /// <summary>
    /// A user-requested recovery passes <paramref name="requireNeutralValidation"/> so stick output
    /// waits for a fresh neutral window; ordinary connection setup trusts a valid factory record.
    /// </summary>
    public void Reset(bool requireNeutralValidation = false)
    {
        CenterX = CenterY = Nominal;
        _anchorCenterX = _anchorCenterY = Nominal;
        IsCalibrated = false;
        MinX = MaxX = MinY = MaxY = Nominal;
        _hardware = null;
        _requiresNeutralValidation = requireNeutralValidation;
        ResetAccumulator();
        _lastAutoCalibUpdate = null;
    }

    private void ResetAccumulator()
    {
        _autoCalibStart = null;
        _accumX = _accumY = _sumSqX = _sumSqY = 0;
        _autoCalibCount = 0;
    }
}

/// <summary>Reads the controller's own stick and normalizes it.</summary>
public static class StickReader
{
    /// <summary>Raw counts around the calibrated center that read as centered.</summary>
    public const double DeadzoneRadius = GyroConstants.NeutralCorrectionRadiusCounts;

    /// <summary>
    /// The physical stick on this controller. A right Joy-Con 2 reports its stick in the right-stick
    /// slot (0x0D) and a left one in the left-stick slot (0x0A).
    /// </summary>
    public static StickSample Raw(JoyCon2Report report, JoyCon2Handedness handedness) =>
        handedness == JoyCon2Handedness.Right ? report.RightStick : report.LeftStick;

    /// <summary>
    /// 0–255 with 128 at center, upstream's normalized stick format. The deadzone is subtracted so
    /// output does not jump as the stick leaves it.
    /// </summary>
    public static (byte X, byte Y) Normalized(StickSample raw, StickCalibration calibration) =>
        (Normalize(raw.X, calibration.CenterX, calibration.EffectiveRangeX),
         Normalize(raw.Y, calibration.CenterY, calibration.EffectiveRangeY));

    private static byte Normalize(ushort raw, double center, double range)
    {
        var delta = raw - center;
        if (Math.Abs(delta) < DeadzoneRadius) return 128;

        var adjusted = delta - (delta > 0 ? DeadzoneRadius : -DeadzoneRadius);
        var scaled = adjusted / (range - DeadzoneRadius) * 127.0;
        var clamped = Math.Clamp(scaled, -127.0, 127.0);

        // Swift's Int(Double) truncates toward zero, and so does this cast.
        return (byte)Math.Clamp((int)(128.0 + clamped), 0, 255);
    }
}
