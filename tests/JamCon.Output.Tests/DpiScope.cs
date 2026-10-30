using System.Runtime.InteropServices;

namespace JamCon.Output.Tests;

/// <summary>
/// Makes the current thread per-monitor DPI aware (v2) for its lifetime, matching JamCon.App's
/// manifest. Without it, a test on a scaled display gets virtualized coordinates the app never sees.
/// </summary>
internal sealed partial class DpiScope : IDisposable
{
    private const nint PerMonitorAwareV2 = -4;
    private readonly nint _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);

    public void Dispose() => SetThreadDpiAwarenessContext(_previous);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);
}
