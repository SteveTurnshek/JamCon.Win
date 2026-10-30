using System.Buffers.Binary;

namespace JamCon.Core.Protocol;

/// <summary>Identity extracted from Nintendo manufacturer data in a BLE advertisement.</summary>
/// <param name="PayloadIncludedCompanyId">
/// True when the vendor/product IDs were found at the CoreBluetooth offsets, meaning the payload
/// still carried its leading two-byte company identifier. See <see cref="JoyCon2Advertisement"/>.
/// </param>
public readonly record struct JoyCon2Identity(
    ushort VendorId,
    ushort ProductId,
    JoyCon2Handedness Handedness,
    bool PayloadIncludedCompanyId);

/// <summary>
/// Extracts Joy-Con 2 identity from Nintendo BLE manufacturer data.
/// </summary>
/// <remarks>
/// <para>
/// There is a platform difference here worth stating plainly, because it is the one place the
/// validated Swift offsets cannot be copied verbatim.
/// </para>
/// <para>
/// Upstream reads the vendor ID at offset 5 and the product ID at offset 7, because
/// CoreBluetooth's manufacturer payload <i>includes</i> the leading two-byte company identifier.
/// WinRT splits those apart: <c>BluetoothLEManufacturerData.CompanyId</c> holds the company ID and
/// <c>.Data</c> holds only the remainder — so on Windows the same fields should sit at offsets
/// 3 and 5.
/// </para>
/// <para>
/// That shift was reasoned first and then confirmed on hardware: the Joy-Con 2 advertises with
/// company ID 0x0553 and carries the vendor and product IDs at offsets 3 and 5 of the remaining
/// payload (docs/Windows-Port.md). The decoder still falls back to the CoreBluetooth offsets and
/// reports which matched via <see cref="JoyCon2Identity.PayloadIncludedCompanyId"/>; that fallback
/// is now known to be dead on Windows and can be removed.
/// </para>
/// </remarks>
public static class JoyCon2Advertisement
{
    private const int WindowsVendorOffset = 3;
    private const int CoreBluetoothVendorOffset = 5;

    /// <summary>
    /// Attempts to identify a Joy-Con 2 from manufacturer payload bytes. Returns false unless the
    /// payload yields the Nintendo vendor ID together with a known Joy-Con 2 product ID.
    /// </summary>
    public static bool TryIdentify(ReadOnlySpan<byte> payload, out JoyCon2Identity identity)
    {
        if (TryAt(payload, WindowsVendorOffset, includedCompanyId: false, out identity)) return true;
        if (TryAt(payload, CoreBluetoothVendorOffset, includedCompanyId: true, out identity)) return true;
        identity = default;
        return false;
    }

    private static bool TryAt(
        ReadOnlySpan<byte> payload,
        int vendorOffset,
        bool includedCompanyId,
        out JoyCon2Identity identity)
    {
        identity = default;
        if (payload.Length < vendorOffset + 4) return false;

        var vendorId = BinaryPrimitives.ReadUInt16LittleEndian(payload[vendorOffset..]);
        if (vendorId != JoyCon2Ids.NintendoVendorId) return false;

        var productId = BinaryPrimitives.ReadUInt16LittleEndian(payload[(vendorOffset + 2)..]);
        if (!TryGetHandedness(productId, out var handedness)) return false;

        identity = new JoyCon2Identity(vendorId, productId, handedness, includedCompanyId);
        return true;
    }

    public static bool TryGetHandedness(ushort productId, out JoyCon2Handedness handedness)
    {
        switch (productId)
        {
            case JoyCon2Ids.LeftJoyCon2ProductId:
                handedness = JoyCon2Handedness.Left;
                return true;
            case JoyCon2Ids.RightJoyCon2ProductId:
                handedness = JoyCon2Handedness.Right;
                return true;
            default:
                handedness = default;
                return false;
        }
    }

    public static string DisplayName(JoyCon2Handedness handedness) =>
        handedness == JoyCon2Handedness.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)";
}
