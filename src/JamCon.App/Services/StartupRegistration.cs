using Microsoft.Win32;

namespace JamCon.App.Services;

/// <summary>
/// "Start with Windows": a per-user Run entry (HKCU, no elevation), pointing at the running exe.
/// The registry is the only record of the choice, so it shows correctly even if changed elsewhere.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JamCon";

    private static string? Command => Environment.ProcessPath is { } path ? $"\"{path}\"" : null;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Command is { } command) key.SetValue(ValueName, command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Repoints an existing entry at this exe, as the Start menu shortcut does. Returns a log line, or null.</summary>
    public static string? Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current || Command is not { } command || current == command) return null;
        key.SetValue(ValueName, command);
        return $"Start-with-Windows entry repointed at {command}.";
    }
}
