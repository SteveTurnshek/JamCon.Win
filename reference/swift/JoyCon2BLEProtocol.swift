import Foundation

enum JoyCon2BLEReportRate: UInt16, CaseIterable, Sendable {
    case hz66 = 0x0042
    case hz133 = 0x0085

    var hertz: Double { Double(rawValue) }

    var descriptorValue: Data {
        Data([UInt8(rawValue & 0xFF), UInt8(rawValue >> 8)])
    }

    /// Opaque values accepted by CoreBluetooth's initial-connect overrides.
    /// Bluetooth profile logs show that value 6 negotiates a 15 ms link on
    /// macOS; these are not HCI interval units despite the option-key names.
    var coreBluetoothIntervalOverride: UInt16 {
        switch self {
        case .hz133: 6
        case .hz66: 12
        }
    }
}

/// Ordered connection policies for production reconnects. The first policy is
/// the measured high-performance path. The later policies keep the controller
/// usable if a future macOS Bluetooth stack rejects an explicit interval.
enum JoyCon2BLEConnectionPolicy: Int, CaseIterable, Sendable {
    case highPerformance
    case standardLowLatency
    case compatible

    var coreBluetoothIntervalOverride: UInt16? {
        switch self {
        case .highPerformance:
            JoyCon2BLEReportRate.hz133.coreBluetoothIntervalOverride
        case .standardLowLatency:
            JoyCon2BLEReportRate.hz66.coreBluetoothIntervalOverride
        case .compatible:
            nil
        }
    }

    var logDescription: String {
        switch self {
        case .highPerformance:
            "high-performance"
        case .standardLowLatency:
            "standard low-latency fallback"
        case .compatible:
            "system-compatible fallback"
        }
    }
}

/// Estimates the cadence actually delivered by CoreBluetooth. A requested
/// controller rate is only a preference: the negotiated BLE connection can
/// cap it, and a missed notification can double one interval. A short median
/// rejects isolated drops while adapting quickly if the requested mode changes.
struct JoyCon2BLECadenceEstimator: Sendable {
    private let fallbackRate: Double
    private var lastTimestamp: TimeInterval?
    private var recentIntervals: [TimeInterval] = []
    private let intervalCapacity = 9

    init(fallbackRate: Double = JoyCon2BLEReportRate.hz133.hertz) {
        self.fallbackRate = fallbackRate
    }

    var estimatedRate: Double {
        guard !recentIntervals.isEmpty else { return fallbackRate }
        let sorted = recentIntervals.sorted()
        return 1.0 / sorted[sorted.count / 2]
    }

    var hasStableEstimate: Bool {
        recentIntervals.count == intervalCapacity
    }

    mutating func record(timestamp: TimeInterval) -> Double {
        defer {
            if timestamp.isFinite,
               lastTimestamp == nil || timestamp > (lastTimestamp ?? timestamp) {
                lastTimestamp = timestamp
            }
        }
        guard timestamp.isFinite,
              let lastTimestamp,
              timestamp > lastTimestamp else { return estimatedRate }

        let interval = timestamp - lastTimestamp
        // Accept every plausible Joy-Con 2 transport cadence while rejecting
        // notification bursts and connection stalls.
        guard interval >= 1.0 / 240.0, interval <= 1.0 / 15.0 else {
            return estimatedRate
        }
        recentIntervals.append(interval)
        if recentIntervals.count > intervalCapacity {
            recentIntervals.removeFirst(recentIntervals.count - intervalCapacity)
        }
        return estimatedRate
    }
}

enum JoyCon2BLEProtocol {
    static let nintendoVendorID: UInt16 = 0x057E
    static let rightProductID: UInt16 = 0x2066
    static let leftProductID: UInt16 = 0x2067
    static let inputReportID: UInt32 = 0x05
    static let minimumInputLength = 0x3C
    /// Requesting 133 Hz is required to make the common decoded stream deliver
    /// at 66 Hz on the tested Joy-Con 2/macOS combination. Bluetooth profile
    /// logs confirm a 15 ms negotiated link and one common report per event.
    static let preferredReportRate = JoyCon2BLEReportRate.hz133
    static let productionReportRate = JoyCon2BLEReportRate.hz66
    static let fallbackReportRate = JoyCon2BLEReportRate.hz66
    /// ICM-42670-P conversion at the controller's approximately ±2,000
    /// degrees/second range. SDL and everything-imu independently use the
    /// equivalent 34.8 radians/second full-scale coefficient. A controlled
    /// 360-degree rotation validated this within normal hand-test error.
    static let gyroScale = 34.8 * 180.0 / (Double.pi * Double(Int16.max))

    static let serviceUUID = "AB7DE9BE-89FE-49AD-828F-118F09DF7FD0"
    static let inputUUID = "AB7DE9BE-89FE-49AD-828F-118F09DF7FD2"
    static let reportRateDescriptorUUID = "679D5510-5A24-4DEE-9557-95DF80486ECB"
    static let commandUUID = "649D4AC9-8EB7-4E6C-AF44-1EA54FE5F005"
    static let responseUUID = "C765A961-D9D8-4D36-A20A-5315B111836A"

    // Buttons + analog stick + IMU. Do not enable the optical sensor, current
    // telemetry, magnetometer, or unknown feature bits on JamCon's input path.
    static let featureMask: UInt8 = 0x07
    static let setFeatureMask = Data([
        0x0C, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00,
        featureMask, 0x00, 0x00, 0x00,
    ])
    static let enableFeatures = Data([
        0x0C, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00,
        featureMask, 0x00, 0x00, 0x00,
    ])

    /// Every standalone Joy-Con 2 stores its primary physical stick
    /// calibration in this factory-memory record. In the common input report,
    /// a right Joy-Con still places that primary stick in the right-stick slot.
    static let primaryStickFactoryCalibrationAddress: UInt32 = 0x0001_30A8
    static let stickCalibrationLength: UInt8 = 9

    /// Select player one and stop the pairing-light chase. Joy-Con 2 uses an
    /// eight-byte LED payload; the low nibble selects the four player LEDs.
    static let setPlayerOneLED = Data([
        0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ])

    /// Responses echo the command byte and subcommand. Byte one is the
    /// controller-level status (0x01 success, 0x00 failure).
    static func commandResponseSucceeded(_ response: Data, for command: Data) -> Bool? {
        guard response.count >= 4,
              command.count >= 4,
              response[response.startIndex] == command[command.startIndex],
              response[response.startIndex + 3] == command[command.startIndex + 3] else {
            return nil
        }
        return response[response.startIndex + 1] == 0x01
    }

    static func handedness(productID: UInt16) -> ControllerHandedness? {
        switch productID {
        case leftProductID: .left
        case rightProductID: .right
        default: nil
        }
    }

    static func userFacingDeviceName(
        peripheralName: String?,
        advertisedName: String?,
        handedness: ControllerHandedness
    ) -> String {
        for candidate in [peripheralName, advertisedName] {
            let trimmed = candidate?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let normalized = trimmed.lowercased()
            guard !trimmed.isEmpty,
                  normalized != "devicename",
                  normalized != "device name",
                  normalized != "unknown" else { continue }
            return trimmed
        }
        return handedness == .left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)"
    }

    static func readMemoryCommand(address: UInt32, length: UInt8) -> Data {
        Data([
            0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00,
            length, 0x7E, 0x00, 0x00,
            UInt8(address & 0xFF),
            UInt8((address >> 8) & 0xFF),
            UInt8((address >> 16) & 0xFF),
            UInt8((address >> 24) & 0xFF),
        ])
    }

    static var readPrimaryStickFactoryCalibration: Data {
        readMemoryCommand(
            address: primaryStickFactoryCalibrationAddress,
            length: stickCalibrationLength
        )
    }

    static func decodeStickCalibrationResponse(
        _ response: Data,
        expectedAddress: UInt32 = primaryStickFactoryCalibrationAddress
    ) -> InputDeviceAnalogStickCalibration? {
        guard response.count >= 16 + Int(stickCalibrationLength),
              response[8] >= stickCalibrationLength,
              uint32LE(response, at: 12) == expectedAddress else { return nil }

        let data = Array(response[16..<(16 + Int(stickCalibrationLength))])
        func unpack(_ offset: Int) -> (UInt16, UInt16) {
            let byte0 = UInt16(data[offset])
            let byte1 = UInt16(data[offset + 1])
            let byte2 = UInt16(data[offset + 2])
            return (
                byte0 | ((byte1 & 0x0F) << 8),
                (byte1 >> 4) | (byte2 << 4)
            )
        }

        let center = unpack(0)
        let positive = unpack(3)
        let negative = unpack(6)
        let calibration = InputDeviceAnalogStickCalibration(
            centerX: center.0,
            centerY: center.1,
            positiveRangeX: positive.0,
            positiveRangeY: positive.1,
            negativeRangeX: negative.0,
            negativeRangeY: negative.1
        )
        return isPlausible(calibration) ? calibration : nil
    }

    /// CoreBluetooth's manufacturer payload includes Nintendo's company data.
    /// The embedded USB VID/PID are little-endian at offsets 5 and 7.
    static func decodeAdvertisement(_ data: Data) -> (vendorID: UInt16, productID: UInt16)? {
        guard data.count >= 9 else { return nil }
        return (uint16LE(data, at: 5), uint16LE(data, at: 7))
    }

    static func decodeMotion(_ bytes: [UInt8]) -> IMUSample? {
        guard bytes.count >= minimumInputLength else { return nil }
        let sample = IMUSample(
            accelX: int16LE(bytes, at: 0x30),
            accelY: int16LE(bytes, at: 0x32),
            accelZ: int16LE(bytes, at: 0x34),
            gyroX: int16LE(bytes, at: 0x36),
            gyroY: int16LE(bytes, at: 0x38),
            gyroZ: int16LE(bytes, at: 0x3A)
        )
        let isZeroFilled = sample.accelX == 0
            && sample.accelY == 0
            && sample.accelZ == 0
            && sample.gyroX == 0
            && sample.gyroY == 0
            && sample.gyroZ == 0
        return isZeroFilled ? nil : sample
    }

    /// Produces the established Joy-Con family control layout consumed by
    /// JoyConButtonMapping. The original raw BLE bytes remain on the frame for
    /// diagnostics; this conversion is application policy in InputEngine.
    static func controlBytes(from bytes: [UInt8]) -> [UInt8]? {
        guard bytes.count >= 16 else { return nil }
        var controls = [UInt8](repeating: 0, count: JoyConHIDProtocol.reportLength)
        controls[JoyConHIDProtocol.Offset.reportID] = UInt8(JoyConHIDProtocol.inputReportID)

        // Shared report 0x05 exposes a four-byte button field at 0x04. Its
        // first three bytes retain Nintendo's right/system/left bit grouping.
        controls[3] = bytes[4]
        controls[4] = bytes[5]
        controls[5] = bytes[6]

        controls.replaceSubrange(6...8, with: bytes[10...12])
        controls.replaceSubrange(9...11, with: bytes[13...15])
        return controls
    }

    private static func uint16LE(_ data: Data, at offset: Int) -> UInt16 {
        let index = data.startIndex.advanced(by: offset)
        return UInt16(data[index]) | (UInt16(data[index + 1]) << 8)
    }

    private static func uint32LE(_ data: Data, at offset: Int) -> UInt32 {
        let index = data.startIndex.advanced(by: offset)
        return UInt32(data[index])
            | (UInt32(data[index + 1]) << 8)
            | (UInt32(data[index + 2]) << 16)
            | (UInt32(data[index + 3]) << 24)
    }

    private static func isPlausible(_ calibration: InputDeviceAnalogStickCalibration) -> Bool {
        let centers = [calibration.centerX, calibration.centerY]
        let ranges = [
            calibration.positiveRangeX,
            calibration.positiveRangeY,
            calibration.negativeRangeX,
            calibration.negativeRangeY,
        ]
        guard centers.allSatisfy({ $0 > 0 && $0 < 4095 }),
              ranges.allSatisfy({ $0 >= 200 && $0 <= 4095 }) else { return false }

        return Int(calibration.centerX) + Int(calibration.positiveRangeX) <= 4095
            && Int(calibration.centerY) + Int(calibration.positiveRangeY) <= 4095
            && Int(calibration.centerX) - Int(calibration.negativeRangeX) >= 0
            && Int(calibration.centerY) - Int(calibration.negativeRangeY) >= 0
    }

    private static func int16LE(_ bytes: [UInt8], at offset: Int) -> Int16 {
        Int16(bitPattern: UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8))
    }
}
