namespace JamCon.Core.Gyro;

/// <summary>Per-frame internals, for diagnostics and tests.</summary>
public sealed record GyroResponseSample(
    double DeltaTime,
    double RawSpeed,
    double FilteredSpeed,
    double AccelerationSpeed,
    double AccelerationGain,
    double ComputedCursorSpeed,
    double BiasX,
    double BiasY,
    double BiasZ,
    bool FilterEnabled,
    bool DidAutoNeutralUpdate);

/// <summary>Bias and rate state, in °/s.</summary>
public sealed record GyroDebugState(
    double BiasX,
    double BiasY,
    double BiasZ,
    bool Calibrated,
    double ObservedSampleRate,
    double? LastNeutralUpdate);

/// <summary>
/// Turns gyro samples into cursor deltas:
/// bias calibration → One Euro filter → acceleration curve → sensitivity.
/// </summary>
/// <remarks>
/// <para>
/// A line-by-line port of reference/swift/GyroProcessor.swift. Inputs are already remapped:
/// <c>x</c> is pitch, <c>y</c> is yaw and <c>z</c> roll (see <see cref="GyroRemapper"/>). Roll is
/// tracked for bias but never moves the cursor.
/// </para>
/// <para>
/// The properties upstream's tests guard, and which this port's tests guard too: cursor distance
/// depends on elapsed time rather than report rate; a transport stall is never extrapolated into a
/// cursor jump; and auto-neutral calibrates out startup drift without ever absorbing a deliberate,
/// slow, constant-speed rotation.
/// </para>
/// <para>Not thread-safe. One instance per controller, used only from the engine thread.</para>
/// </remarks>
public sealed class GyroProcessor
{
    private const int MaxBiasSamples = 64;
    private const double SpeedSmoothingTimeConstant = 0.030;
    private const double JerkSmoothingTimeConstant = 0.050;

    /// <summary>Slow EMA, so a transient stall cannot swing the learned rate.</summary>
    private const double ObservedAlpha = 0.05;

    private double _biasX;
    private double _biasY;
    private double _biasZ;
    private readonly (double X, double Y, double Z)[] _biasBuffer = new (double, double, double)[MaxBiasSamples];
    private int _biasCount;
    private int _biasIndex;
    private (double X, double Y, double Z) _biasSum;
    private bool _biasEstablished;

    private readonly OneEuroFilter _yawFilter = new();
    private readonly OneEuroFilter _pitchFilter = new();
    private double? _lastTimestamp;
    private bool? _lastFilterEnabled;

    private double _lastSpeed;
    private double _lastSpeedEma;
    private double _lastJerkEma;
    private double _observedDtEma;

    private double? _neutralStart;
    private (double X, double Y, double Z) _neutralAccumulator;
    private (double X, double Y, double Z) _neutralSumSquares;
    private int _neutralCount;
    private double? _lastNeutralUpdate;

    /// <summary>Smoothed angular speed, °/s.</summary>
    public double CurrentSpeed { get; private set; }

    public double? LastAdaptiveBeta { get; private set; }
    public GyroDebugState? LastDebugState { get; private set; }
    public GyroResponseSample? LastResponseSample { get; private set; }
    public bool IsCalibrated => _biasEstablished;

    /// <param name="scale">
    /// Effective °/s per count, i.e. <see cref="GyroSettings.EffectiveScale"/>, not the user scale.
    /// </param>
    /// <returns>Cursor delta in pixels. Positive dx is right, positive dy is down.</returns>
    public (double Dx, double Dy) Process(int rawX, int rawY, int rawZ, double timestamp, GyroSettings settings, double scale)
    {
        double x = rawX, y = rawY, z = rawZ;

        // dt. Rate learning accepts only intervals near the configured cadence, so a dropped
        // Bluetooth report cannot masquerade as a slower IMU.
        var configuredRate = Math.Max(1.0, settings.ExpectedSampleRate);
        var configuredDt = 1.0 / configuredRate;
        var timestampIsMonotonic = double.IsFinite(timestamp)
            && (_lastTimestamp is null || timestamp > _lastTimestamp.Value);
        double? rawDt = _lastTimestamp is { } last && double.IsFinite(last) && timestampIsMonotonic
            ? timestamp - last
            : null;

        if (rawDt is { } observed && observed >= configuredDt * 0.5 && observed <= configuredDt * 1.5)
        {
            _observedDtEma = _observedDtEma == 0
                ? observed
                : ObservedAlpha * observed + (1 - ObservedAlpha) * _observedDtEma;
        }

        var expectedRate = configuredRate;
        if (settings.AutoTuneSampleRate && _observedDtEma > 0)
        {
            var observedRate = 1.0 / _observedDtEma;
            expectedRate = Math.Min(configuredRate * 1.25, Math.Max(configuredRate * 0.75, observedRate));
        }

        // Never extrapolate one sample across a transport outage: that is a cursor jump on recovery.
        var expectedDt = 1.0 / expectedRate;
        var maxDt = expectedDt * 2.0;
        double dt;
        if (rawDt is { } measured)
        {
            dt = Math.Min(Math.Max(0.001, measured), maxDt);
            if (measured > maxDt)
            {
                _yawFilter.Reset();
                _pitchFilter.Reset();
            }
        }
        else
        {
            dt = expectedDt;
        }

        // A duplicate or regressing timestamp gets nominal dt now, without poisoning the next interval.
        if (timestampIsMonotonic) _lastTimestamp = timestamp;

        var previousNeutralUpdate = _lastNeutralUpdate;
        UpdateBias(x, y, z, settings.BiasMotionThreshold);
        if (settings.AutoNeutralEnabled && timestampIsMonotonic)
        {
            UpdateAutoNeutral(x, y, z, scale, timestamp);
        }

        // Bias-corrected °/s. Inverted for natural direction, as upstream does.
        var yaw = -((y - _biasY) * scale);
        var pitch = -((x - _biasX) * scale);

        var filteredYaw = yaw;
        var filteredPitch = pitch;

        if (_lastFilterEnabled != settings.FilterEnabled)
        {
            _yawFilter.Reset();
            _pitchFilter.Reset();
            _lastSpeed = 0;
            _lastJerkEma = 0;
            _lastFilterEnabled = settings.FilterEnabled;
        }

        if (settings.FilterEnabled)
        {
            var speed = Math.Sqrt(yaw * yaw + pitch * pitch);
            var adaptiveBeta = ComputeAdaptiveBeta(speed, settings.Beta, dt, settings.AdaptiveSmoothingMode);
            LastAdaptiveBeta = adaptiveBeta;

            _yawFilter.MinCutoff = settings.MinCutoff;
            _yawFilter.Beta = adaptiveBeta;
            _yawFilter.FallbackRate = expectedRate;
            _pitchFilter.MinCutoff = settings.MinCutoff;
            _pitchFilter.Beta = adaptiveBeta;
            _pitchFilter.FallbackRate = expectedRate;

            filteredYaw = _yawFilter.Filter(yaw, timestamp);
            filteredPitch = _pitchFilter.Filter(pitch, timestamp);
        }
        else
        {
            LastAdaptiveBeta = null;
        }

        var rawSpeed = Math.Sqrt(yaw * yaw + pitch * pitch);
        var filteredSpeed = Math.Sqrt(filteredYaw * filteredYaw + filteredPitch * filteredPitch);
        var smoothedSpeed = SmoothSpeed(filteredSpeed, dt);
        CurrentSpeed = smoothedSpeed;

        var gain = ParametricGain(smoothedSpeed, settings.RampSpeed, settings.CurveExponent, settings.SensitivityCap);

        var sensitivity = settings.Sensitivity * 0.1;
        var dx = filteredYaw * dt * sensitivity * gain;
        var dy = filteredPitch * dt * sensitivity * gain;
        var cursorSpeed = Math.Sqrt(dx * dx + dy * dy) / Math.Max(dt, 0.001);

        LastResponseSample = new GyroResponseSample(
            dt, rawSpeed, filteredSpeed, smoothedSpeed, gain, cursorSpeed,
            _biasX * scale, _biasY * scale, _biasZ * scale,
            settings.FilterEnabled,
            _lastNeutralUpdate != previousNeutralUpdate);

        LastDebugState = new GyroDebugState(
            _biasX * scale, _biasY * scale, _biasZ * scale,
            _biasEstablished,
            _observedDtEma > 0 ? 1.0 / _observedDtEma : configuredRate,
            _lastNeutralUpdate);

        return (dx, dy);
    }

    private void UpdateBias(double x, double y, double z, double threshold)
    {
        if (_biasEstablished) return;

        if (x * x + y * y + z * z < threshold * threshold)
        {
            var old = _biasBuffer[_biasIndex];
            _biasBuffer[_biasIndex] = (x, y, z);
            _biasIndex = (_biasIndex + 1) % MaxBiasSamples;
            if (_biasCount < MaxBiasSamples)
            {
                _biasCount++;
                _biasSum = (_biasSum.X + x, _biasSum.Y + y, _biasSum.Z + z);
            }
            else
            {
                _biasSum = (_biasSum.X + x - old.X, _biasSum.Y + y - old.Y, _biasSum.Z + z - old.Z);
            }

            if (_biasCount >= MaxBiasSamples / 2)
            {
                double count = _biasCount;
                _biasX = _biasSum.X / count;
                _biasY = _biasSum.Y / count;
                _biasZ = _biasSum.Z / count;
                _biasEstablished = true;
            }
        }
        else
        {
            _biasCount = 0;
            _biasIndex = 0;
            _biasSum = (0, 0, 0);
        }
    }

    /// <summary>
    /// Re-anchors the bias after a sustained, very quiet window. Low variance alone is not enough:
    /// a deliberate constant-speed rotation is also low-variance, so once a bias exists the candidate
    /// must also sit within 0.2 °/s of it.
    /// </summary>
    private void UpdateAutoNeutral(double rawX, double rawY, double rawZ, double scale, double timestamp)
    {
        const double minDuration = 0.6;
        const int minSamples = 20;
        const double cooldown = 2.0;
        const double maximumStartupBias = 8.0;
        const double maximumEstablishedResidual = 3.0;
        const double stdThreshold = 0.35;

        double degX = rawX * scale, degY = rawY * scale, degZ = rawZ * scale;
        var absoluteMagnitude = Math.Sqrt(degX * degX + degY * degY + degZ * degZ);
        double resX = (rawX - _biasX) * scale, resY = (rawY - _biasY) * scale, resZ = (rawZ - _biasZ) * scale;
        var residualMagnitude = Math.Sqrt(resX * resX + resY * resY + resZ * resZ);

        var clearlyMoving = _biasEstablished
            ? residualMagnitude > maximumEstablishedResidual
            : absoluteMagnitude > maximumStartupBias;
        if (clearlyMoving)
        {
            ResetNeutralAccumulator();
            return;
        }

        if (_neutralStart is null)
        {
            _neutralStart = timestamp;
            _neutralAccumulator = (rawX, rawY, rawZ);
            _neutralSumSquares = (rawX * rawX, rawY * rawY, rawZ * rawZ);
            _neutralCount = 1;
        }
        else
        {
            _neutralAccumulator = (_neutralAccumulator.X + rawX, _neutralAccumulator.Y + rawY, _neutralAccumulator.Z + rawZ);
            _neutralSumSquares = (
                _neutralSumSquares.X + rawX * rawX,
                _neutralSumSquares.Y + rawY * rawY,
                _neutralSumSquares.Z + rawZ * rawZ);
            _neutralCount++;
        }

        if (_neutralStart is not { } start
            || timestamp - start < minDuration
            || _neutralCount < minSamples
            || (_lastNeutralUpdate is { } lastUpdate && timestamp - lastUpdate < cooldown))
        {
            return;
        }

        var inv = 1.0 / _neutralCount;
        double avgX = _neutralAccumulator.X * inv, avgY = _neutralAccumulator.Y * inv, avgZ = _neutralAccumulator.Z * inv;
        var varX = Math.Max(0, _neutralSumSquares.X * inv - avgX * avgX);
        var varY = Math.Max(0, _neutralSumSquares.Y * inv - avgY * avgY);
        var varZ = Math.Max(0, _neutralSumSquares.Z * inv - avgZ * avgZ);
        var stillnessPass = Math.Sqrt(varX) * scale < stdThreshold
            && Math.Sqrt(varY) * scale < stdThreshold
            && Math.Sqrt(varZ) * scale < stdThreshold;

        var candidateMagnitude = Math.Sqrt(avgX * avgX + avgY * avgY + avgZ * avgZ) * scale;
        var candidateShift = Math.Sqrt(
            (avgX - _biasX) * (avgX - _biasX)
            + (avgY - _biasY) * (avgY - _biasY)
            + (avgZ - _biasZ) * (avgZ - _biasZ)) * scale;
        var plausibleNeutral = _biasEstablished ? candidateShift <= 0.2 : candidateMagnitude <= maximumStartupBias;

        if (stillnessPass && plausibleNeutral)
        {
            _biasX = avgX;
            _biasY = avgY;
            _biasZ = avgZ;
            _biasEstablished = true;
            Array.Fill(_biasBuffer, (avgX, avgY, avgZ));
            _biasCount = MaxBiasSamples;
            _biasIndex = 0;
            _biasSum = (avgX * MaxBiasSamples, avgY * MaxBiasSamples, avgZ * MaxBiasSamples);
            _lastNeutralUpdate = timestamp;
        }

        ResetNeutralAccumulator();
    }

    private void ResetNeutralAccumulator()
    {
        _neutralStart = null;
        _neutralAccumulator = (0, 0, 0);
        _neutralSumSquares = (0, 0, 0);
        _neutralCount = 0;
    }

    private double ComputeAdaptiveBeta(double speed, double baseBeta, double dt, AdaptiveSmoothingMode mode)
    {
        var clampedBaseBeta = Math.Min(2.0, Math.Max(0, baseBeta));
        var previousSpeed = _lastSpeed;
        _lastSpeed = speed;

        switch (mode)
        {
            case AdaptiveSmoothingMode.Off:
                return clampedBaseBeta;

            case AdaptiveSmoothingMode.Speed:
                return Math.Min(2.0, clampedBaseBeta + Math.Min(1.0, speed / 150.0) * 0.2);

            default:
                var jerk = Math.Abs(speed - previousSpeed) / Math.Max(dt, 0.001);
                var jerkAlpha = EmaAlpha(dt, JerkSmoothingTimeConstant);
                _lastJerkEma = jerkAlpha * jerk + (1 - jerkAlpha) * _lastJerkEma;
                var speedTerm = Math.Min(1.0, speed / 150.0);
                var jerkTerm = Math.Min(1.0, _lastJerkEma / 200.0);
                return Math.Min(2.0, clampedBaseBeta + Math.Max(speedTerm, jerkTerm) * 0.2);
        }
    }

    /// <summary>
    /// Time-based EMA, so acceleration response does not slow down merely because fewer reports
    /// arrived.
    /// </summary>
    private double SmoothSpeed(double instantaneous, double dt)
    {
        var alpha = EmaAlpha(dt, SpeedSmoothingTimeConstant);
        _lastSpeedEma = alpha * instantaneous + (1 - alpha) * _lastSpeedEma;
        return _lastSpeedEma;
    }

    private static double EmaAlpha(double dt, double timeConstant) =>
        1 - Math.Exp(-Math.Max(0.001, dt) / Math.Max(0.001, timeConstant));

    /// <summary>Gain from 1 at rest to <paramref name="cap"/> at <paramref name="rampSpeed"/> and above.</summary>
    private static double ParametricGain(double speed, double rampSpeed, double exponent, double cap)
    {
        var effectiveCap = Math.Max(1.0, cap);
        var normalized = Math.Min(1.0, speed / Math.Max(1.0, rampSpeed));
        return 1.0 + Math.Pow(normalized, exponent) * (effectiveCap - 1.0);
    }

    public void Reset()
    {
        _biasX = _biasY = _biasZ = 0;
        Array.Clear(_biasBuffer);
        _biasCount = 0;
        _biasIndex = 0;
        _biasSum = (0, 0, 0);
        _biasEstablished = false;
        _lastTimestamp = null;
        _yawFilter.Reset();
        _pitchFilter.Reset();
        _lastFilterEnabled = null;
        _lastSpeed = 0;
        _lastSpeedEma = 0;
        _lastJerkEma = 0;
        CurrentSpeed = 0;
        LastAdaptiveBeta = null;
        _observedDtEma = 0;
        ResetNeutralAccumulator();
        _lastNeutralUpdate = null;
        LastDebugState = null;
        LastResponseSample = null;
    }
}
