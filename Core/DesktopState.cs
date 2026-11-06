namespace FlashDesktop.Core;

/// <summary>
/// The single source of truth for desktop state (red line 1 + reversibility slot #2).
/// V2 only has card visibility; the icon-visibility slot is reserved and always true.
/// All show/hide logic must go through this class, never through tray button handlers.
/// </summary>
public sealed class DesktopState
{
    private bool _cardsVisible = true;

    public event EventHandler? Changed;

    public bool CardsVisible => _cardsVisible;

    /// <summary>Reserved for a future "hide icons" switch. V2 never changes it.</summary>
    public bool IconsVisible => true;

    public void SetCardsVisible(bool visible)
    {
        if (_cardsVisible == visible) return;
        _cardsVisible = visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>No-op in V2: the program never writes system state.</summary>
    public void SetIconsVisible(bool visible)
    {
        // Future: delegate to IconVisibilityService + marker file + startup self-check.
        // Deliberately empty now, see plan section 0.1 (red line 1).
    }
}
