import Foundation
import CoreGraphics

// MARK: - Mouse Button Types

enum MouseButton: String, Codable, CaseIterable, Hashable, Sendable {
    case left
    case right
    case middle

    var displayName: String {
        switch self {
        case .left: return "Left Click"
        case .right: return "Right Click"
        case .middle: return "Middle Click"
        }
    }
}

// MARK: - System Actions

enum SystemAction: String, Codable, CaseIterable, Hashable, Sendable {
    case missionControl
    case launchpad
    case showDesktop
    case appSwitcher
    case playPause

    var displayName: String {
        switch self {
        case .missionControl: return "Mission Control"
        case .launchpad: return "Launchpad"
        case .showDesktop: return "Show Desktop"
        case .appSwitcher: return "App Switcher"
        case .playPause: return "Play/Pause"
        }
    }
}

// MARK: - Key Combination

struct KeyCombo: Codable, Hashable, Sendable {
    let keyCode: UInt16
    let modifiers: UInt64  // CGEventFlags.rawValue

    var eventFlags: CGEventFlags {
        CGEventFlags(rawValue: modifiers)
    }

    init(keyCode: UInt16, modifiers: CGEventFlags = []) {
        self.keyCode = keyCode
        self.modifiers = modifiers.rawValue
    }

    var displayName: String {
        var parts: [String] = []

        let flags = eventFlags
        if flags.contains(.maskControl) { parts.append("⌃") }
        if flags.contains(.maskAlternate) { parts.append("⌥") }
        if flags.contains(.maskShift) { parts.append("⇧") }
        if flags.contains(.maskCommand) { parts.append("⌘") }

        parts.append(keyCodeName)
        return parts.joined()
    }

    private var keyCodeName: String {
        // Special keys that don't vary by keyboard layout
        switch keyCode {
        case 36: return "Return"
        case 48: return "Tab"
        case 49: return "Space"
        case 51: return "Delete"
        case 53: return "Escape"
        case 96: return "F5"
        case 97: return "F6"
        case 98: return "F7"
        case 99: return "F3"
        case 100: return "F8"
        case 101: return "F9"
        case 103: return "F11"
        case 105: return "F13"
        case 107: return "F14"
        case 109: return "F10"
        case 111: return "F12"
        case 113: return "F15"
        case 115: return "Home"
        case 116: return "Page Up"
        case 117: return "Forward Delete"
        case 118: return "F4"
        case 119: return "End"
        case 120: return "F2"
        case 121: return "Page Down"
        case 122: return "F1"
        case 123: return "←"
        case 124: return "→"
        case 125: return "↓"
        case 126: return "↑"
        default: break
        }

        // Get actual character from current keyboard layout
        if let char = Self.characterForKeyCode(keyCode) {
            return char.uppercased()
        }
        return "Key \(keyCode)"
    }

    /// Translates a key code to its character using the current keyboard layout
    private static func characterForKeyCode(_ keyCode: UInt16) -> String? {
        guard let event = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(keyCode), keyDown: true) else {
            return nil
        }
        var length = 0
        var chars = [UniChar](repeating: 0, count: 4)
        event.keyboardGetUnicodeString(maxStringLength: 4, actualStringLength: &length, unicodeString: &chars)
        guard length > 0 else { return nil }
        let result = String(utf16CodeUnits: chars, count: length)
        // Filter out control characters
        guard let scalar = result.unicodeScalars.first,
              !CharacterSet.controlCharacters.contains(scalar) else {
            return nil
        }
        return result
    }
}

// MARK: - Button Action

enum ButtonAction: Codable, Hashable, Sendable {
    case none
    case mouseClick(MouseButton)
    case keyPress(KeyCombo)
    case systemAction(SystemAction)
    case drag       // Gyro moves cursor only when this button is held
    case scroll     // Gyro scrolls content when this button is held
    case radialMenu // Opens radial menu while held, gyro selects segment

    var displayName: String {
        switch self {
        case .none:
            return "None"
        case .mouseClick(let button):
            return button.displayName
        case .keyPress(let combo):
            return combo.displayName
        case .systemAction(let action):
            return action.displayName
        case .drag:
            return "Drag (hold to move)"
        case .scroll:
            return "Scroll (hold to scroll)"
        case .radialMenu:
            return "Radial Menu"
        }
    }

    /// Whether this action is a gyro override mode
    var isGyroMode: Bool {
        switch self {
        case .drag, .scroll, .radialMenu: return true
        default: return false
        }
    }

    // MARK: - Codable

    private enum CodingKeys: String, CodingKey {
        case type, mouseButton, keyCombo, systemAction
    }

    private enum ActionType: String, Codable {
        case none, mouseClick, keyPress, systemAction, drag, scroll, radialMenu
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let type = try container.decode(ActionType.self, forKey: .type)

        switch type {
        case .none:
            self = .none
        case .mouseClick:
            let button = try container.decode(MouseButton.self, forKey: .mouseButton)
            self = .mouseClick(button)
        case .keyPress:
            let combo = try container.decode(KeyCombo.self, forKey: .keyCombo)
            self = .keyPress(combo)
        case .systemAction:
            let action = try container.decode(SystemAction.self, forKey: .systemAction)
            self = .systemAction(action)
        case .drag:
            self = .drag
        case .scroll:
            self = .scroll
        case .radialMenu:
            self = .radialMenu
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)

        switch self {
        case .none:
            try container.encode(ActionType.none, forKey: .type)
        case .mouseClick(let button):
            try container.encode(ActionType.mouseClick, forKey: .type)
            try container.encode(button, forKey: .mouseButton)
        case .keyPress(let combo):
            try container.encode(ActionType.keyPress, forKey: .type)
            try container.encode(combo, forKey: .keyCombo)
        case .systemAction(let action):
            try container.encode(ActionType.systemAction, forKey: .type)
            try container.encode(action, forKey: .systemAction)
        case .drag:
            try container.encode(ActionType.drag, forKey: .type)
        case .scroll:
            try container.encode(ActionType.scroll, forKey: .type)
        case .radialMenu:
            try container.encode(ActionType.radialMenu, forKey: .type)
        }
    }
}

// MARK: - Button Actions (Press + Hold)

/// Combined actions for press (tap) and hold (long press) on a button
struct ButtonActions: Codable, Hashable, Sendable {
    var press: ButtonAction
    var hold: ButtonAction

    init(press: ButtonAction = .none, hold: ButtonAction = .none) {
        self.press = press
        self.hold = hold
    }

    /// Check if any action is configured
    var hasAny: Bool { press != .none || hold != .none }

    /// Whether the press action is a gyro mode (drag/scroll)
    var pressIsGyroMode: Bool { press.isGyroMode }
}

// MARK: - Button Mapping Profile

struct SenseButtonMappingProfile: Codable, Sendable {
    private(set) var mappings: [String: ButtonActions]  // LogicalButton.rawValue -> actions
    private var actionsCache: [ButtonActions] = Array(repeating: ButtonActions(), count: LogicalButton.allCases.count)
    var triggerThreshold: UInt8
    var holdThreshold: Double  // Seconds before hold action fires

    // Cached gyro-mode mapping flags to avoid per-frame dictionary scans
    private var dragMapped: Bool = false
    private var scrollMapped: Bool = false
    private var radialMenuMapped: Bool = false

    static let userDefaultsKey = "SenseButtonMappingProfile_v2"  // New key for new format

    private enum CodingKeys: String, CodingKey {
        case mappings
        case triggerThreshold
        case holdThreshold
    }

    init(mappings: [String: ButtonActions] = [:], triggerThreshold: UInt8 = 128, holdThreshold: Double = 0.3) {
        self.mappings = mappings
        self.triggerThreshold = triggerThreshold
        self.holdThreshold = holdThreshold
        rebuildActionsCache()
        recomputeMappingFlags()
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.mappings = try container.decodeIfPresent([String: ButtonActions].self, forKey: .mappings) ?? [:]
        self.triggerThreshold = try container.decodeIfPresent(UInt8.self, forKey: .triggerThreshold) ?? 128
        self.holdThreshold = try container.decodeIfPresent(Double.self, forKey: .holdThreshold) ?? 0.3
        rebuildActionsCache()
        recomputeMappingFlags()
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(mappings, forKey: .mappings)
        try container.encode(triggerThreshold, forKey: .triggerThreshold)
        try container.encode(holdThreshold, forKey: .holdThreshold)
    }

    static var `default`: Self {
        var profile = SenseButtonMappingProfile()
        // Default mappings (press only)
        profile.mappings[LogicalButton.bumper.rawValue] = ButtonActions(press: .mouseClick(.right))
        profile.mappings[LogicalButton.trigger.rawValue] = ButtonActions(press: .mouseClick(.left))
        profile.mappings[LogicalButton.stickClick.rawValue] = ButtonActions(press: .mouseClick(.middle))
        profile.mappings[LogicalButton.menuButton.rawValue] = ButtonActions(press: .systemAction(.playPause))
        profile.mappings[LogicalButton.playStation.rawValue] = ButtonActions(press: .systemAction(.missionControl))
        profile.rebuildActionsCache()
        profile.recomputeMappingFlags()
        return profile
    }

    func actions(for button: LogicalButton) -> ButtonActions {
        actionsCache[button.index]
    }

    mutating func setActions(_ actions: ButtonActions, for button: LogicalButton) {
        mappings[button.rawValue] = actions
        actionsCache[button.index] = actions
        recomputeMappingFlags()
    }

    mutating func setPressAction(_ action: ButtonAction, for button: LogicalButton) {
        var current = actions(for: button)
        current.press = action
        // Clear hold if press is a gyro mode
        if action.isGyroMode {
            current.hold = .none
        }
        mappings[button.rawValue] = current
        actionsCache[button.index] = current
        recomputeMappingFlags()
    }

    mutating func setHoldAction(_ action: ButtonAction, for button: LogicalButton) {
        var current = actions(for: button)
        current.hold = action
        mappings[button.rawValue] = current
        actionsCache[button.index] = current
        // Hold does not affect gyro-mode flags, but recompute defensively in case of future changes
        recomputeMappingFlags()
    }

    /// Check if any button is mapped to drag mode (in press action)
    var hasDragMapping: Bool {
        dragMapped
    }

    /// Check if any button is mapped to scroll mode (in press action)
    var hasScrollMapping: Bool {
        scrollMapped
    }

    /// Check if any button is mapped to radial menu mode (in press action)
    var hasRadialMenuMapping: Bool {
        radialMenuMapped
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.userDefaultsKey)
        }
    }

    /// Save this profile for a specific controller side
    func save(for profile: ControllerProfile) {
        guard profile.kind == .sense else { return }
        let key = "buttons.\(profile.persistenceKey)"
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: key)
        }
    }

    static func load() -> Self {
        // Try loading new format first
        if let data = UserDefaults.standard.data(forKey: userDefaultsKey),
           let profile = try? JSONDecoder().decode(Self.self, from: data) {
            return profile
        }

        // Try migrating from old format
        let oldKey = "SenseButtonMappingProfile"
        if let oldData = UserDefaults.standard.data(forKey: oldKey),
           let oldProfile = try? JSONDecoder().decode(OldProfile.self, from: oldData) {
            // Migrate old single-action mappings to new press/hold format
            var newProfile = SenseButtonMappingProfile()
            newProfile.triggerThreshold = oldProfile.triggerThreshold
            for (key, action) in oldProfile.mappings {
                newProfile.mappings[key] = ButtonActions(press: action)
            }
            // Save in new format and clean up old
            newProfile.rebuildActionsCache()
            newProfile.recomputeMappingFlags()
            newProfile.save()
            UserDefaults.standard.removeObject(forKey: oldKey)
            return newProfile
        }

        return .default
    }

    /// Load profile for a specific controller side
    static func load(for profile: ControllerProfile) -> Self {
        guard profile.kind == .sense else { return .default }
        let key = "buttons.\(profile.persistenceKey)"

        // Try loading per-profile settings
        if let data = UserDefaults.standard.data(forKey: key),
           let loaded = try? JSONDecoder().decode(Self.self, from: data) {
            return loaded
        }

        // Fall back to global profile (for migration)
        return load()
    }

    /// Check if per-profile settings exist
    static func hasPerProfileSettings(for profile: ControllerProfile) -> Bool {
        guard profile.kind == .sense else { return false }
        let key = "buttons.\(profile.persistenceKey)"
        return UserDefaults.standard.data(forKey: key) != nil
    }

    private mutating func rebuildActionsCache() {
        for button in LogicalButton.allCases {
            actionsCache[button.index] = mappings[button.rawValue] ?? ButtonActions()
        }
    }

    private mutating func recomputeMappingFlags() {
        var drag = false
        var scroll = false
        var radial = false

        for actions in mappings.values {
            switch actions.press {
            case .drag: drag = true
            case .scroll: scroll = true
            case .radialMenu: radial = true
            default: break
            }
            if drag && scroll && radial { break }
        }

        dragMapped = drag
        scrollMapped = scroll
        radialMenuMapped = radial
    }

    /// Old profile format for migration
    private struct OldProfile: Codable {
        var mappings: [String: ButtonAction]
        var triggerThreshold: UInt8
    }
}

// MARK: - Joy-Con Button Mapping Profile

struct JoyConButtonMappingProfile: Codable, Sendable {
    private(set) var mappings: [String: ButtonActions]  // JoyConLogicalButton.rawValue -> actions
    private var actionsCache: [ButtonActions] = Array(repeating: ButtonActions(), count: JoyConLogicalButton.count)
    var holdThreshold: Double  // Seconds before hold action fires

    // Cached gyro-mode mapping flags
    private(set) var dragMapped: Bool = false
    private(set) var scrollMapped: Bool = false
    private(set) var radialMenuMapped: Bool = false

    static let userDefaultsKey = "JoyConButtonMappingProfile"
    static let browserBack = ButtonAction.keyPress(
        KeyCombo(keyCode: 33, modifiers: .maskCommand)
    )
    static let browserForward = ButtonAction.keyPress(
        KeyCombo(keyCode: 30, modifiers: .maskCommand)
    )

    private enum CodingKeys: String, CodingKey {
        case mappings
        case holdThreshold
    }

    init(mappings: [String: ButtonActions] = [:], holdThreshold: Double = 0.3) {
        self.mappings = mappings
        self.holdThreshold = holdThreshold
        rebuildActionsCache()
        recomputeMappingFlags()
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.mappings = try container.decodeIfPresent([String: ButtonActions].self, forKey: .mappings) ?? [:]
        self.holdThreshold = try container.decodeIfPresent(Double.self, forKey: .holdThreshold) ?? 0.3
        rebuildActionsCache()
        recomputeMappingFlags()
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(mappings, forKey: .mappings)
        try container.encode(holdThreshold, forKey: .holdThreshold)
    }

    static var `default`: Self {
        defaultProfile(for: .joyConRight)
    }

    func actions(for button: JoyConLogicalButton) -> ButtonActions {
        actionsCache[button.index]
    }

    mutating func setActions(_ actions: ButtonActions, for button: JoyConLogicalButton) {
        mappings[button.rawValue] = actions
        actionsCache[button.index] = actions
        recomputeMappingFlags()
    }

    mutating func setPressAction(_ action: ButtonAction, for button: JoyConLogicalButton) {
        var current = actions(for: button)
        current.press = action
        if action.isGyroMode {
            current.hold = .none
        }
        mappings[button.rawValue] = current
        actionsCache[button.index] = current
        recomputeMappingFlags()
    }

    mutating func setHoldAction(_ action: ButtonAction, for button: JoyConLogicalButton) {
        var current = actions(for: button)
        current.hold = action
        mappings[button.rawValue] = current
        actionsCache[button.index] = current
        recomputeMappingFlags()
    }

    var hasDragMapping: Bool { dragMapped }
    var hasScrollMapping: Bool { scrollMapped }
    var hasRadialMenuMapping: Bool { radialMenuMapped }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.userDefaultsKey)
        }
    }

    /// Save this profile for a specific controller side (left or right Joy-Con)
    func save(for profile: ControllerProfile) {
        guard profile.kind == .joyCon else { return }
        let key = "buttons.\(profile.persistenceKey)"
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: key)
        }
    }

    static func load() -> Self {
        if let data = UserDefaults.standard.data(forKey: userDefaultsKey),
           let profile = try? JSONDecoder().decode(Self.self, from: data) {
            return profile
        }
        return .default
    }

    /// Load profile for a specific controller side
    static func load(for profile: ControllerProfile) -> Self {
        guard profile.kind == .joyCon else { return .default }
        let key = "buttons.\(profile.persistenceKey)"

        // Try loading per-profile settings
        if let data = UserDefaults.standard.data(forKey: key),
           let loaded = try? JSONDecoder().decode(Self.self, from: data) {
            return loaded
        }

        // Fall back to global profile (for migration)
        return load()
    }

    /// Check if per-profile settings exist
    static func hasPerProfileSettings(for profile: ControllerProfile) -> Bool {
        guard profile.kind == .joyCon else { return false }
        let key = "buttons.\(profile.persistenceKey)"
        return UserDefaults.standard.data(forKey: key) != nil
    }

    /// Default profile for a specific controller side
    static func defaultProfile(for profile: ControllerProfile) -> Self {
        guard profile.kind == .joyCon else { return JoyConButtonMappingProfile() }

        var buttonProfile = JoyConButtonMappingProfile()
        if profile.isLeft {
            // Default mappings for Left Joy-Con
            // D-pad
            buttonProfile.mappings[JoyConLogicalButton.dpadUp.rawValue] = ButtonActions(press: .drag)
            buttonProfile.mappings[JoyConLogicalButton.dpadDown.rawValue] = ButtonActions(press: .radialMenu)
            buttonProfile.mappings[JoyConLogicalButton.dpadLeft.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 53)), hold: .keyPress(KeyCombo(keyCode: 8, modifiers: .maskControl)))  // Escape, hold: ^C
            buttonProfile.mappings[JoyConLogicalButton.dpadRight.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 49, modifiers: .maskControl)), hold: .keyPress(KeyCombo(keyCode: 36)))  // ^Space, hold: Return
            // Shoulder buttons
            buttonProfile.mappings[JoyConLogicalButton.l.rawValue] = ButtonActions(press: .mouseClick(.right))
            buttonProfile.mappings[JoyConLogicalButton.zl.rawValue] = ButtonActions(press: .mouseClick(.left))
            // System buttons
            buttonProfile.mappings[JoyConLogicalButton.minus.rawValue] = ButtonActions(
                press: .mouseClick(.middle)
            )
            buttonProfile.mappings[JoyConLogicalButton.capture.rawValue] = ButtonActions(
                press: .systemAction(.missionControl),
                hold: .systemAction(.playPause)
            )
            // Stick
            buttonProfile.mappings[JoyConLogicalButton.stickClick.rawValue] = ButtonActions(
                press: browserBack,
                hold: browserForward
            )
            // Side rail buttons
            buttonProfile.mappings[JoyConLogicalButton.sl.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 126)))  // Up arrow
            buttonProfile.mappings[JoyConLogicalButton.sr.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 125)))  // Down arrow
        } else {
            // Default mappings for Right Joy-Con
            // Face buttons
            buttonProfile.mappings[JoyConLogicalButton.a.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 53)), hold: .keyPress(KeyCombo(keyCode: 8, modifiers: .maskControl)))  // Escape, hold: ^C
            buttonProfile.mappings[JoyConLogicalButton.b.rawValue] = ButtonActions(press: .radialMenu)
            buttonProfile.mappings[JoyConLogicalButton.x.rawValue] = ButtonActions(press: .drag)
            buttonProfile.mappings[JoyConLogicalButton.y.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 49, modifiers: .maskControl)), hold: .keyPress(KeyCombo(keyCode: 36)))  // ^Space, hold: Return
            // Shoulder buttons
            buttonProfile.mappings[JoyConLogicalButton.r.rawValue] = ButtonActions(press: .mouseClick(.right))
            buttonProfile.mappings[JoyConLogicalButton.zr.rawValue] = ButtonActions(press: .mouseClick(.left))
            // System buttons
            buttonProfile.mappings[JoyConLogicalButton.plus.rawValue] = ButtonActions(
                press: .mouseClick(.middle)
            )
            buttonProfile.mappings[JoyConLogicalButton.home.rawValue] = ButtonActions(
                press: .systemAction(.missionControl),
                hold: .systemAction(.playPause)
            )
            // Stick
            buttonProfile.mappings[JoyConLogicalButton.stickClick.rawValue] = ButtonActions(
                press: browserBack,
                hold: browserForward
            )
            // Side rail buttons
            buttonProfile.mappings[JoyConLogicalButton.sl.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 125)))  // Down arrow
            buttonProfile.mappings[JoyConLogicalButton.sr.rawValue] = ButtonActions(press: .keyPress(KeyCombo(keyCode: 126)))  // Up arrow
        }
        buttonProfile.rebuildActionsCache()
        buttonProfile.recomputeMappingFlags()
        return buttonProfile
    }

    /// Upgrade only the exact legacy stock navigation actions. This preserves
    /// custom mappings while aligning existing Joy-Con profiles with the
    /// canonical one-handed layout used by Joy-Con 2.
    @discardableResult
    mutating func migrateLegacyNavigationDefaults(for profile: ControllerProfile) -> Bool {
        guard profile.kind == .joyCon else { return false }

        var changed = false
        let menuButton: JoyConLogicalButton = profile.isLeft ? .minus : .plus
        let systemButton: JoyConLogicalButton = profile.isLeft ? .capture : .home

        let legacyMenu = ButtonActions(press: .systemAction(.playPause))
        if actions(for: menuButton) == legacyMenu {
            setActions(ButtonActions(press: .mouseClick(.middle)), for: menuButton)
            changed = true
        }

        let legacyStick = ButtonActions(press: .mouseClick(.middle))
        if actions(for: .stickClick) == legacyStick {
            setActions(
                ButtonActions(press: Self.browserBack, hold: Self.browserForward),
                for: .stickClick
            )
            changed = true
        }

        let legacySystem = ButtonActions(press: .systemAction(.missionControl))
        if actions(for: systemButton) == legacySystem {
            setActions(
                ButtonActions(
                    press: .systemAction(.missionControl),
                    hold: .systemAction(.playPause)
                ),
                for: systemButton
            )
            changed = true
        }

        return changed
    }

    private mutating func rebuildActionsCache() {
        for button in JoyConLogicalButton.allCases {
            actionsCache[button.index] = mappings[button.rawValue] ?? ButtonActions()
        }
    }

    private mutating func recomputeMappingFlags() {
        var drag = false
        var scroll = false
        var radial = false

        for actions in mappings.values {
            switch actions.press {
            case .drag: drag = true
            case .scroll: scroll = true
            case .radialMenu: radial = true
            default: break
            }
            if drag && scroll && radial { break }
        }

        dragMapped = drag
        scrollMapped = scroll
        radialMenuMapped = radial
    }
}
