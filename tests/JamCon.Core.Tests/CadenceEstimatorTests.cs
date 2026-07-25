using JamCon.Core.Diagnostics;

namespace JamCon.Core.Tests;

/// <summary>
/// The cadence estimator is what turns "it feels laggy" into a number, so its resistance to
/// dropped notifications is the property that matters. A mean would be dragged by exactly the
/// outliers the median is there to reject.
/// </summary>
public class CadenceEstimatorTests
{
    [Fact]
    public void ReturnsFallbackBeforeAnyIntervalIsRecorded()
    {
        var estimator = new CadenceEstimator(fallbackRateHz: 133.0);
        Assert.Equal(133.0, estimator.EstimatedRateHz);
        Assert.False(estimator.HasStableEstimate);
    }

    [Fact]
    public void MeasuresASteadySixtySixHertzStream()
    {
        var estimator = new CadenceEstimator();
        var interval = 1.0 / 66.0;

        for (var i = 0; i < 20; i++) estimator.Record(i * interval);

        Assert.True(estimator.HasStableEstimate);
        Assert.Equal(66.0, estimator.EstimatedRateHz, precision: 6);
    }

    [Fact]
    public void ASingleDroppedNotificationDoesNotMoveTheMedian()
    {
        var estimator = new CadenceEstimator();
        var interval = 1.0 / 66.0;
        var timestamp = 0.0;

        // Eight good intervals, then one doubled interval standing in for a dropped frame.
        for (var i = 0; i < 8; i++)
        {
            timestamp += interval;
            estimator.Record(timestamp);
        }

        timestamp += interval * 2;
        estimator.Record(timestamp);

        Assert.Equal(66.0, estimator.EstimatedRateHz, precision: 6);
    }

    [Fact]
    public void IgnoresIntervalsOutsideThePlausibleTransportBand()
    {
        var estimator = new CadenceEstimator();

        // A 1-second gap is a connection stall, not a cadence sample.
        estimator.Record(0.0);
        estimator.Record(1.0);
        Assert.Equal(0, estimator.SampleCount);

        // A 1 ms gap is a notification burst, above the 240 Hz ceiling.
        estimator.Reset();
        estimator.Record(0.0);
        estimator.Record(0.001);
        Assert.Equal(0, estimator.SampleCount);
    }

    [Fact]
    public void IgnoresOutOfOrderAndNonFiniteTimestamps()
    {
        var estimator = new CadenceEstimator();
        var interval = 1.0 / 66.0;

        estimator.Record(0.0);
        estimator.Record(interval);
        var afterTwo = estimator.SampleCount;

        estimator.Record(interval / 2);      // out of order
        estimator.Record(double.NaN);        // not finite
        estimator.Record(double.PositiveInfinity);

        Assert.Equal(afterTwo, estimator.SampleCount);
    }

    [Fact]
    public void WindowIsBoundedSoTheEstimateTracksARateChange()
    {
        var estimator = new CadenceEstimator();
        var timestamp = 0.0;

        for (var i = 0; i < 30; i++)
        {
            timestamp += 1.0 / 66.0;
            estimator.Record(timestamp);
        }

        Assert.Equal(66.0, estimator.EstimatedRateHz, precision: 6);

        // The window holds nine intervals, so ten at the new rate must fully displace the old.
        for (var i = 0; i < 10; i++)
        {
            timestamp += 1.0 / 33.0;
            estimator.Record(timestamp);
        }

        Assert.Equal(33.0, estimator.EstimatedRateHz, precision: 6);
    }

    [Fact]
    public void ResetClearsAccumulatedState()
    {
        var estimator = new CadenceEstimator(fallbackRateHz: 133.0);
        for (var i = 0; i < 20; i++) estimator.Record(i / 66.0);
        Assert.True(estimator.HasStableEstimate);

        estimator.Reset();

        Assert.False(estimator.HasStableEstimate);
        Assert.Equal(0, estimator.SampleCount);
        Assert.Equal(133.0, estimator.EstimatedRateHz);
    }
}
