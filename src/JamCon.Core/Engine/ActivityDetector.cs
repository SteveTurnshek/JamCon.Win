using JamCon.Core.Gyro;
using JamCon.Core.Protocol;

namespace JamCon.Core.Engine;

/// <summary>
/// Decides whether a report shows someone using the controller, for idle sleep.
/// </summary>
/// <remarks>
/// Deliberately coarse: a button held, the stick pushed well past its deadzone, or motion fast enough
/// to be intentional. A controller resting on a desk reads well under 1 °/s and one held still in a
/// hand a few °/s, so neither keeps it awake; picking it up and pointing it does.
/// </remarks>
public static class ActivityDetector
{
    /// <summary>Angular speed treated as deliberate motion.</summary>
    public const double GyroThresholdDegreesPerSecond = 20.0;

    /// <summary>Stick deflection from nominal center treated as use; well past any factory offset.</summary>
    public const int StickThresholdCounts = 400;

    /// <summary>Bits 0–23 are buttons; byte 0x07 is status and reads 0xE0 at rest.</summary>
    private const uint ButtonMask = 0x00FF_FFFF;

    public static bool IsActive(JoyCon2Report report, JoyCon2Handedness handedness)
    {
        if (report.Imu.IsZeroFilled) return false;
        if ((report.Buttons & ButtonMask) != 0) return true;

        var stick = handedness == JoyCon2Handedness.Right ? report.RightStick : report.LeftStick;
        if (Math.Abs(stick.X - GyroConstants.NominalStickCenter) > StickThresholdCounts
            || Math.Abs(stick.Y - GyroConstants.NominalStickCenter) > StickThresholdCounts)
        {
            return true;
        }

        double x = report.Imu.GyroX, y = report.Imu.GyroY, z = report.Imu.GyroZ;
        var speed = Math.Sqrt(x * x + y * y + z * z) * GyroConstants.DegreesPerSecondPerCount;
        return speed >= GyroThresholdDegreesPerSecond;
    }
}
