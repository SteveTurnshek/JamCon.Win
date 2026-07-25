using JamCon.Core.Gyro;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Guards the constants that PLAN §4 Phase 2 flags as must-not-drift, plus the GATT
/// identity values. These are the numbers where a typo yields plausible-but-wrong
/// behavior instead of an obvious failure.
/// </summary>
public class ConstantsDriftTests
{
    [Fact]
    public void GyroScaleMatchesValidatedSpec()
    {
        Assert.Equal(0.0608506463, GyroConstants.DegreesPerSecondPerCount, precision: 12);
    }

    [Fact]
    public void GyroScaleAgreesWithTheDerivingFormula()
    {
        // 34.8 rad/s full scale at the IMU's ~±2000 deg/s range, over Int16.MaxValue counts.
        // Guards the literal against a transcription slip, independently of the spec text.
        var derived = 34.8 * 180.0 / (Math.PI * short.MaxValue);
        Assert.Equal(GyroConstants.DegreesPerSecondPerCount, derived, precision: 9);
    }

    [Fact]
    public void StickGeometryMatchesValidatedSpec()
    {
        Assert.Equal(150, GyroConstants.NeutralCorrectionRadiusCounts);
        Assert.Equal(2048, GyroConstants.NominalStickCenter);
    }

    [Fact]
    public void DeviceIdentityMatchesValidatedSpec()
    {
        Assert.Equal(0x057E, JoyCon2Ids.NintendoVendorId);
        Assert.Equal(0x2066, JoyCon2Ids.RightJoyCon2ProductId);
        Assert.Equal(0x2067, JoyCon2Ids.LeftJoyCon2ProductId);
    }

    [Fact]
    public void GattUuidsMatchValidatedSpec()
    {
        Assert.Equal(new Guid("AB7DE9BE-89FE-49AD-828F-118F09DF7FD0"), JoyCon2Ids.Service);
        Assert.Equal(new Guid("AB7DE9BE-89FE-49AD-828F-118F09DF7FD2"), JoyCon2Ids.CommonInput);
        Assert.Equal(new Guid("679D5510-5A24-4DEE-9557-95DF80486ECB"), JoyCon2Ids.ReportRateDescriptor);
        Assert.Equal(new Guid("649D4AC9-8EB7-4E6C-AF44-1EA54FE5F005"), JoyCon2Ids.CommandWrite);
        Assert.Equal(new Guid("C765A961-D9D8-4D36-A20A-5315B111836A"), JoyCon2Ids.CommandResponse);
    }

    [Fact]
    public void InitializationSequenceValuesMatchValidatedSpec()
    {
        Assert.Equal(0x07, JoyCon2Ids.FeatureMaskButtonsStickImu);
        Assert.Equal(0x0085, JoyCon2Ids.ReportRateValue);
        Assert.Equal(0x000130A8u, JoyCon2Ids.StickCalibrationAddress);
        Assert.Equal(9, JoyCon2Ids.StickCalibrationLength);
        Assert.Equal(0x05, JoyCon2Ids.CommonInputReportId);
    }
}
