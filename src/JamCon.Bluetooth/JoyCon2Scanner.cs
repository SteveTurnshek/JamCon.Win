using System.Collections.Concurrent;
using JamCon.Core.Protocol;
using Windows.Devices.Bluetooth.Advertisement;

namespace JamCon.Bluetooth;

/// <summary>A Joy-Con 2 seen in a BLE advertisement.</summary>
public sealed record JoyCon2Discovery(
    ulong BluetoothAddress,
    JoyCon2Identity Identity,
    short SignalStrengthDbm,
    byte[] RawManufacturerData)
{
    public string DisplayName => JoyCon2Advertisement.DisplayName(Identity.Handedness);

    /// <summary>The 48-bit address in conventional MAC notation, most significant byte first.</summary>
    public string AddressString => string.Join(
        ':',
        Enumerable.Range(0, 6).Select(i => ((byte)(BluetoothAddress >> ((5 - i) * 8))).ToString("X2")));
}

/// <summary>
/// Watches BLE advertisements for Joy-Con 2 controllers.
/// </summary>
/// <remarks>
/// Uses <see cref="BluetoothLEScanningMode.Active"/> so scan responses are collected too; Nintendo
/// manufacturer data is not guaranteed to arrive in the passive advertisement alone.
/// </remarks>
public sealed class JoyCon2Scanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly ConcurrentDictionary<ulong, JoyCon2Discovery> _seen = new();

    /// <summary>Raised once per newly discovered address.</summary>
    public event Action<JoyCon2Discovery>? Discovered;

    /// <summary>
    /// Raised for manufacturer data that came from Nintendo's company ID but did not parse as a
    /// known Joy-Con 2. Surfaced rather than swallowed because it is the signal that the
    /// advertisement offsets need revisiting on Windows.
    /// </summary>
    public event Action<ulong, byte[]>? UnrecognizedNintendoAdvertisement;

    /// <summary>
    /// Full detail for every distinct advertisement from <see cref="WatchAddress"/>, from a Nintendo
    /// company ID, or from a device with a Joy-Con-like name. Deduplicated by payload.
    /// </summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Any advertisement at all from <see cref="WatchAddress"/>, including directed ones that carry
    /// no manufacturer data and so never parse as a Joy-Con 2. The flag says whether it carried
    /// Nintendo manufacturer data, which a controller in SYNC mode sends.
    /// </summary>
    public event Action<BluetoothLEAdvertisementType, bool>? WatchedAddressSeen;

    /// <summary>An address to dump in full whenever it advertises, e.g. a known controller.</summary>
    public ulong? WatchAddress { get; init; }

    /// <summary>
    /// Nintendo's Bluetooth SIG company identifier. Not the same number as the USB vendor ID
    /// (0x057E) that the payload carries — the first scanner revision checked the USB ID here and
    /// so could never have flagged a Nintendo advertisement. 0x0553 was confirmed on hardware on
    /// 2026-10-30 (docs/Windows-Port.md); also matching 0x057E is harmless.
    /// </summary>
    private const ushort NintendoBluetoothCompanyId = 0x0553;

    private static bool IsNintendoCompanyId(ushort companyId) =>
        companyId == NintendoBluetoothCompanyId || companyId == JoyCon2Ids.NintendoVendorId;

    private readonly ConcurrentDictionary<string, byte> _diagnosticKeys = new();

    /// <summary>Total advertisements observed, from any device.</summary>
    public int TotalAdvertisements => _totalAdvertisements;

    /// <summary>Distinct BLE addresses observed, from any device.</summary>
    public int DistinctDevices => _allDevices.Count;

    /// <summary>
    /// Names of nearby devices whose name mentions a Joy-Con or Nintendo, regardless of whether the
    /// manufacturer data parsed. If a controller shows up here but not as a discovery, the problem
    /// is the manufacturer-data offsets rather than the controller.
    /// </summary>
    public IReadOnlyCollection<string> InterestingNames => _interestingNames.Keys.ToArray();

    private int _totalAdvertisements;
    private readonly ConcurrentDictionary<ulong, byte> _allDevices = new();
    private readonly ConcurrentDictionary<string, byte> _interestingNames = new();

    public JoyCon2Scanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _watcher.Received += OnReceived;
    }

    public IReadOnlyCollection<JoyCon2Discovery> Seen => _seen.Values.ToArray();

    /// <summary>
    /// Starts scanning. Throws <see cref="BluetoothUnavailableException"/> rather than a bare
    /// COMException when the radio is disabled, so callers can report something actionable.
    /// </summary>
    public void Start()
    {
        try
        {
            _watcher.Start();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x800710DF))
        {
            throw new BluetoothUnavailableException(
                "The Bluetooth radio is not available. Turn Bluetooth on in " +
                "Settings > Bluetooth & devices, then retry.", ex);
        }
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
    }

    private void EmitDiagnostic(BluetoothLEAdvertisementReceivedEventArgs args, string? localName)
    {
        if (Diagnostic is null) return;

        var sections = args.Advertisement.ManufacturerData
            .Select(s => (s.CompanyId, Payload: BufferConvert.ToBytes(s.Data)))
            .ToArray();

        var relevant = args.BluetoothAddress == WatchAddress
            || sections.Any(s => IsNintendoCompanyId(s.CompanyId))
            || (localName?.Contains("joy", StringComparison.OrdinalIgnoreCase) ?? false)
            || (localName?.Contains("nintendo", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!relevant) return;

        var manufacturer = sections.Length == 0
            ? "none"
            : string.Join(" ", sections.Select(s => $"[company 0x{s.CompanyId:X4}: {Convert.ToHexString(s.Payload)}]"));
        var services = string.Join(",", args.Advertisement.ServiceUuids);
        var line =
            $"addr={args.BluetoothAddress:X12} type={args.AdvertisementType} rssi={args.RawSignalStrengthInDBm} " +
            $"name='{localName}' services=[{services}] manufacturer={manufacturer}";

        // RSSI changes on every packet, so dedupe on everything except it.
        var key = $"{args.BluetoothAddress:X12}|{args.AdvertisementType}|{localName}|{services}|{manufacturer}";
        if (_diagnosticKeys.TryAdd(key, 0)) Diagnostic(line);
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Counted for every advertisement, so "found nothing" can be distinguished from
        // "the radio is not actually scanning".
        Interlocked.Increment(ref _totalAdvertisements);
        _allDevices.TryAdd(args.BluetoothAddress, 0);

        var localName = args.Advertisement.LocalName;
        if (!string.IsNullOrWhiteSpace(localName)
            && (localName.Contains("joy", StringComparison.OrdinalIgnoreCase)
                || localName.Contains("nintendo", StringComparison.OrdinalIgnoreCase)))
        {
            _interestingNames.TryAdd($"{localName} ({args.BluetoothAddress:X12})", 0);
        }

        EmitDiagnostic(args, localName);
        if (args.BluetoothAddress == WatchAddress)
        {
            WatchedAddressSeen?.Invoke(
                args.AdvertisementType,
                args.Advertisement.ManufacturerData.Any(s => IsNintendoCompanyId(s.CompanyId)));
        }

        foreach (var section in args.Advertisement.ManufacturerData)
        {
            var payload = BufferConvert.ToBytes(section.Data);

            if (!JoyCon2Advertisement.TryIdentify(payload, out var identity))
            {
                // WinRT exposes the company ID separately, so check it directly as well.
                if (IsNintendoCompanyId(section.CompanyId))
                {
                    UnrecognizedNintendoAdvertisement?.Invoke(args.BluetoothAddress, payload);
                }

                continue;
            }

            var discovery = new JoyCon2Discovery(
                args.BluetoothAddress, identity, args.RawSignalStrengthInDBm, payload);

            if (_seen.TryAdd(args.BluetoothAddress, discovery))
            {
                Discovered?.Invoke(discovery);
            }
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        Stop();
    }
}
