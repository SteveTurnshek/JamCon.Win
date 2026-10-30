using System.IO;

namespace JamCon.App.Services;

/// <summary>Per-user locations, under %LOCALAPPDATA%\JamCon.Win as the plan specifies.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamCon.Win");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogDirectory => Path.Combine(Root, "logs");
}
