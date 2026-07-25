import Foundation
import CoreGraphics
import QuartzCore
import os

/// The real-time input processing engine
/// This is WORLD 1 - has NO knowledge of SwiftUI, @Published, or ObservableObject
/// Processes HID input and drives mouse/keyboard output
/// Mutable processing state is confined to `engineQueue`. The controller
/// backends and settings/debug dependencies provide their own synchronization.
final class InputEngine: @unchecked Sendable {

    static let inputPerformanceLog = OSLog(
        subsystem: Bundle.main.bundleIdentifier ?? "com.jamcon.app",
        category: .pointsOfInterest
    )

    // MARK: - Dependencies

    let settings: SettingsStore
    let debugBuffer: DebugBuffer

    // MARK: - Controllers

    let senseController: SenseController
    let senseBackend: SenseInputDeviceBackend
    let joyConController: JoyConHIDController
    let joyCon2Backend: JoyCon2BLEInputDeviceBackend
    let g502xController: G502XHIDController
    let backendRegistry: InputDeviceBackendRegistry
    let mouseController: MouseController
    let actionExecutor: ActionExecutor
    let holdScheduler: HoldScheduling

    // MARK: - Threading

    /// Serial queue that owns all engine state mutations and input processing.
    let engineQueue = DispatchQueue(label: "JamCon.engineQueue", qos: .userInitiated)
    private let engineQueueKey = DispatchSpecificKey<Void>()

    private func engineQueueSync<T>(_ work: () -> T) -> T {
        if DispatchQueue.getSpecific(key: engineQueueKey) != nil {
            return work()
        }
        return engineQueue.sync(execute: work)
    }

    private func engineQueueAsync(_ work: @escaping @Sendable () -> Void) {
        engineQueue.async(execute: work)
    }

    @inline(__always)
    func assertOnEngineQueue(file: StaticString = #fileID, line: UInt = #line) {
        assert(
            DispatchQueue.getSpecific(key: engineQueueKey) != nil,
            "InputEngine state must be accessed on engineQueue",
            file: file,
            line: line
        )
    }

    // MARK: - Managed Device State (Internal - not observable)

    // Hold detection (per button, per device)
    struct ButtonPressState {
        let actions: ButtonActions
        let pressOwner: SyntheticOutputOwner
        let holdOwner: SyntheticOutputOwner
        var holdFired: Bool = false

        init(actions: ButtonActions, device: ManagedDeviceKey, control: String) {
            self.actions = actions
            self.pressOwner = SyntheticOutputOwner(device: device, control: control, role: .press)
            self.holdOwner = SyntheticOutputOwner(device: device, control: control, role: .hold)
        }
    }

    struct GyroModeState {
        var dragButtonHeld: Bool = false
        var scrollButtonHeld: Bool = false
        var radialMenuButtonHeld: Bool = false
    }

    var mouseMode = GyroModeState()

    final class SenseDeviceState {
        let id: String
        let profile: ControllerProfile
        var mode = GyroModeState()
        let gyroProcessor = GyroProcessor()
        var hasPrimedButtonState: Bool = false

        var buttonStates: [Bool]
        var previousButtonStates: [Bool]
        var previousTriggerPressed: Bool = false
        var joystickScrollTiming = JoystickScrollTiming()
        var buttonPressStates: [ButtonPressState?]
        var holdTimers: [DispatchWorkItem?]

        init(id: String, profile: ControllerProfile) {
            self.id = id
            self.profile = profile

            let count = LogicalButton.allCases.count
            self.buttonStates = Array(repeating: false, count: count)
            self.previousButtonStates = Array(repeating: false, count: count)
            self.buttonPressStates = Array(repeating: nil, count: count)
            self.holdTimers = Array(repeating: nil, count: count)
        }

        func cancelHoldTimers() {
            for timer in holdTimers {
                timer?.cancel()
            }
            for i in holdTimers.indices {
                holdTimers[i] = nil
            }
        }
    }

    final class JoyConDeviceState {
        let id: String
        let profile: ControllerProfile
        var mode = GyroModeState()
        let gyroProcessor = GyroProcessor()
        var hasPrimedButtonState: Bool = false
        var requiresNeutralAfterReconnect = false

        var mapping: JoyConButtonMapping
        var buttonStates: [Bool]
        var previousButtonStates: [Bool]
        var buttonPressStates: [ButtonPressState?]
        var holdTimers: [DispatchWorkItem?]

        init(id: String, profile: ControllerProfile) {
            self.id = id
            self.profile = profile
            self.mapping = JoyConButtonMapping(isLeft: profile.isLeft)

            let count = JoyConLogicalButton.count
            self.buttonStates = Array(repeating: false, count: count)
            self.previousButtonStates = Array(repeating: false, count: count)
            self.buttonPressStates = Array(repeating: nil, count: count)
            self.holdTimers = Array(repeating: nil, count: count)
        }

        func cancelHoldTimers() {
            for timer in holdTimers {
                timer?.cancel()
            }
            for i in holdTimers.indices {
                holdTimers[i] = nil
            }
        }
    }

    var senseDevices: [String: SenseDeviceState] = [:]      // controllerID -> state
    var joyConDevices: [String: JoyConDeviceState] = [:]    // controllerID -> state
    var selectedMouseID: String?

    private var batteryLevels: [ManagedDeviceKey: Int] = [:]
    var radialMenuOwner: ManagedDeviceKey?

    // Cached button mappings (avoid allocation on hot path)
    let leftMapping = SenseButtonMapping(isLeft: true)
    let rightMapping = SenseButtonMapping(isLeft: false)

    // G502X button state
    var g502xButtonStates: [Bool]
    var g502xPreviousButtonStates: [Bool]
    var g502xButtonPressStates: [ButtonPressState?]
    var g502xHoldTimers: [DispatchWorkItem?]
    let g502xMapping = G502XButtonMapping()
    var g502xHasPrimedButtonState: Bool = false

    // Mouse movement capture for radial menu (when using G502X)
    let radialMenuLock = OSAllocatedUnfairLock()
    var radialMenuCursorAnchor: CGPoint?
    var radialMenuCursorPollTimer: DispatchSourceTimer?

    // MARK: - Radial Menu State (Internal)

    var radialMenuPosition: CGPoint = .zero
    var radialMenuAnchor: CGPoint = .zero
    var radialMenuAccumulator: CGPoint = .zero

    // MARK: - Radial Menu UI Throttling

    /// Target UI update rate for the radial menu overlay (keeps up with 120Hz displays).
    static let radialMenuUIUpdateInterval = DispatchTimeInterval.nanoseconds(8_333_333)  // ~120Hz
    var radialMenuPendingDelta: CGPoint = .zero
    var radialMenuUIUpdateTimer: DispatchSourceTimer?

    // MARK: - Radial Menu UI Callback

    /// Called when radial menu should show/hide - UI can observe this.
    /// This is the ONLY callback to UI, and it's for radial menu overlay.
    /// The position is reported in Quartz global display coordinates.
    var onRadialMenuShow: ((_ position: CGPoint, _ configuration: RadialMenuConfiguration, _ pointerStyle: RadialMenuPointerStyle) -> Void)?
    var onRadialMenuHide: ((_ selectedItem: RadialMenuItem?) -> Void)?
    var onRadialMenuUpdate: ((_ delta: CGPoint) -> Void)?
    var onRadialMenuSetPosition: ((_ offset: CGPoint) -> Void)?  // For mouse: absolute position

    // MARK: - Connection Callbacks (for UI to update controller list)

    var onControllerListChanged: (() -> Void)?
    var onConnectionChanged: ((_ connected: Bool, _ name: String?, _ kind: ControllerKind) -> Void)?
    var onBatteryLevelChanged: ((_ level: Int) -> Void)?

    // MARK: - Battery Level (thread-safe, polled by UI)

    private let batteryLock = OSAllocatedUnfairLock()
    private var _batteryLevel: Int = 0

    /// Current battery level (0-100), thread-safe for polling
    var batteryLevel: Int {
        batteryLock.withLock { _batteryLevel }
    }

    private func updateBatteryLevel(_ level: Int) {
        let didChange = batteryLock.withLock {
            if _batteryLevel == level {
                return false
            }
            _batteryLevel = level
            return true
        }
        if didChange {
            onBatteryLevelChanged?(level)
        }
    }

    func setBatteryLevel(_ level: Int, for device: ManagedDeviceKey) {
        guard batteryLevels[device] != level else { return }
        batteryLevels[device] = level
        updateBatteryFromLevels()
    }

    func clearBatteryLevel(for device: ManagedDeviceKey) {
        if batteryLevels.removeValue(forKey: device) != nil {
            updateBatteryFromLevels()
        }
    }

    private func updateBatteryFromLevels() {
        let aggregate = batteryLevels.values.min() ?? 0
        updateBatteryLevel(aggregate)
    }

    // MARK: - Running State

    var isRunning: Bool = false
    var inputHealthAggregator = InputHealthAggregator()
    var gyroResponseAggregator = GyroResponseAggregator()

    // MARK: - Initialization

    init(
        settings: SettingsStore,
        debugBuffer: DebugBuffer,
        mouseController: MouseController? = nil,
        actionExecutor: ActionExecutor? = nil,
        holdScheduler: HoldScheduling = DispatchHoldScheduler(),
        senseGameControllerSession: (any SenseGameControllerSessioning)? = nil,
        senseController: SenseController? = nil,
        joyConController: JoyConHIDController? = nil,
        joyCon2Session: (any JoyCon2BLESessioning)? = nil,
        g502xController: G502XHIDController? = nil
    ) {
        self.settings = settings
        self.debugBuffer = debugBuffer
        let mouseController = mouseController ?? MouseController()
        let senseController = senseController ?? SenseController()
        let joyConController = joyConController ?? JoyConHIDController()
        let g502xController = g502xController ?? G502XHIDController()
        self.mouseController = mouseController
        self.actionExecutor = actionExecutor ?? ActionExecutor(mouseController: mouseController)
        self.holdScheduler = holdScheduler
        self.senseController = senseController
        self.senseBackend = SenseInputDeviceBackend(
            discovery: senseController,
            gameControllerSession: senseGameControllerSession ?? SenseGameControllerSession()
        )
        self.joyConController = joyConController
        self.joyCon2Backend = JoyCon2BLEInputDeviceBackend(
            session: joyCon2Session ?? JoyCon2BLESession()
        )
        self.g502xController = g502xController
        self.backendRegistry = InputDeviceBackendRegistry(backends: [
            senseBackend,
            joyConController,
            joyCon2Backend,
            g502xController,
        ])
        // Initialize G502X button state arrays
        let g502xButtonCount = G502XLogicalButton.count
        self.g502xButtonStates = Array(repeating: false, count: g502xButtonCount)
        self.g502xPreviousButtonStates = Array(repeating: false, count: g502xButtonCount)
        self.g502xButtonPressStates = Array(repeating: nil, count: g502xButtonCount)
        self.g502xHoldTimers = Array(repeating: nil, count: g502xButtonCount)
        engineQueue.setSpecific(key: engineQueueKey, value: ())
    }

    // MARK: - Lifecycle

    func start() {
        let shouldStart = engineQueueSync {
            guard !isRunning else { return false }
            inputHealthAggregator.reset()
            gyroResponseAggregator.reset()
            isRunning = true
            return true
        }
        guard shouldStart else { return }

        JamLog.info(.engine, "Input engine starting")
        setupCallbacks()

        let startResults = backendRegistry.startAll()
        let failedBackends = startResults.filter { !$0.started }.map { $0.backend.id.rawValue }
        if !failedBackends.isEmpty {
            JamLog.error(.engine, "Input backends failed to start: \(failedBackends.joined(separator: ", "))")
        }
        JamLog.info(.engine, "Input engine started")
    }

    func stop() {
        let shouldStop = engineQueueSync {
            guard isRunning else { return false }
            isRunning = false
            flushInputHealth(at: CACurrentMediaTime())
            flushGyroResponseHealth(at: CACurrentMediaTime())

            if radialMenuOwner != nil || radialMenuCursorPollTimer != nil || radialMenuUIUpdateTimer != nil {
                let owner = radialMenuOwner
                stopRadialMenuUIUpdateTimer()
                stopRadialMenuCursorTracking()
                mouseMode.radialMenuButtonHeld = false
                if owner?.kind != .mouse {
                    mouseController.showCursor()
                }
                radialMenuOwner = nil
                onRadialMenuHide?(nil)
            }

            // Release all controller-owned actions before discarding their state.
            for device in senseDevices.values {
                resetSenseTransientState(device)
            }
            for device in joyConDevices.values {
                resetJoyConTransientState(device)
            }
            resetG502XButtonStateBaseline()
            actionExecutor.releaseAll()
            mouseController.forceShowCursor()

            senseDevices.removeAll(keepingCapacity: true)
            joyConDevices.removeAll(keepingCapacity: true)
            batteryLevels.removeAll(keepingCapacity: true)
            selectedMouseID = nil

            return true
        }
        guard shouldStop else { return }

        backendRegistry.stopAll()
        JamLog.info(.engine, "Input engine stopped; synthetic outputs released")
    }

    /// Apply the global output toggle synchronously so a key or mouse button can
    /// never remain down while subsequent physical release reports are ignored.
    func setInputEnabled(_ enabled: Bool) {
        guard !enabled else {
            JamLog.info(.engine, "Input enabled")
            return
        }

        engineQueueSync {
            if radialMenuOwner != nil || radialMenuCursorPollTimer != nil || radialMenuUIUpdateTimer != nil {
                let owner = radialMenuOwner
                stopRadialMenuUIUpdateTimer()
                stopRadialMenuCursorTracking()
                if owner?.kind != .mouse {
                    mouseController.showCursor()
                }
                radialMenuOwner = nil
                onRadialMenuHide?(nil)
            }

            for device in senseDevices.values {
                resetSenseTransientState(device)
            }
            for device in joyConDevices.values {
                resetJoyConTransientState(device)
            }
            resetG502XButtonStateBaseline()
            mouseMode = GyroModeState()
            actionExecutor.releaseAll()
            mouseController.forceShowCursor()
        }
        JamLog.info(.engine, "Input disabled; synthetic outputs released")
    }

    func recordInputHealth(
        device: ManagedDeviceKey,
        inputTimestamp: TimeInterval?,
        timestampSource: InputTimestampSource,
        receivedTimestamp: TimeInterval,
        engineStartTimestamp: TimeInterval,
        engineEndTimestamp: TimeInterval
    ) {
        assertOnEngineQueue()
        guard let summary = inputHealthAggregator.record(
            device: device,
            inputTimestamp: inputTimestamp,
            timestampSource: timestampSource,
            receivedTimestamp: receivedTimestamp,
            engineStartTimestamp: engineStartTimestamp,
            engineEndTimestamp: engineEndTimestamp
        ) else { return }
        JamLog.info(.health, summary.logMessage)
    }

    private func flushInputHealth(at timestamp: TimeInterval) {
        for summary in inputHealthAggregator.flush(at: timestamp) {
            JamLog.info(.health, summary.logMessage)
        }
    }

    func recordGyroResponseHealth(
        device: ManagedDeviceKey,
        timestamp: TimeInterval,
        sample: GyroResponseSample?
    ) {
        assertOnEngineQueue()
        guard let sample,
              let summary = gyroResponseAggregator.record(
                  device: device,
                  timestamp: timestamp,
                  sample: sample
              ) else { return }
        JamLog.info(.health, summary.logMessage)
    }

    private func flushGyroResponseHealth(at timestamp: TimeInterval) {
        for summary in gyroResponseAggregator.flush(at: timestamp) {
            JamLog.info(.health, summary.logMessage)
        }
    }

    // MARK: - Controller Selection

    /// Enable/disable processing for a specific physical device.
    func setDeviceManaged(
        id: String,
        kind: ControllerKind,
        isLeft: Bool,
        profileVariant: ControllerProfileVariant = .standard,
        managed: Bool
    ) {
        engineQueueSync {
            switch kind {
            case .sense:
                let profile = ControllerProfile(kind: .sense, isLeft: isLeft)
                if managed {
                    if senseDevices[id]?.profile != profile {
                        if let existing = senseDevices[id] {
                            resetSenseTransientState(existing)
                        }
                        senseDevices[id] = SenseDeviceState(id: id, profile: profile)
                    }
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: true)
                } else {
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: false)
                    removeSenseDevice(id: id)
                }

            case .joyCon:
                let profile = ControllerProfile(kind: .joyCon, isLeft: isLeft, variant: profileVariant)
                if managed {
                    if joyConDevices[id]?.profile != profile {
                        if let existing = joyConDevices[id] {
                            resetJoyConTransientState(existing)
                        }
                        joyConDevices[id] = JoyConDeviceState(id: id, profile: profile)
                    }
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: true)
                } else {
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: false)
                    removeJoyConDevice(id: id)
                }

            case .mouse:
                if managed {
                    if selectedMouseID == id,
                       backendRegistry.backend(for: kind)?.isConnected == true {
                        return
                    }
                    selectedMouseID = id
                    resetG502XButtonStateBaseline()
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: true)
                } else if selectedMouseID == id {
                    let key = ManagedDeviceKey(kind: .mouse, id: id)
                    cancelRadialMenuIfOwned(by: key)
                    selectedMouseID = nil
                    resetG502XButtonStateBaseline()
                    actionExecutor.releaseAll(for: key)
                    backendRegistry.setDeviceManaged(id: id, kind: kind, managed: false)
                }
            }
        }
    }

    private func removeSenseDevice(id: String) {
        let key = ManagedDeviceKey(kind: .sense, id: id)
        if let device = senseDevices.removeValue(forKey: id) {
            resetSenseTransientState(device)
        }
        clearBatteryLevel(for: key)
        cancelRadialMenuIfOwned(by: key)
    }

    private func removeJoyConDevice(id: String) {
        let key = ManagedDeviceKey(kind: .joyCon, id: id)
        if let device = joyConDevices.removeValue(forKey: id) {
            resetJoyConTransientState(device)
        }
        clearBatteryLevel(for: key)
        cancelRadialMenuIfOwned(by: key)
    }

    /// Get list of available controllers
    var availableControllers: [ControllerInfo] {
        backendRegistry.availableDevicesSnapshot()
    }

    /// Current connection state
    var isConnected: Bool {
        backendRegistry.isConnected
    }

    /// Recalibrate the gyro
    func recalibrate() {
        engineQueueSync {
            for device in senseDevices.values {
                device.gyroProcessor.reset()
            }
            for device in joyConDevices.values {
                device.gyroProcessor.reset()
            }
        }
    }

    /// Soft-restart one managed device without changing persistent management
    /// or system pairing. Backends may reinitialize over their existing
    /// transport session, with close/reopen retained as a fallback.
    @discardableResult
    func resetDevice(id: String, kind: ControllerKind) -> Bool {
        engineQueueSync {
            switch kind {
            case .sense:
                guard let device = senseDevices[id] else { return false }
                resetSenseTransientState(device)

            case .joyCon:
                guard let device = joyConDevices[id] else { return false }
                resetJoyConTransientState(device)
                device.requiresNeutralAfterReconnect = true
                device.mapping.calibration.reset(requireNeutralValidation: true)

            case .mouse:
                guard selectedMouseID == id else { return false }
                resetG502XButtonStateBaseline()
                actionExecutor.releaseAll(for: ManagedDeviceKey(kind: .mouse, id: id))
            }

            guard backendRegistry.restartDevice(id: id, kind: kind) else {
                JamLog.error(.engine, "Could not reset unmanaged or unavailable device \(kind.rawValue):\(id)")
                return false
            }
            JamLog.info(.engine, "Reset device transport: \(kind.rawValue):\(id)")
            return true
        }
    }

    // MARK: - Callbacks Setup

    private func setupCallbacks() {
        backendRegistry.setEventHandlers(
            devicesChanged: { [weak self] _ in
                self?.engineQueueAsync { [weak self] in
                    self?.onControllerListChanged?()
                }
            },
            connectionChanged: { [weak self] event in
                self?.engineQueueAsync { [weak self] in
                    self?.handleBackendConnectionEvent(event)
                }
            },
            inputFrame: { [weak self] frame in
                self?.engineQueueAsync { [weak self] in
                    self?.processInputDeviceFrame(frame)
                }
            }
        )
    }

    private func processInputDeviceFrame(_ frame: InputDeviceFrame) {
        assertOnEngineQueue()
        guard isRunning else { return }
        guard backendRegistry.backend(id: frame.backend.id)?.backendDescriptor == frame.backend else {
            JamLog.errorThrottled(
                .engine,
                key: "input.unknown-backend.\(frame.backend.id.rawValue)",
                interval: 2,
                "Dropping input from unregistered backend \(frame.backend.id.rawValue)"
            )
            return
        }

        switch frame.backend.kind {
        case .sense:
            processSenseReport(frame)
        case .joyCon:
            processJoyConReport(frame)
        case .mouse:
            processG502XReport(frame)
        }
    }

    private func handleBackendConnectionEvent(_ event: InputDeviceBackendConnectionEvent) {
        assertOnEngineQueue()
        guard isRunning else { return }
        let kind = event.backend.kind

        switch kind {
        case .sense:
            if let id = event.deviceID {
                let key = ManagedDeviceKey(kind: kind, id: id)
                if let device = senseDevices[id] {
                    resetSenseTransientState(device)
                }
                if !event.connected {
                    clearBatteryLevel(for: key)
                    cancelRadialMenuIfOwned(by: key)
                }
            }

        case .joyCon:
            if let id = event.deviceID {
                let key = ManagedDeviceKey(kind: kind, id: id)
                if let device = joyConDevices[id] {
                    resetJoyConTransientState(device)
                    if event.connected {
                        device.mapping.calibration.reset(
                            requireNeutralValidation: device.requiresNeutralAfterReconnect
                        )
                        device.requiresNeutralAfterReconnect = false
                    }
                }
                if !event.connected {
                    clearBatteryLevel(for: key)
                    cancelRadialMenuIfOwned(by: key)
                }
            }

        case .mouse:
            resetG502XButtonStateBaseline()
            if !event.connected, let id = event.deviceID {
                actionExecutor.releaseAll(for: ManagedDeviceKey(kind: kind, id: id))
            }
        }

        onConnectionChanged?(event.connected, event.deviceName, kind)
    }

    func resetSenseTransientState(_ device: SenseDeviceState) {
        for idx in device.holdTimers.indices {
            device.holdTimers[idx]?.cancel()
            device.holdTimers[idx] = nil

            guard let pressState = device.buttonPressStates[idx] else { continue }

            actionExecutor.execute(pressState.actions.press, isPressed: false, owner: pressState.pressOwner)
            if pressState.holdFired {
                actionExecutor.execute(pressState.actions.hold, isPressed: false, owner: pressState.holdOwner)
            }

            device.buttonPressStates[idx] = nil
        }

        device.previousTriggerPressed = false
        device.joystickScrollTiming.reset()
        for idx in device.buttonStates.indices {
            device.buttonStates[idx] = false
            device.previousButtonStates[idx] = false
        }
        device.hasPrimedButtonState = false
        device.mode = GyroModeState()
        device.gyroProcessor.reset()
        actionExecutor.releaseAll(for: ManagedDeviceKey(kind: .sense, id: device.id))
    }

    private func resetJoyConTransientState(_ device: JoyConDeviceState) {
        for idx in device.holdTimers.indices {
            device.holdTimers[idx]?.cancel()
            device.holdTimers[idx] = nil

            guard let pressState = device.buttonPressStates[idx] else { continue }

            actionExecutor.execute(pressState.actions.press, isPressed: false, owner: pressState.pressOwner)
            if pressState.holdFired {
                actionExecutor.execute(pressState.actions.hold, isPressed: false, owner: pressState.holdOwner)
            }

            device.buttonPressStates[idx] = nil
        }

        for idx in device.buttonStates.indices {
            device.buttonStates[idx] = false
            device.previousButtonStates[idx] = false
        }
        device.hasPrimedButtonState = false
        device.mode = GyroModeState()
        device.gyroProcessor.reset()
        actionExecutor.releaseAll(for: ManagedDeviceKey(kind: .joyCon, id: device.id))
    }

}
