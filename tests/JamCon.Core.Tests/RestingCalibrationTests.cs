using JamCon.Core.Gyro;

namespace JamCon.Core.Tests;

/// <summary>
/// Bias learned while the controller was held can be off by a degree a second, which always-on game
/// aiming shows as constant drift. Set down, the accelerometer confirms it is resting and the bias
/// is corrected; in the hand or turning slowly, it is not.
/// </summary>
public class RestingCalibrationTests
{
    private const double Rate = 66.0;
    private static readonly double Scale = GyroConstants.DegreesPerSecondPerCount;
    private static readonly GyroSettings Settings = GyroSettings.GameDefault;

    /// <summary>~1 °/s of yaw error baked into the initial bias.</summary>
    private const int LearnedOffset = 16;

    private static (GyroProcessor Gyro, double T) LearnWrongBias()
    {
        var gyro = new GyroProcessor();
        var t = 1.0;
        for (var i = 0; i < 40; i++) gyro.Process(0, LearnedOffset, 0, t += 1 / Rate, Settings, Scale, (0, 0, 4096));
        Assert.True(gyro.IsCalibrated);
        Assert.Equal(LearnedOffset * Scale, gyro.LastDebugState!.BiasY, 3);
        return (gyro, t);
    }

    private static double DriftAfter(GyroProcessor gyro, ref double t, double seconds, Func<int, (double, double, double)?> accel, Func<int, int>? yaw = null)
    {
        double total = 0;
        var frames = (int)(seconds * Rate);
        for (var i = 0; i < frames; i++)
        {
            var (dx, _) = gyro.Process(0, yaw?.Invoke(i) ?? 0, 0, t += 1 / Rate, Settings, Scale, accel(i));
            if (i >= frames - 66) total += dx; // the last second
        }

        return Math.Abs(total);
    }

    [Fact]
    public void SetDownTheBiasIsCorrected()
    {
        var (gyro, t) = LearnWrongBias();
        var noise = new Random(1);
        var drift = DriftAfter(gyro, ref t, 4, _ => (noise.Next(-1, 2), noise.Next(-1, 2), 4096 + noise.Next(-1, 2)));

        Assert.Equal(0, gyro.LastDebugState!.BiasY, 1);
        Assert.True(drift < 1.0, $"still drifting {drift:F1} counts/s");
    }

    [Fact]
    public void InTheHandTheOldConservativeRuleStillApplies()
    {
        var (gyro, t) = LearnWrongBias();
        var noise = new Random(2);
        DriftAfter(gyro, ref t, 4, _ => (noise.Next(-30, 31), noise.Next(-30, 31), 4096 + noise.Next(-30, 31)));

        // Still most of a degree a second off: only the 0.2 °/s rule applied.
        Assert.True(gyro.LastDebugState!.BiasY > 0.7);
    }

    [Fact]
    public void ASlowDeliberateTurnIsNotAbsorbed()
    {
        // Correct bias, then a steady 1 °/s turn: gravity swings across the accelerometer.
        var gyro = new GyroProcessor();
        var t = 1.0;
        for (var i = 0; i < 40; i++) gyro.Process(0, 0, 0, t += 1 / Rate, Settings, Scale, (0, 0, 4096));

        var turn = (int)Math.Round(1.0 / Scale);
        DriftAfter(gyro, ref t, 4, i =>
        {
            var angle = i / Rate * Math.PI / 180; // 1 °/s
            return (4096 * Math.Sin(angle), 0, 4096 * Math.Cos(angle));
        }, _ => turn);

        // The turn was not taken for bias: at most the 0.2 °/s rule's nudge, nowhere near 1 °/s.
        Assert.True(gyro.LastDebugState!.BiasY < 0.2);
    }

    [Fact]
    public void WithoutAnAccelerometerNothingChanges()
    {
        var (gyro, t) = LearnWrongBias();
        DriftAfter(gyro, ref t, 4, _ => null);
        Assert.True(gyro.LastDebugState!.BiasY > 0.7);
    }
}
