using System.IO;
using System.Text.Json;
using JamCon.Bluetooth;
using JamCon.Core.Protocol;

namespace JamCon.App.Services;

/// <summary>
/// Persists the remembered controller so the app reconnects to the same Joy-Con 2 after a restart
/// instead of waiting for any controller in SYNC mode.
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private KnownJoyCon2? _controller;

    public SettingsStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        _controller = Load();
    }

    public KnownJoyCon2? Controller
    {
        get
        {
            lock (_gate) return _controller;
        }
    }

    public void Remember(KnownJoyCon2 controller)
    {
        lock (_gate)
        {
            if (_controller == controller) return;
            _controller = controller;
            Save();
        }

        _log($"Remembering {controller.DisplayName} {JoyCon2Link.Format(controller.Address)}.");
    }

    public void Forget()
    {
        lock (_gate)
        {
            _controller = null;
            Save();
        }

        _log("Forgot the remembered controller.");
    }

    private KnownJoyCon2? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dto = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_path));
            if (dto?.ControllerAddress is not { } address
                || !Enum.TryParse<JoyCon2Handedness>(dto.ControllerHandedness, out var handedness))
            {
                return null;
            }

            return new KnownJoyCon2(Convert.ToUInt64(address.Replace(":", ""), 16), handedness);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or OverflowException)
        {
            // A corrupt settings file should cost the user one SYNC press, not a crash on startup.
            _log($"Ignoring unreadable settings file {_path}: {ex.Message}");
            return null;
        }
    }

    private void Save()
    {
        var dto = new SettingsFile
        {
            ControllerAddress = _controller is { } c ? JoyCon2Link.Format(c.Address) : null,
            ControllerHandedness = _controller?.Handedness.ToString(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class SettingsFile
    {
        public string? ControllerAddress { get; set; }
        public string? ControllerHandedness { get; set; }
    }
}
