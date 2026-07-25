import Foundation
@preconcurrency import CoreGraphics
import QuartzCore

struct PixelScrollAccumulator {
    private var remainderX: CGFloat = 0
    private var remainderY: CGFloat = 0

    mutating func consume(dx: CGFloat, dy: CGFloat) -> (x: Int32, y: Int32) {
        remainderX += dx.isFinite ? dx : 0
        remainderY += dy.isFinite ? dy : 0

        let x = integralComponent(of: remainderX)
        let y = integralComponent(of: remainderY)
        remainderX -= CGFloat(x)
        remainderY -= CGFloat(y)
        return (x, y)
    }

    private func integralComponent(of value: CGFloat) -> Int32 {
        let integral = value.rounded(.towardZero)
        let clamped = min(CGFloat(Int32.max), max(CGFloat(Int32.min), integral))
        return Int32(clamped)
    }
}

/// Mouse controller using CGEvent with proper drag support. Movement, scrolling,
/// and button state are owned by InputEngine's serial queue; cursor visibility
/// bookkeeping is dispatched to the main queue.
final class MouseController: @unchecked Sendable {

    // MARK: - Properties

    /// Per-engine private state avoids inherited modifier flags and eliminates
    /// cross-instance sharing of Core Graphics' non-Sendable event source.
    private let eventSource: CGEventSource? = CGEventSource(stateID: .privateState)

    /// Track mouse buttons currently held so releasing one does not cancel another.
    private var heldMouseButtons: Set<MouseButton> = []

    /// Track click timing for double/triple click detection
    private var lastClickTime: Date = .distantPast
    private var lastClickPosition: CGPoint = .zero
    private var clickCount: Int64 = 0

    private var cachedDisplayBounds: [CGRect]
    private var cachedPosition: CGPoint
    private var lastResyncTime: TimeInterval = 0
    private var scrollAccumulator = PixelScrollAccumulator()

    private let doubleClickInterval: TimeInterval
    private let resyncInterval: TimeInterval

    // MARK: - Initialization

    init(doubleClickInterval: TimeInterval = 0.5, resyncInterval: TimeInterval = 5.0) {
        self.doubleClickInterval = doubleClickInterval
        self.resyncInterval = resyncInterval
        self.cachedDisplayBounds = MouseController.activeDisplayBounds()
        self.cachedPosition = MouseController.currentCursorPosition()
        self.lastResyncTime = CACurrentMediaTime()
    }

    /// Move the mouse by a relative amount, clamped to screen bounds
    func moveRelative(dx: CGFloat, dy: CGFloat) {
        resyncIfNeeded()

        // Update cached position in Quartz display coordinates (origin top-left)
        cachedPosition.x += dx
        cachedPosition.y += dy  // input dy is screen-down; Quartz Y increases downward
        cachedPosition = DisplayGeometry.closestPoint(cachedPosition, in: cachedDisplayBounds)
        let point = cachedPosition

        // Determine event type based on whether a mouse button is held
        let mouseType: CGEventType
        let mouseButton: CGMouseButton

        if let held = dragButton {
            // Button is held - send drag event for proper drag-and-drop support
            switch held {
            case .left:
                mouseType = .leftMouseDragged
                mouseButton = .left
            case .right:
                mouseType = .rightMouseDragged
                mouseButton = .right
            case .middle:
                mouseType = .otherMouseDragged
                mouseButton = .center
            }
        } else {
            // No button held - send regular move event
            mouseType = .mouseMoved
            mouseButton = .left
        }

        guard let event = CGEvent(
            mouseEventSource: eventSource,
            mouseType: mouseType,
            mouseCursorPosition: point,
            mouseButton: mouseButton
        ) else {
            return
        }

        event.post(tap: .cghidEventTap)
    }

    // MARK: - Scroll

    /// Scroll the content under the cursor
    /// - Parameters:
    ///   - dx: Horizontal scroll amount (positive = right)
    ///   - dy: Vertical scroll amount (positive = down)
    func scroll(dx: CGFloat, dy: CGFloat) {
        // Scale factor to make scroll feel natural
        // Gyro values are in screen pixels, scroll needs smaller values
        let scrollScale: CGFloat = 0.5

        let scroll = scrollAccumulator.consume(
            dx: dx * scrollScale,
            dy: -dy * scrollScale // Positive input dy scrolls content upward.
        )
        guard scroll.x != 0 || scroll.y != 0 else { return }

        guard let event = CGEvent(
            scrollWheelEvent2Source: eventSource,
            units: .pixel,
            wheelCount: 2,
            wheel1: scroll.y,  // Vertical scroll
            wheel2: scroll.x,  // Horizontal scroll
            wheel3: 0
        ) else {
            return
        }

        event.post(tap: .cghidEventTap)
    }

    // MARK: - Mouse Clicks

    /// Press mouse button down
    func mouseDown(button: MouseButton) {
        resyncIfNeeded(force: true)
        let currentPos = Self.currentCursorPosition()
        cachedPosition = currentPos
        let point = DisplayGeometry.closestPoint(currentPos, in: cachedDisplayBounds)

        let eventType: CGEventType
        let cgButton: CGMouseButton

        switch button {
        case .left:
            eventType = .leftMouseDown
            cgButton = .left
        case .right:
            eventType = .rightMouseDown
            cgButton = .right
        case .middle:
            eventType = .otherMouseDown
            cgButton = .center
        }

        guard let event = CGEvent(
            mouseEventSource: eventSource,
            mouseType: eventType,
            mouseCursorPosition: point,
            mouseButton: cgButton
        ) else {
            return
        }

        // Clear any inherited modifier flags - controller mouse clicks should be "clean"
        event.flags = []

        // Detect double/triple click based on timing and position
        let now = Date()
        let timeSinceLastClick = now.timeIntervalSince(lastClickTime)
        let maxClickDistance: CGFloat = 4.0  // pixels

        let distance = hypot(currentPos.x - lastClickPosition.x, currentPos.y - lastClickPosition.y)

        if timeSinceLastClick <= doubleClickInterval && distance <= maxClickDistance {
            // Rapid click near same position - increment click count (max 3 for triple-click)
            clickCount = min(clickCount + 1, 3)
        } else {
            // New click sequence
            clickCount = 1
        }

        lastClickTime = now
        lastClickPosition = currentPos

        // Set click state for proper recognition in all applications
        event.setIntegerValueField(.mouseEventClickState, value: clickCount)

        // Track this button as held (for drag events)
        heldMouseButtons.insert(button)

        event.post(tap: .cghidEventTap)
    }

    /// Release mouse button
    func mouseUp(button: MouseButton) {
        resyncIfNeeded(force: true)
        let currentPos = Self.currentCursorPosition()
        cachedPosition = currentPos
        let point = DisplayGeometry.closestPoint(currentPos, in: cachedDisplayBounds)
        heldMouseButtons.remove(button)

        let eventType: CGEventType
        let cgButton: CGMouseButton

        switch button {
        case .left:
            eventType = .leftMouseUp
            cgButton = .left
        case .right:
            eventType = .rightMouseUp
            cgButton = .right
        case .middle:
            eventType = .otherMouseUp
            cgButton = .center
        }

        guard let event = CGEvent(
            mouseEventSource: eventSource,
            mouseType: eventType,
            mouseCursorPosition: point,
            mouseButton: cgButton
        ) else {
            return
        }

        // Clear any inherited modifier flags
        event.flags = []

        // Set click state to match the mouseDown (for double/triple click recognition)
        event.setIntegerValueField(.mouseEventClickState, value: clickCount)

        event.post(tap: .cghidEventTap)
    }

    /// Perform a click (down + up)
    func click(button: MouseButton) {
        mouseDown(button: button)
        mouseUp(button: button)
    }

    // MARK: - Helpers

    private func resyncIfNeeded(force: Bool = false) {
        let now = CACurrentMediaTime()
        guard force || now - lastResyncTime >= resyncInterval else { return }
        cachedDisplayBounds = MouseController.activeDisplayBounds()
        cachedPosition = MouseController.currentCursorPosition()
        lastResyncTime = now
    }

    private static func currentCursorPosition() -> CGPoint {
        guard let event = CGEvent(source: nil) else { return .zero }
        return event.location
    }

    private var dragButton: MouseButton? {
        if heldMouseButtons.contains(.left) { return .left }
        if heldMouseButtons.contains(.right) { return .right }
        if heldMouseButtons.contains(.middle) { return .middle }
        return nil
    }

    private static func activeDisplayBounds() -> [CGRect] {
        let bounds = activeDisplayIDs().map(CGDisplayBounds)
        return bounds.isEmpty ? [CGRect(x: 0, y: 0, width: 1920, height: 1080)] : bounds
    }

    // MARK: - Cursor Visibility

    /// Track cursor hide/show balance
    private var cursorHideCount: Int = 0

    private static func activeDisplayIDs() -> [CGDirectDisplayID] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else {
            return [CGMainDisplayID()]
        }

        var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &displays, &count) == .success, count > 0 else {
            return [CGMainDisplayID()]
        }

        return Array(displays.prefix(Int(count)))
    }

    /// Hide the system cursor
    func hideCursor() {
        // Cursor visibility is effectively UI state; apply on main to avoid timing races with
        // SwiftUI/AppKit window operations (e.g. radial menu overlay).
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if self.cursorHideCount == 0 {
                for displayID in Self.activeDisplayIDs() {
                    CGDisplayHideCursor(displayID)
                }
            }
            self.cursorHideCount += 1
        }
    }

    /// Show the system cursor (must match previous hide calls)
    func showCursor() {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.cursorHideCount = max(self.cursorHideCount - 1, 0)
            if self.cursorHideCount == 0 {
                for displayID in Self.activeDisplayIDs() {
                    CGDisplayShowCursor(displayID)
                }
            }
        }
    }

    /// Force show cursor regardless of hide count (for cleanup)
    func forceShowCursor() {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            while self.cursorHideCount > 0 {
                for displayID in Self.activeDisplayIDs() {
                    CGDisplayShowCursor(displayID)
                }
                self.cursorHideCount -= 1
            }
        }
    }
}
