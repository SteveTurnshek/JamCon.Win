namespace JamCon.Core.Protocol;

/// <summary>
/// Identity and GATT constants for the Switch 2 Joy-Con BLE profile.
/// Values are transcribed from docs/JoyCon2-Bluetooth.md, which records the
/// hardware-validated macOS implementation. Nothing here is inferred.
/// </summary>
public static class JoyCon2Ids
{
    /// <summary>Nintendo USB vendor ID, as embedded in BLE manufacturer data.</summary>
    public const ushort NintendoVendorId = 0x057E;

    public const ushort RightJoyCon2ProductId = 0x2066;
    public const ushort LeftJoyCon2ProductId = 0x2067;

    public static readonly Guid Service = new("AB7DE9BE-89FE-49AD-828F-118F09DF7FD0");
    public static readonly Guid CommonInput = new("AB7DE9BE-89FE-49AD-828F-118F09DF7FD2");
    public static readonly Guid ReportRateDescriptor = new("679D5510-5A24-4DEE-9557-95DF80486ECB");
    public static readonly Guid CommandWrite = new("649D4AC9-8EB7-4E6C-AF44-1EA54FE5F005");
    public static readonly Guid CommandResponse = new("C765A961-D9D8-4D36-A20A-5315B111836A");

    /// <summary>Enables buttons, analog stick and IMU only.</summary>
    public const byte FeatureMaskButtonsStickImu = 0x07;

    /// <summary>Report-rate descriptor value written during initialization.</summary>
    public const ushort ReportRateValue = 0x0085;

    /// <summary>SPI address of the primary stick's nine-byte factory calibration.</summary>
    public const uint StickCalibrationAddress = 0x000130A8;

    public const int StickCalibrationLength = 9;

    /// <summary>Common input report ID carrying buttons, stick and IMU.</summary>
    public const byte CommonInputReportId = 0x05;
}
