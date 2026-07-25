using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// The advertisement decoder is the one place the validated Swift offsets cannot be copied
/// verbatim, because CoreBluetooth includes the company ID in the manufacturer payload and WinRT
/// does not. Both interpretations are tested so whichever one hardware confirms is already covered.
/// </summary>
public class JoyCon2AdvertisementTests
{
    /// <summary>WinRT shape: company ID stripped, so VID sits at offset 3 and PID at offset 5.</summary>
    private static byte[] WindowsPayload(ushort productId) =>
    [
        0x00, 0x00, 0x00,
        0x7E, 0x05,
        (byte)(productId & 0xFF), (byte)(productId >> 8),
    ];

    /// <summary>CoreBluetooth shape: leading company ID, so VID sits at offset 5 and PID at offset 7.</summary>
    private static byte[] CoreBluetoothPayload(ushort productId) =>
    [
        0x7E, 0x05,
        0x00, 0x00, 0x00,
        0x7E, 0x05,
        (byte)(productId & 0xFF), (byte)(productId >> 8),
    ];

    [Fact]
    public void IdentifiesRightJoyConFromWindowsOffsets()
    {
        Assert.True(JoyCon2Advertisement.TryIdentify(WindowsPayload(0x2066), out var identity));
        Assert.Equal(0x057E, identity.VendorId);
        Assert.Equal(0x2066, identity.ProductId);
        Assert.Equal(JoyCon2Handedness.Right, identity.Handedness);
        Assert.False(identity.PayloadIncludedCompanyId);
    }

    [Fact]
    public void IdentifiesLeftJoyConFromWindowsOffsets()
    {
        Assert.True(JoyCon2Advertisement.TryIdentify(WindowsPayload(0x2067), out var identity));
        Assert.Equal(JoyCon2Handedness.Left, identity.Handedness);
        Assert.False(identity.PayloadIncludedCompanyId);
    }

    [Fact]
    public void FallsBackToCoreBluetoothOffsetsAndFlagsThem()
    {
        Assert.True(JoyCon2Advertisement.TryIdentify(CoreBluetoothPayload(0x2066), out var identity));
        Assert.Equal(0x2066, identity.ProductId);
        Assert.True(identity.PayloadIncludedCompanyId);
    }

    [Fact]
    public void RejectsNonNintendoVendor()
    {
        byte[] payload = [0x00, 0x00, 0x00, 0x4C, 0x00, 0x66, 0x20];
        Assert.False(JoyCon2Advertisement.TryIdentify(payload, out _));
    }

    [Fact]
    public void RejectsNintendoDevicesThatAreNotJoyCon2()
    {
        // Nintendo vendor, but an original Joy-Con product ID rather than a Joy-Con 2.
        Assert.False(JoyCon2Advertisement.TryIdentify(WindowsPayload(0x2006), out _));
    }

    [Fact]
    public void RejectsPayloadsTooShortToContainIdentity()
    {
        Assert.False(JoyCon2Advertisement.TryIdentify([], out _));
        Assert.False(JoyCon2Advertisement.TryIdentify([0x7E, 0x05], out _));
    }

    [Theory]
    [InlineData((ushort)0x2066, JoyCon2Handedness.Right)]
    [InlineData((ushort)0x2067, JoyCon2Handedness.Left)]
    public void MapsProductIdToHandedness(ushort productId, JoyCon2Handedness expected)
    {
        Assert.True(JoyCon2Advertisement.TryGetHandedness(productId, out var handedness));
        Assert.Equal(expected, handedness);
    }

    [Fact]
    public void DisplayNameDistinguishesHandedness()
    {
        Assert.Equal("Joy-Con 2 (R)", JoyCon2Advertisement.DisplayName(JoyCon2Handedness.Right));
        Assert.Equal("Joy-Con 2 (L)", JoyCon2Advertisement.DisplayName(JoyCon2Handedness.Left));
    }
}
