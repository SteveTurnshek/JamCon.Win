using JamCon.Core.Protocol;

namespace JamCon.Core.Gyro;

/// <summary>Gyro axes in cursor terms: pitch drives vertical motion, yaw horizontal.</summary>
public readonly record struct GyroAxes(int Pitch, int Yaw, int Roll);

/// <summary>
/// Maps raw Joy-Con 2 IMU axes onto cursor axes for the one-handed grip.
/// </summary>
/// <remarks>
/// Ported from GyroRemapper.swift's Joy-Con 2 branch. For the right controller, upstream's isolated
/// axis measurements put intended vertical motion on raw X, horizontal on raw Z and roll on raw Y
/// (docs/JoyCon2-Bluetooth.md, "Gyro profile"). The left controller is the original Joy-Con left
/// mapping with pitch and yaw negated; upstream notes it has not had its own acceptance pass.
///
/// Values widen to <c>int</c> so negating <see cref="short.MinValue"/> cannot wrap, which in Swift
/// would have trapped.
/// </remarks>
public static class GyroRemapper
{
    public static GyroAxes Remap(short rawX, short rawY, short rawZ, JoyCon2Handedness handedness) =>
        handedness == JoyCon2Handedness.Right
            ? new GyroAxes(Pitch: rawX, Yaw: rawZ, Roll: rawY)
            : new GyroAxes(Pitch: rawY, Yaw: -rawZ, Roll: rawX);
}
