using JamCon.Core.Mapping;

namespace JamCon.Core.Engine;

/// <summary>
/// Shows the radial menu. The engine calls these on its own thread; implementations marshal to
/// their UI thread. Offsets are in device-independent pixels from the menu's center, positive y down.
/// </summary>
public interface IRadialMenuPresenter
{
    /// <summary>Open the menu centered on the current cursor position.</summary>
    void Show(RadialMenuConfiguration configuration);

    void Update(double offsetX, double offsetY, RadialMenuSelection? selection);

    /// <summary>Close the menu. <paramref name="selected"/> is the item about to run, or null.</summary>
    void Hide(RadialMenuItem? selected);
}

/// <summary>For headless use and tests that do not care about the menu.</summary>
public sealed class NullRadialMenuPresenter : IRadialMenuPresenter
{
    public static NullRadialMenuPresenter Instance { get; } = new();

    public void Show(RadialMenuConfiguration configuration)
    {
    }

    public void Update(double offsetX, double offsetY, RadialMenuSelection? selection)
    {
    }

    public void Hide(RadialMenuItem? selected)
    {
    }
}
