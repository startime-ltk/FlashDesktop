namespace FlashDesktop.Core;

/// <summary>
/// Shared services + the single data source the card views read from.
/// Grouping/sorting live here so WindowLayer and CardView never diverge.
/// </summary>
public sealed class CardContext
{
    public ItemStore Store { get; }
    public IconLoader Icons { get; }
    public Settings Settings { get; }
    public string SettingsFile { get; }

    public event System.Action? DataChanged;

    public CardContext(ItemStore store, IconLoader icons, Settings settings, string settingsFile)
    {
        Store = store;
        Icons = icons;
        Settings = settings;
        SettingsFile = settingsFile;
    }

    public System.Collections.Generic.IReadOnlyList<string> Categories() => Store.AllCategories();

    public System.Collections.Generic.IReadOnlyList<ItemEntry> ItemsIn(string category) =>
        Store.ItemsIn(category, Settings.FreqSort);

    public void NotifyChanged() => DataChanged?.Invoke();
}
