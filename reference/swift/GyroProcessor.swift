import Foundation
import CoreGraphics

// MARK: - Gyro Processor

/// Converts raw gyroscope data to mouse movement using professional-grade signal processing.
///
/// Processing Pipeline:
/// ```
/// Raw Gyro → Bias Calibration → One Euro Filter → Acceleration Curve → Sensitivity Scale → Mouse Delta
/// ```
final class GyroProcessor: @unchecked Sendable {

    // MARK: - Bias Estimation

    private var biasX: Double = 0
    private var biasY: Double = 0
    private var biasZ: Double = 0
    private var biasBuffer: [(x: Double, y: Double, z: Double)]
    private var biasCount: Int = 0
    private var biasIndex: Int = 0
    private var biasSum: (x: Double, y: Double, z: Double) = (0, 0, 0)
    private let maxBiasSamples = 64
    private var biasEstablished = false

    // MARK: - Filtering

    private let yawFilter = OneEuroFilter()
    private let pitchFilter = OneEuroFilter()
    private var lastTimestamp: TimeInterval?
    private var lastFilterEnabled: Bool?

    // MARK: - Adaptive Smoothing State

    private var lastSpeed: Double = 0
    private var lastSpeedEMA: Double = 0
    private var lastJerkEMA: Double = 0
    private let speedSmoothingTimeConstant: TimeInterval = 0.030
    private let jerkSmoothingTimeConstant: TimeInterval = 0.050

    /// Current smoothed speed (°/s) for UI visualization
    private(set) var currentSpeed: Double = 0
    private(set) var lastAdaptiveBeta: Double?

    // Sample rate auto-tune
    private var observedDtEMA: Double = 0
    private let observedAlpha = 0.05  // slow EMA to avoid reacting to transient stalls

    // Auto-neutral detection (very still → refresh bias)
    private var neutralStart: TimeInterval?
    private var neutralAccumulator: (x: Double, y: Double, z: Double) = (0, 0, 0)
    private var neutralSumSquares: (x: Double, y: Double, z: Double) = (0, 0, 0)
    private var neutralCount: Int = 0
    private var lastNeutralUpdate: TimeInterval?

    // Debug state snapshot
    struct DebugState {
        let biasX: Double
        let biasY: Double
        let biasZ: Double
        let calibrated: Bool
        let observedSampleRate: Double
        let lastNeutralUpdate: TimeInterval?
    }
    private(set) var lastDebugState: DebugState?
    private(set) var lastResponseSample: GyroResponseSample?

    init() {
        biasBuffer = Array(repeating: (0, 0, 0), count: maxBiasSamples)
    }

    // MARK: - Processing

    /// Process raw gyro data and return mouse deltas
    /// - Parameters:
    ///   - rawX: Raw X-axis gyro value (pitch)
    ///   - rawY: Raw Y-axis gyro value (roll)
    ///   - rawZ: Raw Z-axis gyro value (yaw)
    ///   - timestamp: Monotonic timestamp (seconds)
    ///   - settings: Thread-safe settings snapshot
    /// - Returns: (dx, dy) mouse movement, or nil if no movement
    func process(
        rawX: Int16,
        rawY: Int16,
        rawZ: Int16,
        timestamp: TimeInterval,
        settings: GyroSettingsState
    ) -> (dx: CGFloat, dy: CGFloat)? {

        // 1. Convert to doubles
        let x = Double(rawX)
        let y = Double(rawY)
        let z = Double(rawZ)

        // 2. Calculate dt. Nominal-rate learning accepts only intervals close
        // to the configured cadence, so a dropped Bluetooth report cannot
        // masquerade as a slower physical IMU.
        let configuredRate = max(1.0, settings.expectedSampleRate)
        let configuredDt = 1.0 / configuredRate
        let timestampIsMonotonic = timestamp.isFinite
            && (lastTimestamp == nil || timestamp > (lastTimestamp ?? timestamp))
        let rawDt: TimeInterval? = {
            guard let lastTimestamp,
                  lastTimestamp.isFinite,
                  timestampIsMonotonic else {
                return nil
            }
            return timestamp - lastTimestamp
        }()

        if let rawDt,
           rawDt >= configuredDt * 0.5,
           rawDt <= configuredDt * 1.5 {
            if observedDtEMA == 0 {
                observedDtEMA = rawDt
            } else {
                observedDtEMA = observedAlpha * rawDt + (1 - observedAlpha) * observedDtEMA
            }
        }

        var expectedRate = configuredRate
        if settings.autoTuneSampleRate, observedDtEMA > 0 {
            let observedRate = 1.0 / observedDtEMA
            expectedRate = min(configuredRate * 1.25, max(configuredRate * 0.75, observedRate))
        }

        let expectedDt = 1.0 / expectedRate
        // Never extrapolate one current gyro sample across a long transport
        // outage. That produces a cursor jump after the connection recovers.
        let maxDt = expectedDt * 2.0
        let dt: Double
        if let rawDt {
            dt = min(max(0.001, rawDt), maxDt)
            if rawDt > maxDt {
                yawFilter.reset()
                pitchFilter.reset()
            }
        } else {
            dt = expectedDt
        }
        // A duplicate, invalid, or regressing timestamp gets nominal dt for
        // this sample but must not poison the next valid interval.
        if timestampIsMonotonic {
            lastTimestamp = timestamp
        }

        // 3. Update bias estimation when stationary
        let previousNeutralUpdate = lastNeutralUpdate
        updateBias(x: x, y: y, z: z, threshold: settings.biasMotionThreshold)
        if settings.autoNeutralEnabled, timestampIsMonotonic {
            updateAutoNeutral(
                rawX: x,
                rawY: y,
                rawZ: z,
                gyroScale: settings.gyroScale,
                timestamp: timestamp
            )
        }

        // 4. Apply bias correction
        let calibratedX = x - biasX
        let calibratedY = y - biasY
        _ = z - biasZ  // Z (roll) not used for mouse control

        // 5. Convert to degrees/second
        // Based on PlayStation Sense controller characteristics:
        // Y axis = left/right pointing (yaw), X axis = up/down tilting (pitch)
        let degreesYaw = calibratedY * settings.gyroScale
        let degreesPitch = calibratedX * settings.gyroScale

        // Apply axis inversions for natural direction
        let yaw = -degreesYaw    // Invert for natural left/right
        let pitch = -degreesPitch  // Invert for natural up/down

        // 6. Apply One Euro filter (if enabled)
        var filteredYaw = yaw
        var filteredPitch = pitch

        if lastFilterEnabled != settings.filterEnabled {
            yawFilter.reset()
            pitchFilter.reset()
            lastSpeed = 0
            lastJerkEMA = 0
            lastFilterEnabled = settings.filterEnabled
        }

        if settings.filterEnabled {
            // Update filter parameters
            let speedSquared = yaw * yaw + pitch * pitch
            let speed = sqrt(speedSquared)
            let adaptiveBeta = computeAdaptiveBeta(
                speed: speed,
                baseBeta: settings.beta,
                dt: dt,
                mode: settings.adaptiveSmoothingMode
            )
            lastAdaptiveBeta = adaptiveBeta

            yawFilter.minCutoff = settings.minCutoff
            yawFilter.beta = adaptiveBeta
            yawFilter.fallbackRate = expectedRate
            pitchFilter.minCutoff = settings.minCutoff
            pitchFilter.beta = adaptiveBeta
            pitchFilter.fallbackRate = expectedRate

            filteredYaw = yawFilter.filter(value: yaw, timestamp: timestamp)
            filteredPitch = pitchFilter.filter(value: pitch, timestamp: timestamp)
        } else {
            lastAdaptiveBeta = nil
        }

        // 8. Calculate speed for acceleration curve
        let rawSpeed = sqrt(yaw * yaw + pitch * pitch)
        let filteredSpeed = sqrt(filteredYaw * filteredYaw + filteredPitch * filteredPitch)
        let smoothedSpeed = smoothSpeed(filteredSpeed, dt: dt)
        currentSpeed = smoothedSpeed  // Store for UI visualization

        // 9. Apply acceleration curve (parametric formula)
        let accelGain = computeParametricGain(
            speed: smoothedSpeed,
            rampSpeed: settings.effectiveRampSpeed,
            exponent: settings.effectiveCurveExponent,
            cap: settings.effectiveSensitivityCap
        )

        // 10. Convert to mouse deltas
        let scale = settings.sensitivity * 0.1
        let dx = CGFloat(filteredYaw * dt * scale * accelGain)
        let dy = CGFloat(filteredPitch * dt * scale * accelGain)
        let cursorDeltaMagnitude = sqrt(Double(dx * dx + dy * dy))
        let computedCursorSpeed = cursorDeltaMagnitude / max(dt, 0.001)

        lastResponseSample = GyroResponseSample(
            deltaTime: dt,
            rawSpeed: rawSpeed,
            filteredSpeed: filteredSpeed,
            accelerationSpeed: smoothedSpeed,
            accelerationGain: accelGain,
            computedCursorSpeed: computedCursorSpeed,
            biasX: biasX * settings.gyroScale,
            biasY: biasY * settings.gyroScale,
            biasZ: biasZ * settings.gyroScale,
            filterEnabled: settings.filterEnabled,
            didAutoNeutralUpdate: lastNeutralUpdate != previousNeutralUpdate
        )

        // Capture debug snapshot
        let observedRate = observedDtEMA > 0 ? (1.0 / observedDtEMA) : configuredRate
        lastDebugState = DebugState(
            biasX: biasX * settings.gyroScale,
            biasY: biasY * settings.gyroScale,
            biasZ: biasZ * settings.gyroScale,
            calibrated: biasEstablished,
            observedSampleRate: observedRate,
            lastNeutralUpdate: lastNeutralUpdate
        )

        return (dx, dy)
    }

    // MARK: - Bias Estimation

    private func updateBias(x: Double, y: Double, z: Double, threshold: Double) {
        guard !biasEstablished else { return }
        let magnitudeSquared = x * x + y * y + z * z
        if magnitudeSquared < threshold * threshold {
            let old = biasBuffer[biasIndex]
            biasBuffer[biasIndex] = (x, y, z)
            biasIndex = (biasIndex + 1) % maxBiasSamples
            if biasCount < maxBiasSamples {
                biasCount += 1
                biasSum.x += x
                biasSum.y += y
                biasSum.z += z
            } else {
                // Rolling sum: subtract old, add new
                biasSum.x += x - old.x
                biasSum.y += y - old.y
                biasSum.z += z - old.z
            }

            if biasCount >= maxBiasSamples / 2 {
                let count = Double(biasCount)
                biasX = biasSum.x / count
                biasY = biasSum.y / count
                biasZ = biasSum.z / count
                biasEstablished = true
            }
        } else {
            // Clear buffer when motion exceeds threshold
            biasCount = 0
            biasIndex = 0
            biasSum = (0, 0, 0)
        }
    }

    private func updateAutoNeutral(
        rawX: Double,
        rawY: Double,
        rawZ: Double,
        gyroScale: Double,
        timestamp: TimeInterval
    ) {
        // Low variance alone is insufficient: a deliberate constant-speed
        // rotation also has low variance. Once a bias is established, require
        // the candidate neutral to remain very close to it.
        let degX = rawX * gyroScale
        let degY = rawY * gyroScale
        let degZ = rawZ * gyroScale

        let minDuration: TimeInterval = 0.6
        let minSamples: Int = 20
        let cooldown: TimeInterval = 2.0
        let maximumStartupBias = 8.0
        let maximumEstablishedResidual = 3.0

        let absoluteMagnitude = sqrt(degX * degX + degY * degY + degZ * degZ)
        let residualX = (rawX - biasX) * gyroScale
        let residualY = (rawY - biasY) * gyroScale
        let residualZ = (rawZ - biasZ) * gyroScale
        let residualMagnitude = sqrt(
            residualX * residualX + residualY * residualY + residualZ * residualZ
        )
        let clearlyMoving = biasEstablished
            ? residualMagnitude > maximumEstablishedResidual
            : absoluteMagnitude > maximumStartupBias

        if clearlyMoving {
            resetNeutralAccumulator()
            return
        }

        if neutralStart == nil {
            neutralStart = timestamp
            neutralAccumulator = (rawX, rawY, rawZ)
            neutralSumSquares = (rawX * rawX, rawY * rawY, rawZ * rawZ)
            neutralCount = 1
        } else {
            neutralAccumulator.x += rawX
            neutralAccumulator.y += rawY
            neutralAccumulator.z += rawZ
            neutralSumSquares.x += rawX * rawX
            neutralSumSquares.y += rawY * rawY
            neutralSumSquares.z += rawZ * rawZ
            neutralCount += 1
        }

        if let start = neutralStart,
           timestamp - start >= minDuration,
           neutralCount >= minSamples,
           (lastNeutralUpdate == nil || timestamp - (lastNeutralUpdate ?? 0) >= cooldown) {
            let inv = 1.0 / Double(neutralCount)
            let avgX = neutralAccumulator.x * inv
            let avgY = neutralAccumulator.y * inv
            let avgZ = neutralAccumulator.z * inv
            let varX = max(0, neutralSumSquares.x * inv - avgX * avgX)
            let varY = max(0, neutralSumSquares.y * inv - avgY * avgY)
            let varZ = max(0, neutralSumSquares.z * inv - avgZ * avgZ)
            let stdThreshold: Double = 0.35  // deg/s
            let stillnessPass =
                sqrt(varX) * gyroScale < stdThreshold &&
                sqrt(varY) * gyroScale < stdThreshold &&
                sqrt(varZ) * gyroScale < stdThreshold
            let candidateMagnitude = sqrt(
                avgX * avgX + avgY * avgY + avgZ * avgZ
            ) * gyroScale
            let candidateShift = sqrt(
                (avgX - biasX) * (avgX - biasX)
                    + (avgY - biasY) * (avgY - biasY)
                    + (avgZ - biasZ) * (avgZ - biasZ)
            ) * gyroScale
            let plausibleNeutral = biasEstablished
                ? candidateShift <= 0.2
                : candidateMagnitude <= maximumStartupBias

            if stillnessPass && plausibleNeutral {
                // Force bias to the observed quiet average
                biasX = avgX
                biasY = avgY
                biasZ = avgZ
                biasEstablished = true
                for i in 0..<maxBiasSamples {
                    biasBuffer[i] = (avgX, avgY, avgZ)
                }
                biasCount = maxBiasSamples
                biasIndex = 0
                biasSum = (avgX * Double(maxBiasSamples), avgY * Double(maxBiasSamples), avgZ * Double(maxBiasSamples))
                lastNeutralUpdate = timestamp
            }
            resetNeutralAccumulator()
        }
    }

    private func resetNeutralAccumulator() {
        neutralStart = nil
        neutralAccumulator = (0, 0, 0)
        neutralSumSquares = (0, 0, 0)
        neutralCount = 0
    }

    // MARK: - Adaptive Smoothing

    /// Compute adaptive beta based on motion characteristics
    private func computeAdaptiveBeta(
        speed: Double,
        baseBeta: Double,
        dt: Double,
        mode: AdaptiveSmoothingMode
    ) -> Double {
        let clampedBaseBeta = min(2.0, max(0, baseBeta))
        let previousSpeed = lastSpeed
        lastSpeed = speed

        switch mode {
        case .off:
            return clampedBaseBeta

        case .speed:
            let speedTerm = min(1.0, speed / 150.0)
            let boost = speedTerm * 0.2
            return min(2.0, clampedBaseBeta + boost)

        case .speedAndJerk:
            let jerk = abs(speed - previousSpeed) / max(dt, 0.001)

            let jerkAlpha = Self.emaAlpha(dt: dt, timeConstant: jerkSmoothingTimeConstant)
            lastJerkEMA = jerkAlpha * jerk + (1 - jerkAlpha) * lastJerkEMA

            let speedTerm = min(1.0, speed / 150.0)
            let jerkTerm = min(1.0, lastJerkEMA / 200.0)
            let boost = max(speedTerm, jerkTerm) * 0.2
            return min(2.0, clampedBaseBeta + boost)
        }
    }

    /// Smooth speed with a time-based EMA so acceleration response does not
    /// become slower merely because transport callback frequency dropped.
    private func smoothSpeed(_ instantaneous: Double, dt: TimeInterval) -> Double {
        let alpha = Self.emaAlpha(dt: dt, timeConstant: speedSmoothingTimeConstant)
        lastSpeedEMA = alpha * instantaneous + (1 - alpha) * lastSpeedEMA
        return lastSpeedEMA
    }

    private static func emaAlpha(dt: TimeInterval, timeConstant: TimeInterval) -> Double {
        let safeDt = max(0.001, dt)
        let safeTimeConstant = max(0.001, timeConstant)
        return 1 - exp(-safeDt / safeTimeConstant)
    }

    // MARK: - Acceleration Curve

    /// Compute acceleration gain using parametric curve formula
    /// - Parameters:
    ///   - speed: Current angular velocity (°/s)
    ///   - rampSpeed: Speed at which gain reaches cap (°/s)
    ///   - exponent: Curve shape (< 1 = concave, 1 = linear, > 1 = convex)
    ///   - cap: Maximum gain multiplier
    /// - Returns: Gain multiplier (1.0 to cap)
    private func computeParametricGain(
        speed: Double,
        rampSpeed: Double,
        exponent: Double,
        cap: Double
    ) -> Double {
        // Ensure valid parameters
        let effectiveCap = max(1.0, cap)
        let effectiveRamp = max(1.0, rampSpeed)

        // Normalize speed to 0-1 range (0 to rampSpeed)
        let normalized = min(1.0, speed / effectiveRamp)

        // Apply power curve
        let curved = pow(normalized, exponent)

        // Scale to gain range
        return 1.0 + curved * (effectiveCap - 1.0)
    }

    // MARK: - Reset

    /// Reset the processor state
    func reset() {
        biasX = 0
        biasY = 0
        biasZ = 0
        biasBuffer = Array(repeating: (0, 0, 0), count: maxBiasSamples)
        biasCount = 0
        biasIndex = 0
        biasSum = (0, 0, 0)
        biasEstablished = false
        lastTimestamp = nil
        yawFilter.reset()
        pitchFilter.reset()
        lastFilterEnabled = nil
        lastSpeed = 0
        lastSpeedEMA = 0
        lastJerkEMA = 0
        currentSpeed = 0
        lastAdaptiveBeta = nil
        observedDtEMA = 0
        resetNeutralAccumulator()
        lastNeutralUpdate = nil
        lastDebugState = nil
        lastResponseSample = nil
    }

    /// Whether bias calibration is complete
    var isCalibrated: Bool {
        biasEstablished
    }
}
