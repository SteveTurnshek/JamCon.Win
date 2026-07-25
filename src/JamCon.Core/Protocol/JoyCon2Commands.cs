namespace JamCon.Core.Protocol;

/// <summary>
/// Command payloads for the Joy-Con 2 BLE command protocol, transcribed from the
/// validated Swift implementation (reference/swift/JoyCon2BLEProtocol.swift).
///
/// Do not add commands here speculatively. Upstream deliberately enables only the
/// features it consumes and leaves the optical sensor, magnetometer, current
/// telemetry and unknown feature bits disabled.
/// </summary>
public static class JoyCon2Commands
{
    /// <summary>
    /// Selects the feature set: buttons, analog stick and IMU only.
    /// Sent before <see cref="EnableFeatures"/> — both are required.
    /// </summary>
    public static ReadOnlySpan<byte> SetFeatureMask =>
    [
        0x0C, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00,
        JoyCon2Ids.FeatureMaskButtonsStickImu, 0x00, 0x00, 0x00,
    ];

    /// <summary>Activates the feature set selected by <see cref="SetFeatureMask"/>.</summary>
    public static ReadOnlySpan<byte> EnableFeatures =>
    [
        0x0C, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00,
        JoyCon2Ids.FeatureMaskButtonsStickImu, 0x00, 0x00, 0x00,
    ];

    /// <summary>
    /// Selects player LED one and stops the pairing-light chase. Joy-Con 2 uses an
    /// eight-byte LED payload; the low nibble selects the four player LEDs.
    /// </summary>
    public static ReadOnlySpan<byte> SetPlayerOneLed =>
    [
        0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>Builds a factory-memory read command for <paramref name="address"/>.</summary>
    public static byte[] ReadMemory(uint address, byte length) =>
    [
        0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00,
        length, 0x7E, 0x00, 0x00,
        (byte)(address & 0xFF),
        (byte)((address >> 8) & 0xFF),
        (byte)((address >> 16) & 0xFF),
        (byte)((address >> 24) & 0xFF),
    ];

    /// <summary>Reads the primary stick's nine-byte factory calibration record.</summary>
    public static byte[] ReadPrimaryStickCalibration() =>
        ReadMemory(JoyCon2Ids.StickCalibrationAddress, JoyCon2Ids.StickCalibrationLength);

    /// <summary>Report-rate descriptor payload, little-endian.</summary>
    public static byte[] ReportRateDescriptorValue(JoyCon2ReportRate rate) =>
        [(byte)((ushort)rate & 0xFF), (byte)((ushort)rate >> 8)];

    /// <summary>
    /// Evaluates a command response notification. Responses echo the command byte
    /// and subcommand; byte one carries controller-level status.
    /// Returns null when the response does not correspond to the command at all.
    /// </summary>
    public static bool? ResponseSucceeded(ReadOnlySpan<byte> response, ReadOnlySpan<byte> command)
    {
        if (response.Length < 4 || command.Length < 4) return null;
        if (response[0] != command[0] || response[3] != command[3]) return null;
        return response[1] == 0x01;
    }
}

/// <summary>
/// Report rates the controller accepts in its report-rate descriptor.
/// </summary>
/// <remarks>
/// Requesting 133 Hz is what makes the common decoded stream arrive at ~66 Hz on the
/// validated macOS link: the descriptor value is a request, not a delivery guarantee,
/// and one common report arrives per BLE connection event. Whether Windows negotiates
/// the same 15 ms interval is an open Phase 1 question.
/// </remarks>
public enum JoyCon2ReportRate : ushort
{
    Hz66 = 0x0042,
    Hz133 = 0x0085,
}
