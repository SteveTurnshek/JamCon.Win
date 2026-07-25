import Foundation
import CoreGraphics
import QuartzCore
import os

extension InputEngine {

    // MARK: - Joy-Con Report Processing

    func processJoyConReport(_ report: InputDeviceFrame) {
        assertOnEngineQueue()
        guard isRunning else { return }
        guard let motion = report.motion.latest else {
            JamLog.errorThrottled(
                .joyCon,
                key: "input.missing-motion.\(report.deviceID)",
                interval: 2,
                "Dropping Joy-Con input frame without motion data"
            )
            return
        }
        let engineStartTimestamp = CACurrentMediaTime()
        let healthDevice = ManagedDeviceKey(kind: .joyCon, id: report.deviceID)
        let signpostID = Self.inputPerformanceLog.signpostsEnabled
            ? OSSignpostID(log: Self.inputPerformanceLog)
            : nil
        if let signpostID {
            os_signpost(.begin, log: Self.inputPerformanceLog, name: "Joy-Con Input", signpostID: signpostID)
        }
        defer {
            let engineEndTimestamp = CACurrentMediaTime()
            recordInputHealth(
                device: healthDevice,
                inputTimestamp: report.inputTimestamp,
                timestampSource: report.timestampSource,
                receivedTimestamp: report.receivedTimestamp,
                engineStartTimestamp: engineStartTimestamp,
                engineEndTimestamp: engineEndTimestamp
            )
            if let signpostID {
                os_signpost(.end, log: Self.inputPerformanceLog, name: "Joy-Con Input", signpostID: signpostID)
            }
        }

        // Read settings ONCE at start of frame
        let s = settings.snapshot()

        guard s.isEnabled else { return }

        guard let device = joyConDevices[report.deviceID] else { return }
        let profile = device.profile
        let owner = ManagedDeviceKey(kind: .joyCon, id: device.id)

        let controlBytes: [UInt8]
        if report.backend.id == .joyCon2BluetoothLE {
            guard let normalized = JoyCon2BLEProtocol.controlBytes(from: report.bytes) else {
                JamLog.errorThrottled(
                    .joyCon,
                    key: "joycon2.input.malformed-controls.\(report.deviceID)",
                    interval: 2,
                    "Dropping malformed Joy-Con 2 control report (length=\(report.length))"
                )
                return
            }
            controlBytes = normalized
        } else {
            controlBytes = report.bytes
        }

        let isLeft = profile.isLeft
        let buttonProfile: JoyConButtonMappingProfile = s.joyConButtonMappings[profile] ?? .defaultProfile(for: profile)
        let cursorEnabled = s.cursorControlEnabledByProfile[profile] ?? true

        if let hardwareCalibration = report.analogStickCalibration {
            device.mapping.calibration.applyHardwareCalibration(hardwareCalibration)
        }

        // Bounded neutral correction around the connection/factory anchor.
        let raw = device.mapping.joystickPositionRaw(in: controlBytes)
        device.mapping.calibration.updateAutoCalibration(rawX: raw.x, rawY: raw.y, timestamp: report.timestamp)

        // 1. Process Joy-Con buttons
        if device.hasPrimedButtonState {
            processJoyConButtonActions(
                owner: owner,
                device: device,
                bytes: controlBytes,
                mapping: device.mapping,
                profile: buttonProfile,
                holdThreshold: buttonProfile.holdThreshold
            )
        } else {
            primeJoyConButtonStates(
                device: device,
                bytes: controlBytes,
                mapping: device.mapping
            )
            device.hasPrimedButtonState = true
        }

        // 2. Process gyro through unified pipeline. The decoder exposes all
        // three batched Joy-Con IMU samples so policy stays in the engine.
        let rawGyro = s.joyConUseAveragedGyroSamples
            ? (report.motion.averagedGyro ?? (x: motion.gyroX, y: motion.gyroY, z: motion.gyroZ))
            : (x: motion.gyroX, y: motion.gyroY, z: motion.gyroZ)

        let pipeline = GyroRemapper.process(
            rawX: rawGyro.x,
            rawY: rawGyro.y,
            rawZ: rawGyro.z,
            controllerKind: .joyCon,
            isLeft: isLeft,
            profileVariant: profile.variant,
            nativeScale: report.gyroScale
        )

        // Pass remapped values to gyro processor (which expects pitch in X, yaw in Y)
        var gyroSettings = s.gyroSettings[.joyCon] ?? .defaultForKind(.joyCon)
        let userScale = gyroSettings.gyroScale
        gyroSettings.gyroScale = effectiveGyroScale(
            for: .joyCon,
            userScale: userScale,
            nativeScale: report.gyroScale
        )
        gyroSettings.expectedSampleRate = report.motionSampleRate ?? 66.0
        gyroSettings.biasMotionThreshold = 30.0 // Joy-Con has lower noise floor; tighten bias capture
        if let (dx, dy) = device.gyroProcessor.process(
            rawX: pipeline.remapped.pitch,
            rawY: pipeline.remapped.yaw,
            rawZ: pipeline.remapped.roll,
            timestamp: report.timestamp,
            settings: gyroSettings
        ) {
            recordGyroResponseHealth(
                device: owner,
                timestamp: report.timestamp,
                sample: device.gyroProcessor.lastResponseSample
            )
            routeGyroMovement(
                owner: owner,
                dx: dx,
                dy: dy,
                cursorEnabled: cursorEnabled,
                hasDragMapping: buttonProfile.hasDragMapping,
                configuration: s.radialMenuConfiguration,
                modeState: device.mode
            )
        }

        // 3. Process joystick scroll (if enabled)
        if s.joystickScrollEnabled,
           cursorEnabled,
           device.mapping.calibration.isCalibrated {
            processJoyConJoystickScroll(bytes: controlBytes, mapping: device.mapping, settings: s)
        }

        // 4. Update battery level (Joy-Con battery is in byte 2, upper nibble)
        if report.backend.id == .joyConDirectHID, report.bytes.count > 2 {
            setBatteryLevel(BatteryHelper.joyConLevel(from: report.bytes[2]), for: owner)
        }

        // 4. Record to debug buffer with all pipeline stages
        if s.debugRecordingEnabled && (s.debugRecordingTargetKind == nil || s.debugRecordingTargetKind == .joyCon) {
            let engineEndTimestamp = CACurrentMediaTime()
            debugBuffer.recordTrace(
                device: owner,
                reportID: report.reportID,
                bytes: report.bytes,
                timestamp: report.receivedTimestamp
            )
            debugBuffer.record(
                bytes: report.bytes,
                length: report.length,
                rawGyro: pipeline.raw,
                remappedGyro: pipeline.remapped,
                normalizedGyro: pipeline.normalized,
                accel: (motion.accelX, motion.accelY, motion.accelZ),
                buttonStates: device.buttonStates,
                controllerKind: .joyCon,
                gyroDebug: mapGyroDebug(from: device.gyroProcessor.lastDebugState),
                pipelineTiming: DebugBuffer.PipelineTiming(
                    inputTimestamp: report.inputTimestamp,
                    timestampSource: report.timestampSource,
                    receivedTimestamp: report.receivedTimestamp,
                    engineStartTimestamp: engineStartTimestamp,
                    engineEndTimestamp: engineEndTimestamp
                )
            )
        }
    }

    // MARK: - Joy-Con Button Processing

    private func processJoyConButtonActions(
        owner: ManagedDeviceKey,
        device: JoyConDeviceState,
        bytes: [UInt8],
        mapping: JoyConButtonMapping,
        profile: JoyConButtonMappingProfile,
        holdThreshold: Double
    ) {
        // Process all buttons available on this Joy-Con side
        let availableButtons = JoyConLogicalButton.availableButtons(for: device.profile)

        for button in availableButtons {
            let idx = button.index
            let isPressed = mapping.isPressed(button, in: bytes)
            let wasPressed = device.previousButtonStates[idx]

            if isPressed != wasPressed {
                let actions = profile.actions(for: button)
                if isPressed {
                    handleJoyConButtonDown(owner: owner, device: device, button: button, actions: actions, holdThreshold: holdThreshold)
                } else {
                    handleJoyConButtonUp(owner: owner, device: device, button: button, mappingProfile: profile)
                }
            }

            device.previousButtonStates[idx] = isPressed
            device.buttonStates[idx] = isPressed
        }
    }

    private func primeJoyConButtonStates(
        device: JoyConDeviceState,
        bytes: [UInt8],
        mapping: JoyConButtonMapping
    ) {
        let availableButtons = JoyConLogicalButton.availableButtons(for: device.profile)
        for button in availableButtons {
            let idx = button.index
            let isPressed = mapping.isPressed(button, in: bytes)
            device.previousButtonStates[idx] = isPressed
            device.buttonStates[idx] = isPressed
            device.buttonPressStates[idx] = nil
            device.holdTimers[idx]?.cancel()
            device.holdTimers[idx] = nil
        }
    }

    private func handleJoyConButtonDown(
        owner: ManagedDeviceKey,
        device: JoyConDeviceState,
        button: JoyConLogicalButton,
        actions: ButtonActions,
        holdThreshold: Double
    ) {
        let idx = button.index

        // Handle gyro mode actions immediately
        if actions.pressIsGyroMode {
            device.buttonPressStates[idx] = ButtonPressState(actions: actions, device: owner, control: button.rawValue)
            switch actions.press {
            case .drag:
                device.mode.dragButtonHeld = true
            case .scroll:
                device.mode.scrollButtonHeld = true
            case .radialMenu:
                beginRadialMenu(owner: owner, pointerStyle: .ghostCursor, modeState: &device.mode)
            default:
                break
            }
            return
        }

        let state = ButtonPressState(actions: actions, device: owner, control: button.rawValue)
        device.buttonPressStates[idx] = state

        if case .mouseClick = actions.press {
            actionExecutor.execute(actions.press, isPressed: true, owner: state.pressOwner)
        }

        // Schedule hold timer if there's a hold action
        if actions.hold != .none {
            let timer = DispatchWorkItem { [weak self] in
                guard let self else { return }
                guard self.isRunning else { return }
                guard let device = self.joyConDevices[owner.id] else { return }
                guard var state = device.buttonPressStates[idx], !state.holdFired else { return }

                state.holdFired = true
                device.buttonPressStates[idx] = state
                self.actionExecutor.execute(state.actions.hold, isPressed: true, owner: state.holdOwner)
            }
            device.holdTimers[idx]?.cancel()
            device.holdTimers[idx] = timer
            holdScheduler.schedule(timer, after: holdThreshold, on: engineQueue)
        }
    }

    private func handleJoyConButtonUp(
        owner: ManagedDeviceKey,
        device: JoyConDeviceState,
        button: JoyConLogicalButton,
        mappingProfile: JoyConButtonMappingProfile
    ) {
        let idx = button.index

        // Cancel hold timer
        device.holdTimers[idx]?.cancel()
        device.holdTimers[idx] = nil

        if let state = device.buttonPressStates[idx], state.actions.pressIsGyroMode {
            handleGyroModeRelease(owner: owner, action: state.actions.press, modeState: &device.mode)
            device.buttonPressStates[idx] = nil
            return
        }

        let actions = mappingProfile.actions(for: button)
        if actions.pressIsGyroMode {
            handleGyroModeRelease(owner: owner, action: actions.press, modeState: &device.mode)
            return
        }

        guard let state = device.buttonPressStates[idx] else { return }

        // Handle mouse click release
        if case .mouseClick = state.actions.press {
            actionExecutor.execute(state.actions.press, isPressed: false, owner: state.pressOwner)
            if state.holdFired {
                actionExecutor.execute(state.actions.hold, isPressed: false, owner: state.holdOwner)
            }
            device.buttonPressStates[idx] = nil
            return
        }

        if state.holdFired {
            // Hold action was executed, release it
            actionExecutor.execute(state.actions.hold, isPressed: false, owner: state.holdOwner)
        } else {
            // Hold didn't fire, execute press action as tap
            if state.actions.press != .none {
                actionExecutor.tap(state.actions.press, owner: state.pressOwner)
            }
        }

        device.buttonPressStates[idx] = nil
    }
}
