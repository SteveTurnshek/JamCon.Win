namespace JamCon.Core.Diagnostics;

/// <summary>
/// Estimates the cadence actually delivered by the transport, ported from
/// reference/swift/JoyCon2BLEProtocol.swift (JoyCon2BLECadenceEstimator).
/// </summary>
/// <remarks>
/// A requested report rate is only a preference: the negotiated BLE connection can cap it, and a
/// single missed notification doubles one interval. Taking the median of a short window rejects
/// isolated drops while still adapting quickly when the real rate changes. A mean would be dragged
/// by exactly the outliers we want to ignore.
/// </remarks>
public sealed class CadenceEstimator(double fallbackRateHz = 133.0)
{
    private const int IntervalCapacity = 9;

    /// <summary>Widest interval band accepted, in seconds — 240 Hz to 15 Hz.</summary>
    private const double MinimumInterval = 1.0 / 240.0;
    private const double MaximumInterval = 1.0 / 15.0;

    private readonly List<double> _intervals = new(IntervalCapacity);
    private double? _lastTimestampSeconds;

    /// <summary>True once the window is full, i.e. the estimate is worth quoting.</summary>
    public bool HasStableEstimate => _intervals.Count == IntervalCapacity;

    public int SampleCount => _intervals.Count;

    /// <summary>Median-based rate in Hz, or the fallback until at least one interval is recorded.</summary>
    public double EstimatedRateHz
    {
        get
        {
            if (_intervals.Count == 0) return fallbackRateHz;
            var sorted = _intervals.ToArray();
            Array.Sort(sorted);
            return 1.0 / sorted[sorted.Length / 2];
        }
    }

    /// <summary>
    /// Records a notification timestamp and returns the updated estimate. Out-of-order,
    /// non-finite and implausibly spaced timestamps are ignored rather than allowed to
    /// corrupt the window.
    /// </summary>
    public double Record(double timestampSeconds)
    {
        if (!double.IsFinite(timestampSeconds)) return EstimatedRateHz;

        var previous = _lastTimestampSeconds;
        if (previous is null || timestampSeconds > previous.Value)
        {
            _lastTimestampSeconds = timestampSeconds;
        }

        if (previous is null || timestampSeconds <= previous.Value) return EstimatedRateHz;

        var interval = timestampSeconds - previous.Value;
        if (interval < MinimumInterval || interval > MaximumInterval) return EstimatedRateHz;

        _intervals.Add(interval);
        if (_intervals.Count > IntervalCapacity)
        {
            _intervals.RemoveRange(0, _intervals.Count - IntervalCapacity);
        }

        return EstimatedRateHz;
    }

    public void Reset()
    {
        _intervals.Clear();
        _lastTimestampSeconds = null;
    }
}
