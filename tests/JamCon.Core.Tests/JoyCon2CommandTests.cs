using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Byte-for-byte checks against the validated Swift command payloads in
/// reference/swift/JoyCon2BLEProtocol.swift. A wrong byte here produces a controller that
/// either ignores us or enables features we deliberately leave off, so these are asserted
/// literally rather than derived.
/// </summary>
public class JoyCon2CommandTests
{
    [Fact]
    public void SetFeatureMaskMatchesValidatedPayload()
    {
        byte[] expected = [0x0C, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00];
        Assert.Equal(expected, JoyCon2Commands.SetFeatureMask.ToArray());
    }

    [Fact]
    public void EnableFeaturesMatchesValidatedPayload()
    {
        byte[] expected = [0x0C, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00];
        Assert.Equal(expected, JoyCon2Commands.EnableFeatures.ToArray());
    }

    [Fact]
    public void SetFeatureMaskAndEnableFeaturesDifferOnlyInSubcommandByte()
    {
        var set = JoyCon2Commands.SetFeatureMask.ToArray();
        var enable = JoyCon2Commands.EnableFeatures.ToArray();

        Assert.Equal(0x02, set[3]);
        Assert.Equal(0x04, enable[3]);

        for (var i = 0; i < set.Length; i++)
        {
            if (i == 3) continue;
            Assert.Equal(set[i], enable[i]);
        }
    }

    [Fact]
    public void SetPlayerOneLedMatchesValidatedPayload()
    {
        byte[] expected =
        [
            0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(expected, JoyCon2Commands.SetPlayerOneLed.ToArray());
    }

    [Fact]
    public void ReadPrimaryStickCalibrationEncodesAddressLittleEndian()
    {
        byte[] expected =
        [
            0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00,
            0x09, 0x7E, 0x00, 0x00,
            0xA8, 0x30, 0x01, 0x00,
        ];
        Assert.Equal(expected, JoyCon2Commands.ReadPrimaryStickCalibration());
    }

    [Fact]
    public void ReportRateDescriptorValueIsLittleEndian()
    {
        Assert.Equal([0x85, 0x00], JoyCon2Commands.ReportRateDescriptorValue(JoyCon2ReportRate.Hz133));
        Assert.Equal([0x42, 0x00], JoyCon2Commands.ReportRateDescriptorValue(JoyCon2ReportRate.Hz66));
    }

    [Fact]
    public void ResponseSucceededRequiresEchoedCommandAndSubcommand()
    {
        var command = JoyCon2Commands.SetFeatureMask.ToArray();

        // Echoes byte 0 and byte 3, status 0x01.
        byte[] success = [0x0C, 0x01, 0x00, 0x02];
        Assert.True(JoyCon2Commands.ResponseSucceeded(success, command));

        // Echoes correctly but reports failure.
        byte[] failure = [0x0C, 0x00, 0x00, 0x02];
        Assert.False(JoyCon2Commands.ResponseSucceeded(failure, command));
    }

    [Fact]
    public void ResponseSucceededReturnsNullWhenResponseIsForAnotherCommand()
    {
        var command = JoyCon2Commands.SetFeatureMask.ToArray();

        byte[] wrongCommandByte = [0x09, 0x01, 0x00, 0x02];
        Assert.Null(JoyCon2Commands.ResponseSucceeded(wrongCommandByte, command));

        byte[] wrongSubcommand = [0x0C, 0x01, 0x00, 0x07];
        Assert.Null(JoyCon2Commands.ResponseSucceeded(wrongSubcommand, command));

        byte[] tooShort = [0x0C, 0x01, 0x00];
        Assert.Null(JoyCon2Commands.ResponseSucceeded(tooShort, command));
    }
}
