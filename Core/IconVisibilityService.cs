namespace FlashDesktop.Core;

public interface IIconVisibilityService
{
    bool IsVisible { get; }
    void SetVisible(bool visible);
}

/// <summary>
/// Reversibility slot #3. V2 is a no-op: no HideIcons, no WM_SETTINGCHANGE,
/// zero persistent system changes (plan section 0.1, red line 1).
/// </summary>
public sealed class NoOpIconVisibilityService : IIconVisibilityService
{
    public bool IsVisible => true;

    public void SetVisible(bool visible)
    {
        // Intentionally does nothing. A future implementation would need:
        // remember original value on disk, restore on exit, self-check on startup,
        // and a secondary-monitor policy. That change is vetoed by red line 1
        // unless the owner explicitly re-authorises it.
    }
}
