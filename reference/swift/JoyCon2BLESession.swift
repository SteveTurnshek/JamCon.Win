@preconcurrency import CoreBluetooth
import Foundation
import QuartzCore

/// CoreBluetooth transport for Nintendo's proprietary Joy-Con 2 BLE service.
/// All CoreBluetooth objects and mutable transport state are confined to
/// `JamCon.JoyCon2.BLE`; callbacks leave the queue only through Sendable values.
final class JoyCon2BLESession: NSObject, JoyCon2BLESessioning, @unchecked Sendable {
    private enum CommandPurpose: Equatable {
        case acknowledgement
        case primaryStickFactoryCalibration
    }

    private struct InitializationCommand {
        let payload: Data
        let purpose: CommandPurpose
        let isRequired: Bool

        static func required(_ payload: Data) -> InitializationCommand {
            InitializationCommand(
                payload: payload,
                purpose: .acknowledgement,
                isRequired: true
            )
        }
    }

    private final class Context: @unchecked Sendable {
        var device: JoyCon2BLEDevice
        var peripheral: CBPeripheral
        var input: CBCharacteristic?
        var command: CBCharacteristic?
        var response: CBCharacteristic?
        var reportRateDescriptor: CBDescriptor?
        var inputDescriptorDiscoveryStarted = false
        var inputDescriptorsDiscovered = false
        var responseNotificationRequested = false
        var commands: [InitializationCommand] = []
        var currentCommand: InitializationCommand?
        var currentCommandAttempt = 0
        var commandWaitingForWriteReady = false
        var commandGeneration = 0
        var reportRateCandidates: [JoyCon2BLEReportRate] = []
        var currentReportRate: JoyCon2BLEReportRate?
        var reportRateGeneration = 0
        var initializationStarted = false
        var reinitializingInPlace = false
        var ready = false
        var connectionGeneration = 0
        var connectionPolicyIndex = 0
        var retryNotBefore: TimeInterval = 0
        var reconnectScheduled = false
        var stickCalibration: InputDeviceAnalogStickCalibration?

        init(device: JoyCon2BLEDevice, peripheral: CBPeripheral) {
            self.device = device
            self.peripheral = peripheral
        }
    }

    private let queue = DispatchQueue(label: "JamCon.JoyCon2.BLE", qos: .userInteractive)
    private var central: CBCentralManager!
    private var handlers: JoyCon2BLESessionHandlers?
    private var contextsByID: [String: Context] = [:]
    private var contextIDByPeripheral: [UUID: String] = [:]
    private var desiredDeviceIDs: Set<String> = []
    private var started = false

    private let serviceUUID = CBUUID(string: JoyCon2BLEProtocol.serviceUUID)
    private let inputUUID = CBUUID(string: JoyCon2BLEProtocol.inputUUID)
    private let reportRateDescriptorUUID = CBUUID(
        string: JoyCon2BLEProtocol.reportRateDescriptorUUID
    )
    private let commandUUID = CBUUID(string: JoyCon2BLEProtocol.commandUUID)
    private let responseUUID = CBUUID(string: JoyCon2BLEProtocol.responseUUID)
    private let preferredReportRate: JoyCon2BLEReportRate

    init(preferredReportRate: JoyCon2BLEReportRate = JoyCon2BLEProtocol.preferredReportRate) {
        self.preferredReportRate = preferredReportRate
        super.init()
        central = CBCentralManager(delegate: self, queue: queue)
    }

    @discardableResult
    func start(handlers: JoyCon2BLESessionHandlers) -> Bool {
        queue.async { [weak self] in
            guard let self else { return }
            self.handlers = handlers
            self.started = true
            self.startScanningIfPossible()
        }
        return true
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.started = false
            if self.central.state == .poweredOn {
                self.central.stopScan()
                for context in self.contextsByID.values
                    where context.peripheral.state != .disconnected {
                    self.central.cancelPeripheralConnection(context.peripheral)
                }
            }
            self.contextsByID.removeAll(keepingCapacity: true)
            self.contextIDByPeripheral.removeAll(keepingCapacity: true)
            self.desiredDeviceIDs.removeAll(keepingCapacity: true)
            self.handlers = nil
        }
    }

    func connect(deviceID: String) {
        queue.async { [weak self] in
            guard let self else { return }
            self.desiredDeviceIDs.insert(deviceID)
            guard let context = self.contextsByID[deviceID] else { return }
            self.requestConnection(context)
        }
    }

    func disconnect(deviceID: String) {
        queue.async { [weak self] in
            guard let self else { return }
            self.desiredDeviceIDs.remove(deviceID)
            guard let context = self.contextsByID[deviceID] else { return }
            context.connectionGeneration += 1
            context.connectionPolicyIndex = 0
            context.retryNotBefore = 0
            context.reconnectScheduled = false
            if context.peripheral.state != .disconnected {
                self.central.cancelPeripheralConnection(context.peripheral)
            }
        }
    }

    func reinitialize(deviceID: String) {
        queue.async { [weak self] in
            guard let self,
                  self.started,
                  self.desiredDeviceIDs.contains(deviceID),
                  let context = self.contextsByID[deviceID] else { return }

            guard context.peripheral.state == .connected,
                  context.command != nil,
                  context.response?.isNotifying == true,
                  context.input != nil,
                  context.inputDescriptorsDiscovered else {
                self.fallbackToReconnectForReset(context)
                return
            }

            self.prepareInPlaceReinitialization(context)
            JamLog.info(
                .joyCon,
                "Reinitializing Joy-Con 2 over the existing Bluetooth connection: \(context.device.name)"
            )
            self.beginInitialization(context)
        }
    }

    private func requestConnection(_ context: Context) {
        guard started, desiredDeviceIDs.contains(context.device.id) else { return }

        let now = CACurrentMediaTime()
        if context.retryNotBefore > now {
            scheduleReconnect(context, delay: context.retryNotBefore - now)
            return
        }
        guard context.peripheral.state == .disconnected else { return }

        context.reconnectScheduled = false
        context.connectionGeneration += 1
        let generation = context.connectionGeneration
        let policies = JoyCon2BLEConnectionPolicy.allCases
        let policyIndex = min(context.connectionPolicyIndex, policies.count - 1)
        let policy = policies[policyIndex]
        var options: [String: Any] = [
            CBConnectPeripheralOptionNotifyOnConnectionKey: true,
            CBConnectPeripheralOptionNotifyOnDisconnectionKey: true,
            CBConnectPeripheralOptionNotifyOnNotificationKey: true,
            CBConnectPeripheralOptionStartDelayKey: 0,
        ]
        if let intervalOverride = policy.coreBluetoothIntervalOverride {
            // Nintendo does not issue the standard peripheral-side parameter
            // update. Apply the measured initial-connect override before
            // notifications become active.
            options["kCBConnectOptionRequiresLowLatency"] = true
            options["kCBConnectOptionLatencyCritical"] = true
            options["kCBConnectOptionOverrideMinCIFrames"] = intervalOverride
            options["kCBConnectOptionOverrideMaxCIFrames"] = intervalOverride
        }

        JamLog.info(.joyCon, "Requesting Joy-Con 2 Bluetooth connection: \(context.device.name)")
        central.connect(context.peripheral, options: options)
        if let intervalOverride = policy.coreBluetoothIntervalOverride {
            JamLog.info(
                .joyCon,
                "Joy-Con 2 requested CoreBluetooth CI override "
                    + "\(intervalOverride) (\(policy.logDescription))"
            )
        } else {
            JamLog.info(
                .joyCon,
                "Joy-Con 2 using \(policy.logDescription) Bluetooth negotiation"
            )
        }

        // Joy-Con 2 pairing can take substantially longer than a normal BLE
        // reconnect. Match the proven macOS implementation's pairing window
        // instead of cancelling while the controller is still negotiating.
        queue.asyncAfter(deadline: .now() + 60) { [weak self, weak context] in
            guard let self, let context,
                  context.connectionGeneration == generation,
                  self.desiredDeviceIDs.contains(context.device.id),
                  !context.ready else { return }
            JamLog.error(
                .joyCon,
                "Joy-Con 2 connection timed out in state \(context.peripheral.state.rawValue); retrying"
            )
            self.advanceConnectionPolicy(context)
            if context.peripheral.state != .disconnected {
                self.central.cancelPeripheralConnection(context.peripheral)
            }
            // Joy-Con 2 firmware enters a connection cooldown when hosts retry
            // repeatedly. Give it a quiet window instead of keeping it stuck.
            context.retryNotBefore = CACurrentMediaTime() + 120
            self.scheduleReconnect(context, delay: 120)
        }
    }

    private func advanceConnectionPolicy(_ context: Context) {
        let maximumIndex = JoyCon2BLEConnectionPolicy.allCases.count - 1
        guard context.connectionPolicyIndex < maximumIndex else { return }
        context.connectionPolicyIndex += 1
        let policy = JoyCon2BLEConnectionPolicy.allCases[context.connectionPolicyIndex]
        JamLog.info(
            .joyCon,
            "Joy-Con 2 will retry with \(policy.logDescription) Bluetooth parameters"
        )
    }

    private func scheduleReconnect(_ context: Context, delay: TimeInterval = 1.5) {
        guard started,
              desiredDeviceIDs.contains(context.device.id),
              !context.reconnectScheduled else { return }
        context.reconnectScheduled = true
        queue.asyncAfter(deadline: .now() + delay) { [weak self, weak context] in
            guard let self, let context else { return }
            context.reconnectScheduled = false
            self.requestConnection(context)
        }
    }

    private func startScanningIfPossible() {
        guard started, central.state == .poweredOn, !central.isScanning else { return }
        central.scanForPeripherals(
            withServices: nil,
            options: [CBCentralManagerScanOptionAllowDuplicatesKey: false]
        )
    }

    private func deviceID(for peripheral: CBPeripheral) -> String {
        "joycon2.ble:\(peripheral.identifier.uuidString.lowercased())"
    }

    private func context(for peripheral: CBPeripheral) -> Context? {
        contextIDByPeripheral[peripheral.identifier].flatMap { contextsByID[$0] }
    }

    private func beginInitialization(_ context: Context) {
        guard !context.initializationStarted,
              context.command != nil,
              context.response?.isNotifying == true,
              context.inputDescriptorsDiscovered else { return }
        context.initializationStarted = true
        JamLog.info(.joyCon, "Initializing Joy-Con 2 input: \(context.device.name)")
        context.commands = [
            .required(JoyCon2BLEProtocol.setFeatureMask),
            .required(JoyCon2BLEProtocol.enableFeatures),
            InitializationCommand(
                payload: JoyCon2BLEProtocol.readPrimaryStickFactoryCalibration,
                purpose: .primaryStickFactoryCalibration,
                isRequired: false
            ),
            .required(JoyCon2BLEProtocol.setPlayerOneLED),
        ]
        sendNextCommand(context)
    }

    private func sendNextCommand(_ context: Context) {
        guard started, !context.ready, context.currentCommand == nil else { return }
        guard !context.commands.isEmpty else {
            beginReportRateConfiguration(context)
            return
        }

        context.currentCommand = context.commands.removeFirst()
        context.currentCommandAttempt = 0
        sendCurrentCommand(context)
    }

    private func sendCurrentCommand(_ context: Context) {
        guard started,
              !context.ready,
              let command = context.currentCommand,
              let characteristic = context.command else { return }

        if characteristic.properties.contains(.writeWithoutResponse),
           !context.peripheral.canSendWriteWithoutResponse {
            context.commandWaitingForWriteReady = true
            return
        }

        context.commandWaitingForWriteReady = false
        context.currentCommandAttempt += 1
        context.commandGeneration += 1
        let generation = context.commandGeneration
        let writeType: CBCharacteristicWriteType = characteristic.properties.contains(.writeWithoutResponse)
            ? .withoutResponse
            : .withResponse
        context.peripheral.writeValue(command.payload, for: characteristic, type: writeType)

        // The GATT write response (when present) only confirms transport. The
        // controller's response notification is the semantic acknowledgment.
        queue.asyncAfter(deadline: .now() + 1.0) { [weak self, weak context] in
            guard let self, let context,
                  context.commandGeneration == generation,
                  context.currentCommand != nil else { return }
            self.retryCurrentCommand(context, reason: "acknowledgment timed out")
        }
    }

    private func retryCurrentCommand(_ context: Context, reason: String) {
        guard context.currentCommand != nil else { return }
        context.commandGeneration += 1
        if context.currentCommandAttempt < 3 {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 initialization \(reason); retrying (\(context.currentCommandAttempt + 1)/3)"
            )
            queue.asyncAfter(deadline: .now() + 0.1) { [weak self, weak context] in
                guard let self, let context else { return }
                self.sendCurrentCommand(context)
            }
        } else if context.currentCommand?.isRequired == true {
            failInitialization(context, reason: reason)
        } else {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 optional initialization \(reason); continuing without factory stick calibration"
            )
            completeCurrentCommand(context)
        }
    }

    private func completeCurrentCommand(_ context: Context) {
        guard context.currentCommand != nil else { return }
        context.currentCommand = nil
        context.currentCommandAttempt = 0
        context.commandWaitingForWriteReady = false
        context.commandGeneration += 1

        queue.asyncAfter(deadline: .now() + 0.05) { [weak self, weak context] in
            guard let self, let context else { return }
            self.sendNextCommand(context)
        }
    }

    private func handleCommandResponse(_ response: Data, context: Context) {
        guard let command = context.currentCommand else { return }
        guard let succeeded = JoyCon2BLEProtocol.commandResponseSucceeded(
            response,
            for: command.payload
        ) else {
            let hex = response.prefix(16).map { String(format: "%02X", $0) }.joined(separator: " ")
            JamLog.errorThrottled(
                .joyCon,
                key: "joycon2.command.unmatched.\(context.device.id)",
                interval: 2,
                "Ignoring unmatched Joy-Con 2 command response: \(hex)"
            )
            return
        }
        guard succeeded else {
            retryCurrentCommand(context, reason: "controller rejected command")
            return
        }

        if command.purpose == .primaryStickFactoryCalibration {
            guard let calibration = JoyCon2BLEProtocol.decodeStickCalibrationResponse(response) else {
                retryCurrentCommand(context, reason: "returned malformed factory stick calibration")
                return
            }
            context.stickCalibration = calibration
            JamLog.info(
                .joyCon,
                "Joy-Con 2 factory stick calibration loaded "
                    + "(center=\(calibration.centerX),\(calibration.centerY))"
            )
        }
        completeCurrentCommand(context)
    }

    private func beginReportRateConfiguration(_ context: Context) {
        guard context.currentReportRate == nil else { return }
        guard context.reportRateDescriptor != nil else {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 common input has no report-rate descriptor; using its default cadence"
            )
            subscribeToInput(context)
            return
        }

        context.reportRateCandidates = [preferredReportRate]
        if preferredReportRate != JoyCon2BLEProtocol.fallbackReportRate {
            context.reportRateCandidates.append(JoyCon2BLEProtocol.fallbackReportRate)
        }
        tryNextReportRate(context)
    }

    private func tryNextReportRate(_ context: Context) {
        guard let descriptor = context.reportRateDescriptor else {
            subscribeToInput(context)
            return
        }
        guard !context.reportRateCandidates.isEmpty else {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 rejected all requested report rates; using the negotiated default cadence"
            )
            subscribeToInput(context)
            return
        }

        let reportRate = context.reportRateCandidates.removeFirst()
        context.currentReportRate = reportRate
        context.reportRateGeneration += 1
        let generation = context.reportRateGeneration
        context.peripheral.writeValue(reportRate.descriptorValue, for: descriptor)

        queue.asyncAfter(deadline: .now() + 2.0) { [weak self, weak context] in
            guard let self, let context,
                  context.reportRateGeneration == generation,
                  context.currentReportRate == reportRate else { return }
            context.currentReportRate = nil
            context.reportRateGeneration += 1
            JamLog.error(
                .joyCon,
                "Joy-Con 2 \(Int(reportRate.hertz)) Hz report-rate request timed out; using the negotiated default cadence"
            )
            self.subscribeToInput(context)
        }
    }

    private func subscribeToInput(_ context: Context) {
        guard let input = context.input else { return }
        if input.isNotifying {
            markReady(context)
        } else {
            context.peripheral.setNotifyValue(true, for: input)
        }
    }

    private func failInitialization(_ context: Context, reason: String) {
        JamLog.error(.joyCon, "Joy-Con 2 initialization failed: \(reason)")
        let wasInPlaceReset = context.reinitializingInPlace
        context.reinitializingInPlace = false
        let retryDelay: TimeInterval = wasInPlaceReset ? 5 : 30
        context.retryNotBefore = max(
            context.retryNotBefore,
            CACurrentMediaTime() + retryDelay
        )
        if wasInPlaceReset {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 in-place reset failed; falling back to a Bluetooth reconnect"
            )
        }
        if context.peripheral.state != .disconnected {
            central.cancelPeripheralConnection(context.peripheral)
        } else {
            scheduleReconnect(context, delay: retryDelay)
        }
    }

    private func markReady(_ context: Context) {
        guard !context.ready else { return }
        let completedInPlaceReset = context.reinitializingInPlace
        context.reinitializingInPlace = false
        context.ready = true
        // Every future reconnect starts with the proven production path. The
        // fallbacks only apply while an individual connection attempt is
        // failing.
        context.connectionPolicyIndex = 0
        context.connectionGeneration += 1
        if completedInPlaceReset {
            JamLog.info(
                .joyCon,
                "Joy-Con 2 in-place reset completed without disconnecting Bluetooth"
            )
        }
        handlers?.ready(context.device.id, context.stickCalibration)
    }

    private func prepareInPlaceReinitialization(_ context: Context) {
        context.commands.removeAll(keepingCapacity: true)
        context.currentCommand = nil
        context.currentCommandAttempt = 0
        context.commandWaitingForWriteReady = false
        context.commandGeneration += 1
        context.reportRateCandidates.removeAll(keepingCapacity: true)
        context.currentReportRate = nil
        context.reportRateGeneration += 1
        context.initializationStarted = false
        context.reinitializingInPlace = true
        context.ready = false
        context.stickCalibration = nil
        context.connectionGeneration += 1
    }

    private func fallbackToReconnectForReset(_ context: Context) {
        JamLog.error(
            .joyCon,
            "Joy-Con 2 cannot reset in place because its live GATT session is incomplete; "
                + "falling back to a Bluetooth reconnect"
        )
        context.reinitializingInPlace = false
        context.retryNotBefore = 0
        switch context.peripheral.state {
        case .disconnected:
            requestConnection(context)
        case .connected, .connecting, .disconnecting:
            central.cancelPeripheralConnection(context.peripheral)
        @unknown default:
            central.cancelPeripheralConnection(context.peripheral)
        }
    }

    private func resetConnectionState(_ context: Context) {
        context.input = nil
        context.command = nil
        context.response = nil
        context.reportRateDescriptor = nil
        context.inputDescriptorDiscoveryStarted = false
        context.inputDescriptorsDiscovered = false
        context.responseNotificationRequested = false
        context.commands.removeAll(keepingCapacity: true)
        context.currentCommand = nil
        context.currentCommandAttempt = 0
        context.commandWaitingForWriteReady = false
        context.commandGeneration += 1
        context.reportRateCandidates.removeAll(keepingCapacity: true)
        context.currentReportRate = nil
        context.reportRateGeneration += 1
        context.initializationStarted = false
        context.reinitializingInPlace = false
        context.ready = false
        context.stickCalibration = nil
    }
}

extension JoyCon2BLESession: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        switch central.state {
        case .poweredOn:
            startScanningIfPossible()
        case .unknown, .resetting:
            break
        case .poweredOff:
            handlers?.unavailable("Bluetooth is turned off")
        case .unauthorized:
            handlers?.unavailable("JamCon does not have Bluetooth permission")
        case .unsupported:
            handlers?.unavailable("Bluetooth Low Energy is unsupported on this Mac")
        @unknown default:
            handlers?.unavailable("Bluetooth entered an unknown state")
        }
    }

    func centralManager(
        _ central: CBCentralManager,
        didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any],
        rssi RSSI: NSNumber
    ) {
        guard started,
              let manufacturerData = advertisementData[CBAdvertisementDataManufacturerDataKey] as? Data,
              let identity = JoyCon2BLEProtocol.decodeAdvertisement(manufacturerData),
              identity.vendorID == JoyCon2BLEProtocol.nintendoVendorID,
              let handedness = JoyCon2BLEProtocol.handedness(productID: identity.productID) else { return }

        let id = deviceID(for: peripheral)
        let localName = advertisementData[CBAdvertisementDataLocalNameKey] as? String
        let device = JoyCon2BLEDevice(
            id: id,
            name: JoyCon2BLEProtocol.userFacingDeviceName(
                peripheralName: peripheral.name,
                advertisedName: localName,
                handedness: handedness
            ),
            productID: identity.productID,
            handedness: handedness
        )
        if let existing = contextsByID[id] {
            existing.device = device
            if existing.peripheral !== peripheral {
                existing.peripheral.delegate = nil
                existing.peripheral = peripheral
            }
            contextIDByPeripheral[peripheral.identifier] = id
            handlers?.discovered(device)
        } else {
            let context = Context(device: device, peripheral: peripheral)
            contextsByID[id] = context
            contextIDByPeripheral[peripheral.identifier] = id
            handlers?.discovered(device)
        }
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard let context = context(for: peripheral) else { return }
        guard desiredDeviceIDs.contains(context.device.id) else {
            central.cancelPeripheralConnection(peripheral)
            return
        }
        context.retryNotBefore = 0
        context.reconnectScheduled = false
        JamLog.info(.joyCon, "Joy-Con 2 Bluetooth connected: \(context.device.name)")
        resetConnectionState(context)
        peripheral.delegate = self
        peripheral.discoverServices([serviceUUID])
    }

    func centralManager(
        _ central: CBCentralManager,
        didFailToConnect peripheral: CBPeripheral,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral) else { return }
        context.connectionGeneration += 1
        handlers?.disconnected(context.device.id, error?.localizedDescription ?? "Connection failed")
        guard desiredDeviceIDs.contains(context.device.id) else { return }
        advanceConnectionPolicy(context)
        context.retryNotBefore = CACurrentMediaTime() + 30
        scheduleReconnect(context, delay: 30)
    }

    func centralManager(
        _ central: CBCentralManager,
        didDisconnectPeripheral peripheral: CBPeripheral,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral) else { return }
        context.connectionGeneration += 1
        resetConnectionState(context)
        handlers?.disconnected(context.device.id, error?.localizedDescription)
        guard desiredDeviceIDs.contains(context.device.id) else {
            context.retryNotBefore = 0
            context.reconnectScheduled = false
            return
        }
        let delay = max(5, context.retryNotBefore - CACurrentMediaTime())
        context.retryNotBefore = CACurrentMediaTime() + delay
        scheduleReconnect(context, delay: delay)
    }
}

extension JoyCon2BLESession: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: (any Error)?) {
        guard let context = context(for: peripheral) else { return }
        guard error == nil,
              let service = peripheral.services?.first(where: { $0.uuid == serviceUUID }) else {
            failInitialization(
                context,
                reason: error?.localizedDescription ?? "Nintendo BLE service missing"
            )
            return
        }
        peripheral.discoverCharacteristics(
            [inputUUID, commandUUID, responseUUID],
            for: service
        )
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didDiscoverCharacteristicsFor service: CBService,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral) else { return }
        guard error == nil,
              let characteristics = service.characteristics else {
            failInitialization(
                context,
                reason: error?.localizedDescription ?? "Nintendo BLE characteristics missing"
            )
            return
        }
        let discovered = characteristics.map {
            "\($0.uuid.uuidString)[0x\(String($0.properties.rawValue, radix: 16))]"
        }.joined(separator: ", ")
        JamLog.debug(
            .joyCon,
            "Joy-Con 2 GATT service \(service.uuid.uuidString): \(discovered)"
        )
        context.input = characteristics.first(where: { $0.uuid == inputUUID })
            ?? context.input
        context.command = characteristics.first(where: { $0.uuid == commandUUID })
            ?? context.command
        context.response = characteristics.first(where: { $0.uuid == responseUUID })
            ?? context.response

        if let input = context.input,
           !context.inputDescriptorDiscoveryStarted {
            context.inputDescriptorDiscoveryStarted = true
            peripheral.discoverDescriptors(for: input)
        }
        if let response = context.response,
           !response.isNotifying,
           !context.responseNotificationRequested {
            context.responseNotificationRequested = true
            peripheral.setNotifyValue(true, for: response)
        }
        guard context.input != nil,
              context.command != nil,
              context.response != nil else {
            failInitialization(context, reason: "required Nintendo BLE characteristics missing")
            return
        }
        JamLog.info(.joyCon, "Joy-Con 2 transport characteristics discovered: \(context.device.name)")
        beginInitialization(context)
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didDiscoverDescriptorsFor characteristic: CBCharacteristic,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral),
              characteristic.uuid == inputUUID else { return }
        context.inputDescriptorsDiscovered = true

        if let error {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 input descriptor discovery failed: \(error.localizedDescription)"
            )
        } else {
            let descriptors = characteristic.descriptors ?? []
            context.reportRateDescriptor = descriptors.first {
                $0.uuid == reportRateDescriptorUUID
            }
            let description = descriptors.map(\.uuid.uuidString).joined(separator: ", ")
            JamLog.debug(
                .joyCon,
                "Joy-Con 2 input descriptors: \(description.isEmpty ? "none" : description)"
            )
        }
        beginInitialization(context)
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didUpdateNotificationStateFor characteristic: CBCharacteristic,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral) else { return }
        if let error {
            failInitialization(
                context,
                reason: "notification setup failed for \(characteristic.uuid.uuidString): \(error.localizedDescription)"
            )
            return
        }
        if characteristic.uuid == responseUUID, characteristic.isNotifying {
            beginInitialization(context)
        } else if characteristic.uuid == inputUUID, characteristic.isNotifying {
            markReady(context)
        }
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didUpdateValueFor characteristic: CBCharacteristic,
        error: (any Error)?
    ) {
        guard error == nil,
              let context = context(for: peripheral),
              let data = characteristic.value else { return }
        if characteristic.uuid == inputUUID {
            guard context.ready else { return }
            handlers?.input(context.device.id, Array(data), CACurrentMediaTime())
        } else if characteristic.uuid == responseUUID {
            handleCommandResponse(data, context: context)
        }
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didWriteValueFor characteristic: CBCharacteristic,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral),
              characteristic.uuid == commandUUID,
              let error,
              context.currentCommand != nil else { return }
        retryCurrentCommand(context, reason: "command write failed: \(error.localizedDescription)")
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didWriteValueFor descriptor: CBDescriptor,
        error: (any Error)?
    ) {
        guard let context = context(for: peripheral),
              let characteristicUUID = descriptor.characteristic?.uuid else { return }

        guard characteristicUUID == inputUUID,
              let reportRate = context.currentReportRate else { return }
        context.currentReportRate = nil
        context.reportRateGeneration += 1

        if let error {
            JamLog.error(
                .joyCon,
                "Joy-Con 2 \(Int(reportRate.hertz)) Hz report-rate request failed: \(error.localizedDescription)"
            )
            tryNextReportRate(context)
            return
        }

        JamLog.info(
            .joyCon,
            "Joy-Con 2 requested report rate: \(Int(reportRate.hertz)) Hz"
        )
        // Native report 0x08 and the common report are mutually exclusive on
        // the tested firmware. The native 40-byte motion block is packed and
        // has no validated decoder, so production always uses common report
        // 0x05 for controls and motion.
        subscribeToInput(context)
    }

    func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
        guard let context = context(for: peripheral),
              context.currentCommand != nil,
              context.commandWaitingForWriteReady else { return }
        sendCurrentCommand(context)
    }
}
