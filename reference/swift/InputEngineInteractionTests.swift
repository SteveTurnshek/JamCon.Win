import XCTest
import QuartzCore
@testable import JamCon

final class InputEngineInteractionTests: XCTestCase {
    func testDuplicatePressAndMappingEditStillReleaseCapturedAction() {
        let harness = makeHarness(actions: ButtonActions(press: .mouseClick(.left)))

        harness.process(trigger: 0, timestamp: 1.0)   // Prime
        harness.process(trigger: 255, timestamp: 1.1)
        harness.process(trigger: 255, timestamp: 1.2) // Duplicate snapshot

        var edited = SenseButtonMappingProfile()
        edited.setActions(ButtonActions(press: .mouseClick(.right)), for: .trigger)
        harness.settings.update { $0.senseButtonMappings[.senseRight] = edited }
        harness.process(trigger: 0, timestamp: 1.3)

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.left, isPressed: true),
            .mouseButton(.left, isPressed: false),
        ])
        harness.shutdown()
    }

    func testHoldFiresDeterministicallyAndSuppressesTapAction() {
        let tap = KeyCombo(keyCode: 8)
        let hold = KeyCombo(keyCode: 9, modifiers: .maskCommand)
        let harness = makeHarness(actions: ButtonActions(press: .keyPress(tap), hold: .keyPress(hold)))

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        XCTAssertTrue(harness.backend.events.isEmpty)

        harness.fireScheduledHolds()
        harness.process(trigger: 0, timestamp: 1.2)

        XCTAssertEqual(harness.backend.events, [
            .key(hold, isPressed: true),
            .key(hold, isPressed: false),
        ])
        harness.shutdown()
    }

    func testShortPressProducesBalancedTap() {
        let combo = KeyCombo(keyCode: 53)
        let harness = makeHarness(actions: ButtonActions(press: .keyPress(combo), hold: .keyPress(KeyCombo(keyCode: 49))))

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        harness.process(trigger: 0, timestamp: 1.2)

        XCTAssertEqual(harness.backend.events, [
            .key(combo, isPressed: true),
            .key(combo, isPressed: false),
        ])
        harness.shutdown()
    }

    func testImmediateMousePressCanAlsoFireAndReleaseHoldAction() {
        let hold = KeyCombo(keyCode: 49)
        let harness = makeHarness(
            actions: ButtonActions(
                press: .mouseClick(.left),
                hold: .keyPress(hold)
            )
        )

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        harness.fireScheduledHolds()
        harness.process(trigger: 0, timestamp: 1.2)

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.left, isPressed: true),
            .key(hold, isPressed: true),
            .mouseButton(.left, isPressed: false),
            .key(hold, isPressed: false),
        ])
        harness.shutdown()
    }

    func testGlobalDisableReleasesMouseDownBeforeIgnoringReleaseReport() {
        let harness = makeHarness(actions: ButtonActions(press: .mouseClick(.left)))

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        harness.settings.update { $0.isEnabled = false }
        harness.engine.setInputEnabled(false)
        harness.process(trigger: 0, timestamp: 1.2)

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.left, isPressed: true),
            .mouseButton(.left, isPressed: false),
        ])
        harness.shutdown()
    }

    func testDeviceUnmanageReleasesOutputWhenPhysicalReleaseWasLost() {
        let harness = makeHarness(actions: ButtonActions(press: .mouseClick(.middle)))

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        harness.engine.setDeviceManaged(id: harness.deviceID, kind: .sense, isLeft: false, managed: false)

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.middle, isPressed: true),
            .mouseButton(.middle, isPressed: false),
        ])
        harness.shutdown()
    }

    func testEngineStopReleasesHeldOutputAndCancelsPendingHold() {
        let harness = makeHarness(
            actions: ButtonActions(
                press: .mouseClick(.right),
                hold: .keyPress(KeyCombo(keyCode: 49))
            )
        )

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 255, timestamp: 1.1)
        harness.engine.stop()
        harness.fireScheduledHolds()

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.right, isPressed: true),
            .mouseButton(.right, isPressed: false),
        ])
    }

    func testSenseTriggerHysteresisPreventsThresholdChatter() {
        let harness = makeHarness(actions: ButtonActions(press: .mouseClick(.left)))

        harness.process(trigger: 0, timestamp: 1.0)
        harness.process(trigger: 128, timestamp: 1.1)
        harness.process(trigger: 125, timestamp: 1.2)
        harness.process(trigger: 121, timestamp: 1.3)
        harness.process(trigger: 120, timestamp: 1.4)

        XCTAssertEqual(harness.backend.events, [
            .mouseButton(.left, isPressed: true),
            .mouseButton(.left, isPressed: false),
        ])
        harness.shutdown()
    }

    func testSenseJoystickScrollTimingIsReportRateIndependentAndBounded() {
        var timing = JoystickScrollTiming()

        XCTAssertEqual(timing.frameScale(at: 1, nominalRate: 60), 1)
        XCTAssertEqual(timing.frameScale(at: 1 + 1.0 / 120.0, nominalRate: 60), 0.5, accuracy: 0.000_001)
        XCTAssertEqual(timing.frameScale(at: 1 + 1.0 / 60.0, nominalRate: 60), 0.5, accuracy: 0.000_001)
        XCTAssertEqual(timing.frameScale(at: 2, nominalRate: 60), 3, accuracy: 0.000_001)
        XCTAssertEqual(timing.frameScale(at: 1.5, nominalRate: 60), 0)
    }

    func testPixelScrollAccumulatorPreservesSubpixelMotion() {
        var accumulator = PixelScrollAccumulator()

        let first = accumulator.consume(dx: 0.4, dy: -0.4)
        let second = accumulator.consume(dx: 0.4, dy: -0.4)
        let third = accumulator.consume(dx: 0.4, dy: -0.4)

        XCTAssertEqual(first.x, 0)
        XCTAssertEqual(first.y, 0)
        XCTAssertEqual(second.x, 0)
        XCTAssertEqual(second.y, 0)
        XCTAssertEqual(third.x, 1)
        XCTAssertEqual(third.y, -1)
    }

    private func makeHarness(actions: ButtonActions) -> SenseEngineHarness {
        SenseEngineHarness(actions: actions)
    }
}

private final class SenseEngineHarness {
    let settings = SettingsStore()
    let backend = EngineRecordingBackend()
    let scheduler = ManualHoldScheduler()
    let engine: InputEngine
    let deviceID = "sense-right"

    init(actions: ButtonActions) {
        var mapping = SenseButtonMappingProfile(holdThreshold: 10)
        mapping.setActions(actions, for: .trigger)
        settings.update {
            $0.senseButtonMappings[.senseRight] = mapping
            $0.cursorControlEnabledByProfile[.senseRight] = false
        }

        let executor = ActionExecutor(eventBackend: backend)
        engine = InputEngine(
            settings: settings,
            debugBuffer: DebugBuffer(),
            actionExecutor: executor,
            holdScheduler: scheduler
        )

        engine.engineQueue.sync {
            engine.isRunning = true
            engine.senseDevices[deviceID] = InputEngine.SenseDeviceState(id: deviceID, profile: .senseRight)
        }
    }

    func process(trigger: UInt8, timestamp _: TimeInterval) {
        var bytes = [UInt8](repeating: 0, count: SenseHIDProtocol.reportLength)
        bytes[0] = UInt8(SenseHIDProtocol.inputReportID)
        bytes[SenseHIDProtocol.Offset.triggerAnalog] = trigger
        let motion = try! SenseInputReportDecoder.decode(bytes).motion
        let timestamp = CACurrentMediaTime()
        let report = InputDeviceFrame(
            backend: engine.senseBackend.backendDescriptor,
            deviceID: deviceID,
            reportID: SenseHIDProtocol.inputReportID,
            bytes: bytes,
            motion: .single(motion),
            timestamp: timestamp,
            receivedTimestamp: timestamp,
            inputTimestamp: nil,
            timestampSource: .hostReceipt
        )

        engine.engineQueue.sync {
            engine.processSenseReport(report)
        }
    }

    func fireScheduledHolds() {
        engine.engineQueue.sync {
            scheduler.fireAll()
        }
    }

    func shutdown() {
        engine.stop()
    }
}

private final class ManualHoldScheduler: HoldScheduling {
    private var pending: [DispatchWorkItem] = []

    func schedule(_ workItem: DispatchWorkItem, after delay: TimeInterval, on queue: DispatchQueue) {
        pending.append(workItem)
    }

    func fireAll() {
        let work = pending
        pending.removeAll()
        for item in work where !item.isCancelled {
            item.perform()
        }
    }
}

private final class EngineRecordingBackend: SyntheticEventBackend {
    private(set) var events: [SyntheticOutputEvent] = []

    func post(_ event: SyntheticOutputEvent) {
        events.append(event)
    }
}
