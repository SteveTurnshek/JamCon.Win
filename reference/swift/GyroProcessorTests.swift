import XCTest
@testable import JamCon

final class GyroProcessorTests: XCTestCase {
    func testAccelerationSmoothingDependsOnElapsedTimeNotReportCount() throws {
        let speedAt30Hz = accelerationSpeed(after: 0.12, reportRate: 30)
        let speedAt60Hz = accelerationSpeed(after: 0.12, reportRate: 60)

        XCTAssertEqual(speedAt30Hz, speedAt60Hz, accuracy: 0.0001)
        XCTAssertGreaterThan(speedAt60Hz, 95)
    }

    func testCursorDistanceDependsOnElapsedTimeNotReportRate() {
        let distanceAt66Hz = cursorDistance(over: 1, reportRate: 66)
        let distanceAt133Hz = cursorDistance(over: 1, reportRate: 133)

        XCTAssertEqual(distanceAt66Hz, distanceAt133Hz, accuracy: 0.2)
    }

    func testAutoTuneIgnoresTransportStallAndCapsExtrapolation() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.autoTuneSampleRate = true
        settings.expectedSampleRate = 66

        var timestamp = 0.0
        _ = processor.process(rawX: 0, rawY: 20, rawZ: 0, timestamp: timestamp, settings: settings)
        for _ in 0..<40 {
            timestamp += 1.0 / 66.0
            _ = processor.process(rawX: 0, rawY: 20, rawZ: 0, timestamp: timestamp, settings: settings)
        }

        timestamp += 0.3
        _ = processor.process(rawX: 0, rawY: 20, rawZ: 0, timestamp: timestamp, settings: settings)

        let response = try XCTUnwrap(processor.lastResponseSample)
        let debug = try XCTUnwrap(processor.lastDebugState)
        XCTAssertEqual(response.deltaTime, 2.0 / 66.0, accuracy: 0.0001)
        XCTAssertEqual(debug.observedSampleRate, 66, accuracy: 0.5)
    }

    func testAutoNeutralCalibratesQuietStartupBias() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.autoNeutralEnabled = true
        settings.gyroScale = 0.1

        processConstant(
            processor: processor,
            rawX: 40,
            from: 0,
            through: 0.8,
            reportRate: 60,
            settings: settings
        )

        XCTAssertTrue(processor.isCalibrated)
        XCTAssertEqual(try XCTUnwrap(processor.lastDebugState).biasX, 4, accuracy: 0.001)
    }

    func testAutoNeutralDoesNotAbsorbEstablishedConstantSpeedMotion() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.autoNeutralEnabled = true
        settings.gyroScale = 0.1

        processConstant(
            processor: processor,
            rawX: 40,
            from: 0,
            through: 0.8,
            reportRate: 60,
            settings: settings
        )
        processConstant(
            processor: processor,
            rawX: 43,
            from: 0.8 + 1.0 / 60.0,
            through: 3.2,
            reportRate: 60,
            settings: settings
        )

        let debug = try XCTUnwrap(processor.lastDebugState)
        let response = try XCTUnwrap(processor.lastResponseSample)
        XCTAssertEqual(debug.biasX, 4, accuracy: 0.001)
        XCTAssertEqual(response.rawSpeed, 0.3, accuracy: 0.001)
    }

    func testFilterStateResetsAcrossEnableTransitions() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.filterEnabled = true
        settings.minCutoff = 0.5
        settings.beta = 0
        settings.adaptiveSmoothingMode = .off

        _ = processor.process(rawX: 0, rawY: 100, rawZ: 0, timestamp: 0, settings: settings)
        _ = processor.process(rawX: 0, rawY: 100, rawZ: 0, timestamp: 1.0 / 60.0, settings: settings)

        settings.filterEnabled = false
        _ = processor.process(rawX: 0, rawY: 0, rawZ: 0, timestamp: 2.0 / 60.0, settings: settings)

        settings.filterEnabled = true
        _ = processor.process(rawX: 0, rawY: 0, rawZ: 0, timestamp: 3.0 / 60.0, settings: settings)

        XCTAssertEqual(try XCTUnwrap(processor.lastResponseSample).filteredSpeed, 0, accuracy: 0.0001)
    }

    func testRegressingTimestampDoesNotPoisonNextValidDelta() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.expectedSampleRate = 60

        _ = processor.process(rawX: 0, rawY: 10, rawZ: 0, timestamp: 1, settings: settings)
        _ = processor.process(rawX: 0, rawY: 10, rawZ: 0, timestamp: 0.5, settings: settings)
        _ = processor.process(
            rawX: 0,
            rawY: 10,
            rawZ: 0,
            timestamp: 1 + 1.0 / 60.0,
            settings: settings
        )

        XCTAssertEqual(
            try XCTUnwrap(processor.lastResponseSample).deltaTime,
            1.0 / 60.0,
            accuracy: 0.0001
        )
    }

    func testAdaptiveBetaHonorsValuesAboveOneAndItsDocumentedCap() throws {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.filterEnabled = true
        settings.beta = 1.5
        settings.adaptiveSmoothingMode = .speed

        _ = processor.process(rawX: 0, rawY: 150, rawZ: 0, timestamp: 0, settings: settings)
        XCTAssertEqual(try XCTUnwrap(processor.lastAdaptiveBeta), 1.7, accuracy: 0.0001)

        settings.beta = 2
        _ = processor.process(
            rawX: 0,
            rawY: 150,
            rawZ: 0,
            timestamp: 1.0 / 60.0,
            settings: settings
        )
        XCTAssertEqual(try XCTUnwrap(processor.lastAdaptiveBeta), 2, accuracy: 0.0001)
    }

    private func accelerationSpeed(after duration: TimeInterval, reportRate: Double) -> Double {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.expectedSampleRate = reportRate

        var timestamp = 0.0
        _ = processor.process(rawX: 0, rawY: 0, rawZ: 0, timestamp: timestamp, settings: settings)
        let step = 1.0 / reportRate
        while timestamp + step < duration {
            timestamp += step
            _ = processor.process(rawX: 0, rawY: 100, rawZ: 0, timestamp: timestamp, settings: settings)
        }
        _ = processor.process(rawX: 0, rawY: 100, rawZ: 0, timestamp: duration, settings: settings)
        return processor.lastResponseSample?.accelerationSpeed ?? 0
    }

    private func cursorDistance(over duration: TimeInterval, reportRate: Double) -> Double {
        let processor = GyroProcessor()
        var settings = baseSettings()
        settings.expectedSampleRate = reportRate
        settings.sensitivityCap = 1

        var timestamp = 0.0
        _ = processor.process(rawX: 0, rawY: 0, rawZ: 0, timestamp: timestamp, settings: settings)
        var distance = 0.0
        let step = 1.0 / reportRate
        while timestamp + step < duration {
            timestamp += step
            let delta = processor.process(
                rawX: 0,
                rawY: 100,
                rawZ: 0,
                timestamp: timestamp,
                settings: settings
            )
            distance += Double(delta?.dx ?? 0)
        }
        let delta = processor.process(
            rawX: 0,
            rawY: 100,
            rawZ: 0,
            timestamp: duration,
            settings: settings
        )
        distance += Double(delta?.dx ?? 0)
        return distance
    }

    private func processConstant(
        processor: GyroProcessor,
        rawX: Int16,
        from start: TimeInterval,
        through end: TimeInterval,
        reportRate: Double,
        settings: GyroSettingsState
    ) {
        var timestamp = start
        while timestamp <= end {
            _ = processor.process(
                rawX: rawX,
                rawY: 0,
                rawZ: 0,
                timestamp: timestamp,
                settings: settings
            )
            timestamp += 1.0 / reportRate
        }
    }

    private func baseSettings() -> GyroSettingsState {
        var settings = GyroSettingsState.defaultForKind(.joyCon)
        settings.gyroScale = 1
        settings.filterEnabled = false
        settings.autoTuneSampleRate = false
        settings.autoNeutralEnabled = false
        settings.biasMotionThreshold = 0
        settings.sensitivity = 10
        settings.rampSpeed = 100
        settings.curveExponent = 1
        settings.sensitivityCap = 10
        return settings
    }
}

final class OneEuroFilterTests: XCTestCase {
    func testDerivativeUsesPreviousRawSample() {
        let filter = OneEuroFilter()
        filter.minCutoff = 1
        filter.beta = 1
        filter.derivativeCutoff = 1
        filter.fallbackRate = 10

        XCTAssertEqual(filter.filter(value: 0, timestamp: 0), 0, accuracy: 0.000_001)
        let second = filter.filter(value: 10, timestamp: 0.1)
        let third = filter.filter(value: 10, timestamp: 0.2)

        let derivativeAlpha = oneEuroAlpha(cutoff: 1, dt: 0.1)
        let firstDerivative = derivativeAlpha * 100
        let firstValueAlpha = oneEuroAlpha(cutoff: 1 + firstDerivative, dt: 0.1)
        let expectedSecond = firstValueAlpha * 10
        let secondDerivative = (1 - derivativeAlpha) * firstDerivative
        let secondValueAlpha = oneEuroAlpha(cutoff: 1 + secondDerivative, dt: 0.1)
        let expectedThird = secondValueAlpha * 10 + (1 - secondValueAlpha) * expectedSecond

        XCTAssertEqual(second, expectedSecond, accuracy: 0.000_001)
        XCTAssertEqual(third, expectedThird, accuracy: 0.000_001)
    }

    func testRegressingTimestampUsesFallbackWithoutMovingClockBackward() {
        let filter = OneEuroFilter()
        filter.minCutoff = 1
        filter.beta = 0
        filter.fallbackRate = 10

        _ = filter.filter(value: 0, timestamp: 1)
        _ = filter.filter(value: 10, timestamp: 0.5)
        let result = filter.filter(value: 10, timestamp: 1.1)

        let alpha = oneEuroAlpha(cutoff: 1, dt: 0.1)
        let afterFallback = alpha * 10
        let expected = alpha * 10 + (1 - alpha) * afterFallback
        XCTAssertEqual(result, expected, accuracy: 0.000_001)
    }

    private func oneEuroAlpha(cutoff: Double, dt: Double) -> Double {
        let tau = 1.0 / (2.0 * Double.pi * cutoff)
        return 1.0 / (1.0 + tau / dt)
    }
}
