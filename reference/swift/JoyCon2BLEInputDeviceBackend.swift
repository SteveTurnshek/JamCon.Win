import Foundation
import QuartzCore

struct JoyCon2BLEDevice: Equatable, Sendable {
    let id: String
    let name: String
    let productID: UInt16
    let handedness: ControllerHandedness

    var controllerInfo: ControllerInfo {
        ControllerInfo(
            id: id,
            name: name,
            productID: Int(productID),
            kind: .joyCon,
            handedness: handedness,
            profileVariant: .joyCon2
        )
    }
}

struct JoyCon2BLESessionHandlers: Sendable {
    let discovered: @Sendable (JoyCon2BLEDevice) -> Void
    let ready: @Sendable (
        _ deviceID: String,
        _ stickCalibration: InputDeviceAnalogStickCalibration?
    ) -> Void
    let disconnected: @Sendable (_ deviceID: String, _ error: String?) -> Void
    let input: @Sendable (_ deviceID: String, _ bytes: [UInt8], _ receivedTimestamp: TimeInterval) -> Void
    let unavailable: @Sendable (_ reason: String) -> Void
}

protocol JoyCon2BLESessioning: AnyObject, Sendable {
    @discardableResult
    func start(handlers: JoyCon2BLESessionHandlers) -> Bool
    func stop()
    func connect(deviceID: String)
    func disconnect(deviceID: String)
    func reinitialize(deviceID: String)
}

final class JoyCon2BLEInputDeviceBackend: InputDeviceBackend, @unchecked Sendable {
    let backendDescriptor = InputDeviceBackendDescriptor(
        id: .joyCon2BluetoothLE,
        kind: .joyCon,
        displayName: "Joy-Con 2 Bluetooth LE",
        capabilities: [.buttons, .analogStick, .motion]
    )

    private struct State {
        var started = false
        var devices: [String: JoyCon2BLEDevice] = [:]
        var managedDeviceIDs: Set<String> = []
        var readyDeviceIDs: Set<String> = []
        var firstReportLogged: Set<String> = []
        var cadenceLogged: Set<String> = []
        var cadenceEstimators: [String: JoyCon2BLECadenceEstimator] = [:]
        var stickCalibrations: [String: InputDeviceAnalogStickCalibration] = [:]
        var handlers = InputDeviceBackendEventHandlers.none
    }

    private let lock = NSLock()
    private var state = State()
    private let session: any JoyCon2BLESessioning

    init(session: any JoyCon2BLESessioning = JoyCon2BLESession()) {
        self.session = session
    }

    @discardableResult
    func start() -> Bool {
        let shouldStart = locked { state -> Bool in
            guard !state.started else { return false }
            state.started = true
            return true
        }
        guard shouldStart else { return true }

        let started = session.start(handlers: JoyCon2BLESessionHandlers(
            discovered: { [weak self] device in self?.handleDiscovered(device) },
            ready: { [weak self] deviceID, calibration in
                self?.handleReady(deviceID: deviceID, stickCalibration: calibration)
            },
            disconnected: { [weak self] deviceID, error in
                self?.handleDisconnected(deviceID: deviceID, error: error)
            },
            input: { [weak self] deviceID, bytes, timestamp in
                self?.handleInput(deviceID: deviceID, bytes: bytes, receivedTimestamp: timestamp)
            },
            unavailable: { reason in
                JamLog.errorThrottled(
                    .joyCon,
                    key: "joycon2.ble.unavailable",
                    interval: 5,
                    "Joy-Con 2 Bluetooth unavailable: \(reason)"
                )
            }
        ))
        if !started {
            locked { $0.started = false }
        }
        return started
    }

    func stop() {
        let stoppedDevices = locked { state -> [(JoyCon2BLEDevice, InputDeviceBackendEventHandlers)] in
            guard state.started else { return [] }
            state.started = false
            let handler = state.handlers
            let devices = state.readyDeviceIDs.compactMap { state.devices[$0] }.map { ($0, handler) }
            state.readyDeviceIDs.removeAll(keepingCapacity: true)
            state.firstReportLogged.removeAll(keepingCapacity: true)
            state.cadenceLogged.removeAll(keepingCapacity: true)
            state.cadenceEstimators.removeAll(keepingCapacity: true)
            state.stickCalibrations.removeAll(keepingCapacity: true)
            return devices
        }
        session.stop()
        for (device, handler) in stoppedDevices {
            handler.connectionChanged(false, device.name, device.id)
        }
    }

    func availableDevicesSnapshot() -> [ControllerInfo] {
        locked { state in
            state.devices.values
                .map(\.controllerInfo)
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
    }

    var isConnected: Bool {
        locked { !$0.readyDeviceIDs.isEmpty }
    }

    func setDeviceManaged(id: String, managed: Bool) {
        let action = locked { state -> (
            shouldConnect: Bool?,
            disconnectedDevice: JoyCon2BLEDevice?,
            handler: InputDeviceBackendEventHandlers
        ) in
            if managed {
                state.managedDeviceIDs.insert(id)
                return (state.devices[id] == nil ? nil : true, nil, state.handlers)
            }
            state.managedDeviceIDs.remove(id)
            let wasReady = state.readyDeviceIDs.remove(id) != nil
            state.firstReportLogged.remove(id)
            state.cadenceLogged.remove(id)
            state.cadenceEstimators.removeValue(forKey: id)
            state.stickCalibrations.removeValue(forKey: id)
            return (false, wasReady ? state.devices[id] : nil, state.handlers)
        }
        guard let shouldConnect = action.shouldConnect else { return }
        if shouldConnect {
            JamLog.info(.joyCon, "Connecting managed Joy-Con 2: \(id)")
            session.connect(deviceID: id)
        } else {
            session.disconnect(deviceID: id)
            if let device = action.disconnectedDevice {
                action.handler.connectionChanged(false, device.name, device.id)
            }
        }
    }

    @discardableResult
    func restartDevice(id: String) -> Bool {
        let event = locked { state -> (
            device: JoyCon2BLEDevice,
            handler: InputDeviceBackendEventHandlers,
            wasReady: Bool
        )? in
            guard state.started,
                  state.managedDeviceIDs.contains(id),
                  let device = state.devices[id] else { return nil }
            let wasReady = state.readyDeviceIDs.remove(id) != nil
            state.firstReportLogged.remove(id)
            state.cadenceLogged.remove(id)
            state.cadenceEstimators.removeValue(forKey: id)
            state.stickCalibrations.removeValue(forKey: id)
            return (device, state.handlers, wasReady)
        }
        guard let event else { return false }

        // Stop frames at the backend boundary before changing controller
        // configuration. The BLE session remains connected and will publish a
        // fresh ready event after setup and factory calibration complete.
        if event.wasReady {
            event.handler.connectionChanged(false, event.device.name, event.device.id)
        }
        session.reinitialize(deviceID: id)
        return true
    }

    func setEventHandlers(_ handlers: InputDeviceBackendEventHandlers) {
        locked { $0.handlers = handlers }
    }

    private func handleDiscovered(_ device: JoyCon2BLEDevice) {
        let result = locked { state -> (changed: Bool, connect: Bool, handler: InputDeviceBackendEventHandlers) in
            guard state.started else { return (false, false, state.handlers) }
            let changed = state.devices[device.id] != device
            state.devices[device.id] = device
            return (changed, state.managedDeviceIDs.contains(device.id), state.handlers)
        }
        if result.changed {
            JamLog.info(
                .joyCon,
                "Joy-Con 2 discovered: \(device.name) (PID: 0x\(String(format: "%04X", device.productID)))"
            )
            result.handler.devicesChanged()
        }
        if result.connect {
            session.connect(deviceID: device.id)
        }
    }

    private func handleReady(
        deviceID: String,
        stickCalibration: InputDeviceAnalogStickCalibration?
    ) {
        let event = locked { state -> (JoyCon2BLEDevice, InputDeviceBackendEventHandlers)? in
            guard state.started,
                  state.managedDeviceIDs.contains(deviceID),
                  let device = state.devices[deviceID],
                  state.readyDeviceIDs.insert(deviceID).inserted else { return nil }
            state.stickCalibrations[deviceID] = stickCalibration
            return (device, state.handlers)
        }
        guard let (device, handler) = event else { return }
        JamLog.info(.joyCon, "Joy-Con 2 input ready: \(device.name)")
        handler.connectionChanged(true, device.name, device.id)
    }

    private func handleDisconnected(deviceID: String, error: String?) {
        let event = locked { state -> (JoyCon2BLEDevice?, InputDeviceBackendEventHandlers, Bool) in
            let wasReady = state.readyDeviceIDs.remove(deviceID) != nil
            state.firstReportLogged.remove(deviceID)
            state.cadenceLogged.remove(deviceID)
            state.cadenceEstimators.removeValue(forKey: deviceID)
            state.stickCalibrations.removeValue(forKey: deviceID)
            return (state.devices[deviceID], state.handlers, wasReady)
        }
        if let error {
            JamLog.errorThrottled(
                .joyCon,
                key: "joycon2.disconnected.\(deviceID)",
                interval: 5,
                "Joy-Con 2 disconnected: \(error)"
            )
        }
        guard event.2 else { return }
        event.1.connectionChanged(false, event.0?.name, deviceID)
    }

    private func handleInput(
        deviceID: String,
        bytes: [UInt8],
        receivedTimestamp: TimeInterval
    ) {
        let output = locked { state -> (
            handlers: InputDeviceBackendEventHandlers,
            logFirstReport: Bool,
            stableCadence: Double?,
            motionSampleRate: Double,
            stickCalibration: InputDeviceAnalogStickCalibration?
        )? in
            guard state.started,
                  state.managedDeviceIDs.contains(deviceID),
                  state.readyDeviceIDs.contains(deviceID) else { return nil }
            let shouldLog = state.firstReportLogged.insert(deviceID).inserted
            var estimator = state.cadenceEstimators[deviceID]
                ?? JoyCon2BLECadenceEstimator(
                    fallbackRate: JoyCon2BLEProtocol.productionReportRate.hertz
                )
            let motionSampleRate = estimator.record(timestamp: receivedTimestamp)
            state.cadenceEstimators[deviceID] = estimator
            let stableCadence = estimator.hasStableEstimate
                && state.cadenceLogged.insert(deviceID).inserted
                ? motionSampleRate
                : nil
            return (
                state.handlers,
                shouldLog,
                stableCadence,
                motionSampleRate,
                state.stickCalibrations[deviceID]
            )
        }
        guard let output else { return }
        if output.logFirstReport {
            let hex = bytes.prefix(64).map { String(format: "%02X", $0) }.joined(separator: " ")
            JamLog.debug(.joyCon, "Joy-Con 2 first input report len=\(bytes.count): \(hex)")
        }
        if let stableCadence = output.stableCadence {
            JamLog.info(
                .joyCon,
                "Joy-Con 2 input cadence stabilized at "
                    + "\(String(format: "%.1f", stableCadence)) Hz "
                    + "(production target \(Int(JoyCon2BLEProtocol.productionReportRate.hertz)) Hz)"
            )
        }

        guard let motion = JoyCon2BLEProtocol.decodeMotion(bytes) else {
            // A single zero-filled report is normal while the controller turns
            // its IMU on. Keep it visible in diagnostics without presenting a
            // successful connection as an error.
            JamLog.debugThrottled(
                .joyCon,
                key: "joycon2.input.missing-motion.\(deviceID)",
                interval: 2,
                "Dropping Joy-Con 2 report without live IMU data (length=\(bytes.count))"
            )
            return
        }

        output.handlers.inputFrame(InputDeviceFrame(
            backend: backendDescriptor,
            deviceID: deviceID,
            reportID: JoyCon2BLEProtocol.inputReportID,
            bytes: bytes,
            motion: .single(motion),
            timestamp: receivedTimestamp,
            receivedTimestamp: receivedTimestamp,
            inputTimestamp: nil,
            timestampSource: .hostReceipt,
            gyroScale: JoyCon2BLEProtocol.gyroScale,
            motionSampleRate: output.motionSampleRate,
            analogStickCalibration: output.stickCalibration
        ))
    }

    private func locked<Value>(_ body: (inout State) -> Value) -> Value {
        lock.lock()
        defer { lock.unlock() }
        return body(&state)
    }
}
