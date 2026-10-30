using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace JamCon.App.Services;

/// <summary>
/// Keeps a "JamCon" shortcut in the user's Start menu pointing at the running exe, so Windows
/// search finds the app. There is no installer yet, so the app registers itself on every launch;
/// the shortcut follows whichever copy was started last.
/// </summary>
internal static class StartMenuShortcut
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JamCon.lnk");

    /// <summary>Creates or repoints the shortcut. Returns a line for the log.</summary>
    public static string Ensure()
    {
        var target = Environment.ProcessPath;
        if (target is null) return "Start menu shortcut skipped: the exe path is unknown.";

        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;

            if (File.Exists(Path))
            {
                file.Load(Path, 0);
                var current = new StringBuilder(260);
                link.GetPath(current, current.Capacity, IntPtr.Zero, 0);
                if (string.Equals(current.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    return "Start menu shortcut is current.";
                }
            }

            link.SetPath(target);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target)!);
            link.SetDescription("Use a Switch 2 Joy-Con as a gyro mouse");
            link.SetIconLocation(target, 0);
            file.Save(Path, true);
            return $"Start menu shortcut written: {Path} -> {target}";
        }
        catch (Exception ex)
        {
            return $"Couldn't write the Start menu shortcut: {ex.Message}";
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
