import XCTest
@testable import JamCon

final class JoyCon2BLEInputDeviceBackendTests: XCTestCase {
    func testJoyCon2UsesIndependentProfileAndExposesCButtonOnRightOnly() throws {
        let profile = ControllerProfile.joyCon2Right

        XCTAssertEqual(profile.displayName, "Joy-Con 2 Right")
        XCTAssertEqual(profile.persistenceKey, "joyCon2.right")
        XCTAssertNotEqual(profile.persistenceKey, ControllerProfile.joyConRight.persistenceKey)
        XCTAssertTrue(JoyConLogicalButton.availableButtons(for: profile).contains(.c))
        XCTAssertFalse(JoyConLogicalButton.availableButtons(for: .joyCon2Left).contains(.c))

        var report = [UInt8](repeating: 0, count: 0x3F)
        report[5] = 1 << 6
        let controls = try XCTUnwrap(JoyCon2BLEProtocol.controlBytes(from: report))
        XCTAssertTrue(JoyConButtonMapping(isLeft: false).isPressed(.c, in: controls))
    }

    func testCanonicalJoyConDefaultsMirrorOneHandedSemanticsAcrossGenerations() {
        let backForward = ButtonActions(
            press: JoyConButtonMappingProfile.browserBack,
            hold: JoyConButtonMappingProfile.browserForward
        )
        for profile in [ControllerProfile.joyConRight, .joyCon2Right] {
            let mapping = JoyConButtonMappingProfile.defaultProfile(for: profile)
            XCTAssertEqual(mapping.actions(for: .zr), ButtonActions(press: .mouseClick(.left)))
            XCTAssertEqual(mapping.actions(for: .r), ButtonActions(press: .mouseClick(.right)))
            XCTAssertEqual(mapping.actions(for: .plus), ButtonActions(press: .mouseClick(.middle)))
            XCTAssertEqual(mapping.actions(for: .stickClick), backForward)
            XCTAssertEqual(
                mapping.actions(for: .home),
                ButtonActions(
                    press: .systemAction(.missionControl),
                    hold: .systemAction(.playPause)
                )
            )
            XCTAssertEqual(mapping.actions(for: .x), ButtonActions(press: .drag))
            XCTAssertEqual(mapping.actions(for: .b), ButtonActions(press: .radialMenu))
        }

        for profile in [ControllerProfile.joyConLeft, .joyCon2Left] {
            let mapping = JoyConButtonMappingProfile.defaultProfile(for: profile)
            XCTAssertEqual(mapping.actions(for: .zl), ButtonActions(press: .mouseClick(.left)))
            XCTAssertEqual(mapping.actions(for: .l), ButtonActions(press: .mouseClick(.right)))
            XCTAssertEqual(mapping.actions(for: .minus), ButtonActions(press: .mouseClick(.middle)))
            XCTAssertEqual(mapping.actions(for: .stickClick), backForward)
            XCTAssertEqual(
                mapping.actions(for: .capture),
                ButtonActions(
                    press: .systemAction(.missionControl),
                    hold: .systemAction(.playPause)
                )
            )
            XCTAssertEqual(mapping.actions(for: .dpadUp), ButtonActions(press: .drag))
            XCTAssertEqual(mapping.actions(for: .dpadDown), ButtonActions(press: .radialMenu))
        }

        XCTAssertEqual(
            JoyConButtonMappingProfile.defaultProfile(for: .joyCon2Right).actions(for: .c),
            ButtonActions()
        )
        XCTAssertEqual(SettingsStore.InputSettings.defaultJoystickScrollSpeed, 8.0)
        XCTAssertEqual(SettingsStore.InputSettings.defaultJoystickScrollAcceleration, 3.0)
        XCTAssertEqual(
            SettingsStore.InputSettings().joystickScrollSpeed,
            SettingsStore.InputSettings.defaultJoystickScrollSpeed
        )
    }

    func testLegacyJoyConNavigationMigrationChangesOnlyExactStockActions() {
        var right = JoyConButtonMappingProfile()
        right.setActions(
            ButtonActions(press: .systemAction(.playPause)),
            for: .plus
        )
        right.setActions(
            ButtonActions(press: .mouseClick(.middle)),
            for: .stickClick
        )
        right.setActions(
            ButtonActions(press: .systemAction(.missionControl)),
            for: .home
        )
        let customRail = ButtonActions(
            press: .keyPress(KeyCombo(keyCode: 125)),
            hold: .keyPress(KeyCombo(keyCode: 30, modifiers: [.maskCommand, .maskShift]))
        )
        right.setActions(customRail, for: .sl)

        XCTAssertTrue(right.migrateLegacyNavigationDefaults(for: .joyConRight))
        XCTAssertEqual(right.actions(for: .plus), ButtonActions(press: .mouseClick(.middle)))
        XCTAssertEqual(
            right.actions(for: .stickClick),
            ButtonActions(
                press: JoyConButtonMappingProfile.browserBack,
                hold: JoyConButtonMappingProfile.browserForward
            )
        )
        XCTAssertEqual(
            right.actions(for: .home),
            ButtonActions(
                press: .systemAction(.missionControl),
                hold: .systemAction(.playPause)
            )
        )
        XCTAssertEqual(right.actions(for: .sl), customRail)
        XCTAssertFalse(right.migrateLegacyNavigationDefaults(for: .joyConRight))

        var left = JoyConButtonMappingProfile()
        left.setActions(
            ButtonActions(press: .systemAction(.playPause)),
            for: .minus
        )
        left.setActions(
            ButtonActions(press: .mouseClick(.middle)),
            for: .stickClick
        )
        left.setActions(
            ButtonActions(press: .systemAction(.missionControl)),
            for: .capture
        )

        XCTAssertTrue(left.migrateLegacyNavigationDefaults(for: .joyConLeft))
        XCTAssertEqual(left.actions(for: .minus), ButtonActions(press: .mouseClick(.middle)))
        XCTAssertEqual(
            left.actions(for: .stickClick),
            ButtonActions(
                press: JoyConButtonMappingProfile.browserBack,
                hold: JoyConButtonMappingProfile.browserForward
            )
        )
        XCTAssertEqual(
            left.actions(for: .capture),
            ButtonActions(
                press: .systemAction(.missionControl),
                hold: .systemAction(.playPause)
            )
        )
        XCTAssertFalse(left.migrateLegacyNavigationDefaults(for: .joyConLeft))

        var customized = JoyConButtonMappingProfile()
        let customMenu = ButtonActions(press: .keyPress(KeyCombo(keyCode: 49)))
        customized.setActions(customMenu, for: .minus)
        XCTAssertFalse(customized.migrateLegacyNavigationDefaults(for: .joyConLeft))
        XCTAssertEqual(customized.actions(for: .minus), customMenu)
    }

    func testLegacyEncodedProfileDefaultsToStandardVariant() throws {
        let data = try XCTUnwrap("{\"kind\":\"joyCon\",\"isLeft\":false}".data(using: .utf8))
        let profile = try JSONDecoder().decode(ControllerProfile.self, from: data)

        XCTAssertEqual(profile, .joyConRight)
        XCTAssertEqual(profile.persistenceKey, "joyCon.right")
    }

    func testJoyCon2VariantCannotLeakIntoAnotherControllerFamily() throws {
        let constructed = ControllerProfile(
            kind: .sense,
            isLeft: false,
            variant: .joyCon2
        )
        let data = try XCTUnwrap(
            "{\"kind\":\"sense\",\"isLeft\":false,\"variant\":\"joyCon2\"}"
                .data(using: .utf8)
        )
        let decoded = try JSONDecoder().decode(ControllerProfile.self, from: data)
        let deviceInfo = ControllerInfo(
            id: "sense",
            name: "Sense",
            productID: 0,
            kind: .sense,
            handedness: .right,
            profileVariant: .joyCon2
        )

        XCTAssertEqual(constructed, .senseRight)
        XCTAssertEqual(decoded, .senseRight)
        XCTAssertEqual(deviceInfo.profileVariant, .standard)
    }

    func testJoyCon2RightUsesMeasuredGripAxesWithoutChangingOriginalJoyCon() {
        let original = GyroRemapper.remap(
            rawX: 100,
            rawY: 200,
            rawZ: 300,
            controllerKind: .joyCon,
            isLeft: false
        )
        let joyCon2 = GyroRemapper.remap(
            rawX: 100,
            rawY: 200,
            rawZ: 300,
            controllerKind: .joyCon,
            isLeft: false,
            profileVariant: .joyCon2
        )

        XCTAssertEqual(original.pitch, 200)
        XCTAssertEqual(original.yaw, -300)
        XCTAssertEqual(joyCon2.pitch, 100)
        XCTAssertEqual(joyCon2.yaw, 300)
        XCTAssertEqual(joyCon2.roll, 200)

        let pipeline = GyroRemapper.process(
            rawX: 100,
            rawY: 200,
            rawZ: 300,
            controllerKind: .joyCon,
            isLeft: false,
            profileVariant: .joyCon2,
            nativeScale: JoyCon2BLEProtocol.gyroScale
        )
        XCTAssertEqual(
            pipeline.normalized.pitch,
            100 * JoyCon2BLEProtocol.gyroScale,
            accuracy: 0.000_001
        )
        XCTAssertEqual(
            pipeline.normalized.yaw,
            300 * JoyCon2BLEProtocol.gyroScale,
            accuracy: 0.000_001
        )
    }

    func testAdvertisementDecoderRecognizesJoyCon2Identity() {
        var data = Data(repeating: 0, count: 13)
        data[5] = 0x7E
        data[6] = 0x05
        data[7] = 0x67
        data[8] = 0x20

        let identity = JoyCon2BLEProtocol.decodeAdvertisement(data)

        XCTAssertEqual(identity?.vendorID, JoyCon2BLEProtocol.nintendoVendorID)
        XCTAssertEqual(identity?.productID, JoyCon2BLEProtocol.leftProductID)
    }

    func testPlaceholderBluetoothNameFallsBackToJoyConIdentity() {
        XCTAssertEqual(
            JoyCon2BLEProtocol.userFacingDeviceName(
                peripheralName: "DeviceName",
                advertisedName: nil,
                handedness: .right
            ),
            "Joy-Con 2 (R)"
        )
        XCTAssertEqual(
            JoyCon2BLEProtocol.userFacingDeviceName(
                peripheralName: "DeviceName",
                advertisedName: "My Joy-Con",
                handedness: .right
            ),
            "My Joy-Con"
        )

        let placeholder = ControllerInfo(
            id: "right",
            name: "DeviceName",
            productID: Int(JoyCon2BLEProtocol.rightProductID),
            kind: .joyCon,
            handedness: .right,
            profileVariant: .joyCon2
        )
        let redundantFallback = ControllerInfo(
            id: "left",
            name: "Joy-Con 2 (L)",
            productID: Int(JoyCon2BLEProtocol.leftProductID),
            kind: .joyCon,
            handedness: .left,
            profileVariant: .joyCon2
        )

        XCTAssertEqual(placeholder.displayName, "Joy-Con 2 Right")
        XCTAssertNil(placeholder.meaningfulTransportName)
        XCTAssertNil(redundantFallback.meaningfulTransportName)

        let mouse = ControllerInfo(
            id: "mouse",
            name: "USB Receiver",
            productID: 0,
            kind: .mouse,
            handedness: .none
        )
        XCTAssertNil(mouse.meaningfulTransportName)
    }

    func testFactoryStickCalibrationReadCommandAndResponse() throws {
        let expected = InputDeviceAnalogStickCalibration(
            centerX: 2050,
            centerY: 2040,
            positiveRangeX: 1500,
            positiveRangeY: 1600,
            negativeRangeX: 1400,
            negativeRangeY: 1300
        )
        let command = JoyCon2BLEProtocol.readPrimaryStickFactoryCalibration
        XCTAssertEqual(command[8], 9)
        XCTAssertEqual(Array(command[12...15]), [0xA8, 0x30, 0x01, 0x00])

        var response = Data(repeating: 0, count: 25)
        response[0] = command[0]
        response[1] = 0x01
        response[3] = command[3]
        response[8] = 9
        response[12...15] = command[12...15]
        writePackedPair(expected.centerX, expected.centerY, to: &response, at: 16)
        writePackedPair(expected.positiveRangeX, expected.positiveRangeY, to: &response, at: 19)
        writePackedPair(expected.negativeRangeX, expected.negativeRangeY, to: &response, at: 22)

        XCTAssertEqual(
            JoyCon2BLEProtocol.decodeStickCalibrationResponse(response),
            expected
        )

        response[12] &+= 1
        XCTAssertNil(JoyCon2BLEProtocol.decodeStickCalibrationResponse(response))
    }

    func testLongHeldScrollPositionCannotWalkStickCenter() {
        var mapping = JoyConButtonMapping(isLeft: false)
        var timestamp = 0.0

        for _ in 0..<45 {
            mapping.calibration.updateAutoCalibration(
                rawX: 2050,
                rawY: 2040,
                timestamp: timestamp
            )
            timestamp += 1.0 / 66.0
        }

        XCTAssertTrue(mapping.calibration.isCalibrated)
        let learnedCenterX = mapping.calibration.centerX
        let learnedCenterY = mapping.calibration.centerY

        var heldReport = [UInt8](repeating: 0, count: JoyConHIDProtocol.reportLength)
        writePackedStick(x: 2300, y: 2040, to: &heldReport, at: JoyConHIDProtocol.Offset.rightStickStart)
        for _ in 0..<(66 * 60 * 5) {
            mapping.calibration.updateAutoCalibration(
                rawX: 2300,
                rawY: 2040,
                timestamp: timestamp
            )
            timestamp += 1.0 / 66.0
        }

        XCTAssertEqual(mapping.calibration.centerX, learnedCenterX, accuracy: 0.001)
        XCTAssertEqual(mapping.calibration.centerY, learnedCenterY, accuracy: 0.001)
        XCTAssertGreaterThan(mapping.joystickPosition(in: heldReport).x, 148)

        var releasedReport = [UInt8](repeating: 0, count: JoyConHIDProtocol.reportLength)
        writePackedStick(x: 2050, y: 2040, to: &releasedReport, at: JoyConHIDProtocol.Offset.rightStickStart)
        XCTAssertEqual(mapping.joystickPosition(in: releasedReport).x, 128)
        XCTAssertEqual(mapping.joystickPosition(in: releasedReport).y, 128)
    }

    func testManualResetRequiresFactoryNeutralBeforeStickOutputResumes() {
        let hardware = InputDeviceAnalogStickCalibration(
            centerX: 2050,
            centerY: 2040,
            positiveRangeX: 1500,
            positiveRangeY: 1600,
            negativeRangeX: 1400,
            negativeRangeY: 1300
        )
        var calibration = JoyConStickCalibration()
        calibration.applyHardwareCalibration(hardware)
        XCTAssertTrue(calibration.isCalibrated)

        calibration.reset(requireNeutralValidation: true)
        calibration.applyHardwareCalibration(hardware)
        XCTAssertFalse(calibration.isCalibrated)

        var timestamp = 0.0
        for _ in 0..<(66 * 60 * 5) {
            calibration.updateAutoCalibration(
                rawX: 2300,
                rawY: 2040,
                timestamp: timestamp
            )
            timestamp += 1.0 / 66.0
        }
        XCTAssertFalse(calibration.isCalibrated)
        XCTAssertEqual(calibration.centerX, Double(hardware.centerX))

        for _ in 0..<45 {
            calibration.updateAutoCalibration(
                rawX: hardware.centerX,
                rawY: hardware.centerY,
                timestamp: timestamp
            )
            timestamp += 1.0 / 66.0
        }
        XCTAssertTrue(calibration.isCalibrated)
    }

    func testInitializationUsesOnlyRequiredFeaturesAndPrefersFastestRate() {
        XCTAssertEqual(
            Array(JoyCon2BLEProtocol.setPlayerOneLED),
            [
                0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00,
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            ]
        )
        XCTAssertEqual(JoyCon2BLEProtocol.featureMask, 0x07)
        XCTAssertEqual(JoyCon2BLEProtocol.setFeatureMask[8], 0x07)
        XCTAssertEqual(JoyCon2BLEProtocol.enableFeatures[8], 0x07)
        XCTAssertEqual(JoyCon2BLEProtocol.preferredReportRate, .hz133)
        XCTAssertEqual(JoyCon2BLEProtocol.productionReportRate, .hz66)
        XCTAssertEqual(
            JoyCon2BLEProtocol.gyroScale,
            0.060_850_646_292_161_775,
            accuracy: 0.000_000_001
        )
        XCTAssertEqual(
            JoyCon2BLEProtocol.reportRateDescriptorUUID,
            "679D5510-5A24-4DEE-9557-95DF80486ECB"
        )
        XCTAssertEqual(JoyCon2BLEReportRate.hz133.descriptorValue, Data([0x85, 0x00]))
        XCTAssertEqual(JoyCon2BLEReportRate.hz66.descriptorValue, Data([0x42, 0x00]))
        XCTAssertEqual(JoyCon2BLEReportRate.hz133.coreBluetoothIntervalOverride, 6)
        XCTAssertEqual(JoyCon2BLEReportRate.hz66.coreBluetoothIntervalOverride, 12)
        XCTAssertEqual(
            JoyCon2BLEConnectionPolicy.highPerformance.coreBluetoothIntervalOverride,
            6
        )
        XCTAssertEqual(
            JoyCon2BLEConnectionPolicy.standardLowLatency.coreBluetoothIntervalOverride,
            12
        )
        XCTAssertNil(JoyCon2BLEConnectionPolicy.compatible.coreBluetoothIntervalOverride)
    }

    func testCadenceEstimatorAdaptsBetweenSupportedRatesAndIgnoresStalls() {
        var estimator = JoyCon2BLECadenceEstimator(fallbackRate: 133)

        XCTAssertEqual(estimator.record(timestamp: 1), 133)
        for index in 1...9 {
            _ = estimator.record(timestamp: 1 + Double(index) / 66.0)
        }
        XCTAssertTrue(estimator.hasStableEstimate)
        XCTAssertEqual(estimator.estimatedRate, 66, accuracy: 0.01)

        _ = estimator.record(timestamp: 2)
        XCTAssertEqual(estimator.estimatedRate, 66, accuracy: 0.01)

        var timestamp = 2.0
        var rate = 0.0
        for _ in 0..<9 {
            timestamp += 1.0 / 133.0
            rate = estimator.record(timestamp: timestamp)
        }
        XCTAssertEqual(rate, 133, accuracy: 0.01)
    }

    func testCommandResponsesMustMatchAndReportControllerSuccess() {
        let command = JoyCon2BLEProtocol.enableFeatures
        var response = Data([command[0], 0x01, 0x00, command[3]])

        XCTAssertEqual(JoyCon2BLEProtocol.commandResponseSucceeded(response, for: command), true)
        response[1] = 0x00
        XCTAssertEqual(JoyCon2BLEProtocol.commandResponseSucceeded(response, for: command), false)
        response[3] &+= 1
        XCTAssertNil(JoyCon2BLEProtocol.commandResponseSucceeded(response, for: command))
    }

    func testControlLayoutNormalizesSharedReportWithoutReplacingRawDiagnostics() {
        var report = [UInt8](repeating: 0, count: 0x3E)
        report[4] = 0xA5
        report[5] = 0x5A
        report[6] = 0xC3
        report[10...15] = [1, 2, 3, 4, 5, 6]

        let controls = JoyCon2BLEProtocol.controlBytes(from: report)

        XCTAssertEqual(controls?[3...5], [0xA5, 0x5A, 0xC3])
        XCTAssertEqual(controls?[6...11], [1, 2, 3, 4, 5, 6])
        XCTAssertEqual(report[4], 0xA5)
    }

    func testMotionDecoderUsesJoyCon2IMUOffsets() {
        var report = [UInt8](repeating: 0, count: 0x3C)
        writeInt16(101, to: &report, at: 0x30)
        writeInt16(-202, to: &report, at: 0x32)
        writeInt16(303, to: &report, at: 0x34)
        writeInt16(-404, to: &report, at: 0x36)
        writeInt16(505, to: &report, at: 0x38)
        writeInt16(-606, to: &report, at: 0x3A)

        XCTAssertEqual(JoyCon2BLEProtocol.decodeMotion(report), IMUSample(
            accelX: 101,
            accelY: -202,
            accelZ: 303,
            gyroX: -404,
            gyroY: 505,
            gyroZ: -606
        ))
    }

    func testBackendFollowsDiscoveryManagementReadyAndFrameContract() {
        let session = FakeJoyCon2BLESession()
        let backend = JoyCon2BLEInputDeviceBackend(session: session)
        let harness = InputDeviceBackendContractHarness(backend: backend)
        XCTAssertTrue(harness.start())

        let device = JoyCon2BLEDevice(
            id: "joycon2.ble:test-left",
            name: "Joy-Con 2 (L)",
            productID: JoyCon2BLEProtocol.leftProductID,
            handedness: .left
        )
        session.emitDiscovered(device)
        XCTAssertEqual(harness.availableDevicesSnapshot(), [device.controllerInfo])
        XCTAssertEqual(device.controllerInfo.profileVariant, .joyCon2)
        XCTAssertEqual(harness.changedBackendsSnapshot(), [backend.backendDescriptor])

        XCTAssertTrue(harness.setDeviceManaged(id: device.id, kind: .joyCon, managed: true))
        XCTAssertEqual(session.connectCallsSnapshot(), [device.id])
        let stickCalibration = InputDeviceAnalogStickCalibration(
            centerX: 2050,
            centerY: 2040,
            positiveRangeX: 1500,
            positiveRangeY: 1600,
            negativeRangeX: 1400,
            negativeRangeY: 1300
        )
        session.emitReady(deviceID: device.id, stickCalibration: stickCalibration)
        XCTAssertEqual(harness.connectionEventsSnapshot().map(\.connected), [true])

        var report = [UInt8](repeating: 0, count: 0x3C)
        writeInt16(4096, to: &report, at: 0x34)
        writeInt16(120, to: &report, at: 0x38)
        session.emitInput(deviceID: device.id, bytes: report, timestamp: 42)

        let frame = try! XCTUnwrap(harness.inputFramesSnapshot().first)
        XCTAssertEqual(frame.backend, backend.backendDescriptor)
        XCTAssertEqual(frame.deviceID, device.id)
        XCTAssertEqual(frame.reportID, JoyCon2BLEProtocol.inputReportID)
        XCTAssertEqual(frame.bytes, report)
        XCTAssertEqual(frame.motion.latest?.gyroY, 120)
        XCTAssertEqual(frame.gyroScale, JoyCon2BLEProtocol.gyroScale)
        XCTAssertEqual(frame.motionSampleRate, JoyCon2BLEProtocol.productionReportRate.hertz)
        XCTAssertEqual(frame.analogStickCalibration, stickCalibration)

        XCTAssertTrue(backend.restartDevice(id: device.id))
        XCTAssertEqual(session.reinitializeCallsSnapshot(), [device.id])
        XCTAssertTrue(session.disconnectCallsSnapshot().isEmpty)
        XCTAssertEqual(harness.connectionEventsSnapshot().map(\.connected), [true, false])

        session.emitInput(deviceID: device.id, bytes: report, timestamp: 43)
        XCTAssertEqual(harness.inputFramesSnapshot().count, 1)

        session.emitReady(deviceID: device.id, stickCalibration: stickCalibration)
        session.emitInput(deviceID: device.id, bytes: report, timestamp: 44)
        XCTAssertEqual(harness.connectionEventsSnapshot().map(\.connected), [true, false, true])
        XCTAssertEqual(harness.inputFramesSnapshot().count, 2)

        XCTAssertTrue(harness.setDeviceManaged(id: device.id, kind: .joyCon, managed: false))
        XCTAssertEqual(session.disconnectCallsSnapshot(), [device.id])
        XCTAssertEqual(
            harness.connectionEventsSnapshot().map(\.connected),
            [true, false, true, false]
        )
        harness.stop()
    }

    private func writeInt16(_ value: Int16, to bytes: inout [UInt8], at offset: Int) {
        let raw = UInt16(bitPattern: value)
        bytes[offset] = UInt8(raw & 0xFF)
        bytes[offset + 1] = UInt8(raw >> 8)
    }

    private func writePackedPair(
        _ first: UInt16,
        _ second: UInt16,
        to bytes: inout Data,
        at offset: Int
    ) {
        bytes[offset] = UInt8(first & 0xFF)
        bytes[offset + 1] = UInt8((first >> 8) & 0x0F) | UInt8((second & 0x0F) << 4)
        bytes[offset + 2] = UInt8((second >> 4) & 0xFF)
    }

    private func writePackedStick(
        x: UInt16,
        y: UInt16,
        to bytes: inout [UInt8],
        at offset: Int
    ) {
        bytes[offset] = UInt8(x & 0xFF)
        bytes[offset + 1] = UInt8((x >> 8) & 0x0F) | UInt8((y & 0x0F) << 4)
        bytes[offset + 2] = UInt8((y >> 4) & 0xFF)
    }
}

private final class FakeJoyCon2BLESession: JoyCon2BLESessioning, @unchecked Sendable {
    private let lock = NSLock()
    private var handlers: JoyCon2BLESessionHandlers?
    private var connectCalls: [String] = []
    private var disconnectCalls: [String] = []
    private var reinitializeCalls: [String] = []

    func start(handlers: JoyCon2BLESessionHandlers) -> Bool {
        locked { self.handlers = handlers }
        return true
    }

    func stop() {
        locked { handlers = nil }
    }

    func connect(deviceID: String) {
        locked { connectCalls.append(deviceID) }
    }

    func disconnect(deviceID: String) {
        locked { disconnectCalls.append(deviceID) }
    }

    func reinitialize(deviceID: String) {
        locked { reinitializeCalls.append(deviceID) }
    }

    func connectCallsSnapshot() -> [String] {
        locked { connectCalls }
    }

    func disconnectCallsSnapshot() -> [String] {
        locked { disconnectCalls }
    }

    func reinitializeCallsSnapshot() -> [String] {
        locked { reinitializeCalls }
    }

    func emitDiscovered(_ device: JoyCon2BLEDevice) {
        locked { handlers }?.discovered(device)
    }

    func emitReady(
        deviceID: String,
        stickCalibration: InputDeviceAnalogStickCalibration? = nil
    ) {
        locked { handlers }?.ready(deviceID, stickCalibration)
    }

    func emitInput(deviceID: String, bytes: [UInt8], timestamp: TimeInterval) {
        locked { handlers }?.input(deviceID, bytes, timestamp)
    }

    private func locked<Value>(_ body: () -> Value) -> Value {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}
