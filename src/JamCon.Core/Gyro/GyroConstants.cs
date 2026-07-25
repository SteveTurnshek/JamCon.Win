namespace JamCon.Core.Gyro;

/// <summary>
/// Calibration constants the port must not drift on. Transcribed from
/// docs/JoyCon2-Bluetooth.md and cross-confirmed by matiaspalmac/everything-imu.
/// GyroConstantsTests guards these values deliberately: a silent change here
/// produces subtly wrong cursor motion that is very hard to trace back.
/// </summary>
public static class GyroConstants
{
    /// <summary>Gyro scale in degrees/second per raw count.</summary>
    public const double DegreesPerSecondPerCount = 0.0608506463;

    /// <summary>
    /// Radius, in stick counts, of the neutral-correction window around the
    /// immutable factory/connection anchor.
    /// </summary>
    public const int NeutralCorrectionRadiusCounts = 150;

    /// <summary>Nominal analog stick center, in counts.</summary>
    public const int NominalStickCenter = 2048;
}
