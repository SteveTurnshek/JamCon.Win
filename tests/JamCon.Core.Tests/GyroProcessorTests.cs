using JamCon.Core.Gyro;

namespace JamCon.Core.Tests;

/// <summary>
/// Translated from upstream's GyroProcessorTests.swift. Tolerances and inputs are unchanged.
/// Upstream passes the effective scale in settings.gyroScale; this port passes it as an argument.
/// </summary>
public class GyroProcessorTests
{
    private static GyroSettings BaseSettings() => GyroSettings.Default with
    {
        FilterEnabled = false,
        AutoTuneSampleRate = false,
        AutoNeutralEnabled = false,
        BiasMotionThreshold = 0,
        Sensitivity = 10,
        RampSpeed = 100,
        CurveExponent = 1,
        SensitivityCap = 10,
    };

    [Fact]
    public void AccelerationSmoothingDependsOnElapsedTimeNotReportCount()
    {
        var at30 = AccelerationSpeed(after: 0.12, reportRate: 30);
        var at60 = AccelerationSpeed(after: 0.12, reportRate: 60);

        Assert.Equal(at30, at60, 0.0001);
        Assert.True(at60 > 95);
    }

    [Fact]
    public void CursorDistanceDependsOnElapsedTimeNotReportRate()
    {
        var at66 = CursorDistance(over: 1, reportRate: 66);
        var at133 = CursorDistance(over: 1, reportRate: 133);

        Assert.InRange(at66 - at133, -0.2, 0.2);
    }

    [Fact]
    public void AutoTuneIgnoresTransportStallAndCapsExtrapolation()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { AutoTuneSampleRate = true, ExpectedSampleRate = 66 };

        var t = 0.0;
        processor.Process(0, 20, 0, t, settings, 1);
        for (var i = 0; i < 40; i++)
        {
            t += 1.0 / 66.0;
            processor.Process(0, 20, 0, t, settings, 1);
        }

        t += 0.3;
        processor.Process(0, 20, 0, t, settings, 1);

        Assert.Equal(2.0 / 66.0, processor.LastResponseSample!.DeltaTime, 0.0001);
        Assert.InRange(processor.LastDebugState!.ObservedSampleRate, 65.5, 66.5);
    }

    [Fact]
    public void AutoNeutralCalibratesQuietStartupBias()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { AutoNeutralEnabled = true };

        ProcessConstant(processor, rawX: 40, from: 0, through: 0.8, reportRate: 60, settings, scale: 0.1);

        Assert.True(processor.IsCalibrated);
        Assert.Equal(4, processor.LastDebugState!.BiasX, 0.001);
    }

    [Fact]
    public void AutoNeutralDoesNotAbsorbEstablishedConstantSpeedMotion()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { AutoNeutralEnabled = true };

        ProcessConstant(processor, rawX: 40, from: 0, through: 0.8, reportRate: 60, settings, scale: 0.1);
        ProcessConstant(processor, rawX: 43, from: 0.8 + 1.0 / 60.0, through: 3.2, reportRate: 60, settings, scale: 0.1);

        Assert.Equal(4, processor.LastDebugState!.BiasX, 0.001);
        Assert.Equal(0.3, processor.LastResponseSample!.RawSpeed, 0.001);
    }

    [Fact]
    public void FilterStateResetsAcrossEnableTransitions()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with
        {
            FilterEnabled = true,
            MinCutoff = 0.5,
            Beta = 0,
            AdaptiveSmoothingMode = AdaptiveSmoothingMode.Off,
        };

        processor.Process(0, 100, 0, 0, settings, 1);
        processor.Process(0, 100, 0, 1.0 / 60.0, settings, 1);
        processor.Process(0, 0, 0, 2.0 / 60.0, settings with { FilterEnabled = false }, 1);
        processor.Process(0, 0, 0, 3.0 / 60.0, settings, 1);

        Assert.Equal(0, processor.LastResponseSample!.FilteredSpeed, 0.0001);
    }

    [Fact]
    public void RegressingTimestampDoesNotPoisonNextValidDelta()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { ExpectedSampleRate = 60 };

        processor.Process(0, 10, 0, 1, settings, 1);
        processor.Process(0, 10, 0, 0.5, settings, 1);
        processor.Process(0, 10, 0, 1 + 1.0 / 60.0, settings, 1);

        Assert.Equal(1.0 / 60.0, processor.LastResponseSample!.DeltaTime, 0.0001);
    }

    [Fact]
    public void AdaptiveBetaHonorsValuesAboveOneAndItsDocumentedCap()
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with
        {
            FilterEnabled = true,
            Beta = 1.5,
            AdaptiveSmoothingMode = AdaptiveSmoothingMode.Speed,
        };

        processor.Process(0, 150, 0, 0, settings, 1);
        Assert.Equal(1.7, processor.LastAdaptiveBeta!.Value, 0.0001);

        processor.Process(0, 150, 0, 1.0 / 60.0, settings with { Beta = 2 }, 1);
        Assert.Equal(2, processor.LastAdaptiveBeta!.Value, 0.0001);
    }

    [Fact]
    public void EffectiveScaleUsesNativeScaleAtTheDefaultUserScaleAndClamps()
    {
        var native = GyroConstants.DegreesPerSecondPerCount;
        Assert.Equal(native, GyroSettings.EffectiveScale(GyroSettings.ReferenceScale, native), 1e-12);
        Assert.Equal(native * 4, GyroSettings.EffectiveScale(10, native), 1e-12);
        Assert.Equal(native * 0.25, GyroSettings.EffectiveScale(0.0001, native), 1e-12);
    }

    private static double AccelerationSpeed(double after, double reportRate)
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { ExpectedSampleRate = reportRate };

        var t = 0.0;
        processor.Process(0, 0, 0, t, settings, 1);
        var step = 1.0 / reportRate;
        while (t + step < after)
        {
            t += step;
            processor.Process(0, 100, 0, t, settings, 1);
        }

        processor.Process(0, 100, 0, after, settings, 1);
        return processor.LastResponseSample?.AccelerationSpeed ?? 0;
    }

    private static double CursorDistance(double over, double reportRate)
    {
        var processor = new GyroProcessor();
        var settings = BaseSettings() with { ExpectedSampleRate = reportRate, SensitivityCap = 1 };

        var t = 0.0;
        processor.Process(0, 0, 0, t, settings, 1);
        var distance = 0.0;
        var step = 1.0 / reportRate;
        while (t + step < over)
        {
            t += step;
            distance += processor.Process(0, 100, 0, t, settings, 1).Dx;
        }

        distance += processor.Process(0, 100, 0, over, settings, 1).Dx;
        return distance;
    }

    private static void ProcessConstant(
        GyroProcessor processor, int rawX, double from, double through, double reportRate, GyroSettings settings, double scale)
    {
        for (var t = from; t <= through; t += 1.0 / reportRate)
        {
            processor.Process(rawX, 0, 0, t, settings, scale);
        }
    }
}

/// <summary>Translated from upstream's OneEuroFilterTests.</summary>
public class OneEuroFilterTests
{
    private static double Alpha(double cutoff, double dt) => 1.0 / (1.0 + 1.0 / (2.0 * Math.PI * cutoff) / dt);

    [Fact]
    public void DerivativeUsesPreviousRawSample()
    {
        var filter = new OneEuroFilter { MinCutoff = 1, Beta = 1, DerivativeCutoff = 1, FallbackRate = 10 };

        Assert.Equal(0, filter.Filter(0, 0), 0.000001);
        var second = filter.Filter(10, 0.1);
        var third = filter.Filter(10, 0.2);

        var derivativeAlpha = Alpha(1, 0.1);
        var firstDerivative = derivativeAlpha * 100;
        var expectedSecond = Alpha(1 + firstDerivative, 0.1) * 10;
        var secondDerivative = (1 - derivativeAlpha) * firstDerivative;
        var secondValueAlpha = Alpha(1 + secondDerivative, 0.1);
        var expectedThird = secondValueAlpha * 10 + (1 - secondValueAlpha) * expectedSecond;

        Assert.Equal(expectedSecond, second, 0.000001);
        Assert.Equal(expectedThird, third, 0.000001);
    }

    [Fact]
    public void RegressingTimestampUsesFallbackWithoutMovingClockBackward()
    {
        var filter = new OneEuroFilter { MinCutoff = 1, Beta = 0, FallbackRate = 10 };

        filter.Filter(0, 1);
        filter.Filter(10, 0.5);
        var result = filter.Filter(10, 1.1);

        var alpha = Alpha(1, 0.1);
        var afterFallback = alpha * 10;
        Assert.Equal(alpha * 10 + (1 - alpha) * afterFallback, result, 0.000001);
    }
}
