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

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            var payload = BufferConvert.ToBytes(section.Data);

            if (!JoyCon2Advertisement.TryIdentify(payload, out var identity))
            {
                // WinRT exposes the company ID separately, so check it directly as well.
                if (section.CompanyId == JoyCon2Ids.NintendoVendorId)
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
