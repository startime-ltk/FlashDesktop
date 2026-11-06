using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FlashDesktop.Core;

/// <summary>
/// Red line 1: the only file that can "lose the user's layout" is our own items.json.
/// Write = temp file + atomic replace + one .bak. Startup = validate + rollback.
/// We never overwrite a corrupt file with an empty one.
/// Holds the working set of reference entries (we NEVER store the user's real files).
/// </summary>
public sealed class ItemStore
{
    private readonly string _file;
    private readonly string _bak;
    private readonly string _tmp;
    private ItemDocument _doc = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public bool RecoveredFromBackup { get; private set; }
    public bool UsedEmptyFallback { get; private set; }

    public ItemStore(string file)
    {
        _file = file;
        _bak = file + ".bak";
        _tmp = file + ".tmp";
    }

    public IReadOnlyList<ItemEntry> Items => _doc.Items;

    public bool Empty => _doc.Items.Count == 0;

    public void LoadFromDisk()
    {
        var fromMain = TryRead(_file);
        if (fromMain is not null) { _doc = fromMain; return; }

        var fromBak = TryRead(_bak);
        if (fromBak is not null)
        {
            RecoveredFromBackup = true;
            _doc = fromBak;
            return;
        }

        // Never write an empty file over a damaged one.
        UsedEmptyFallback = true;
        _doc = new ItemDocument();
    }

    /// <summary>Persist. Keeps a single .bak of the previous good version.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_file)) File.Copy(_file, _bak, true);

        File.WriteAllText(_tmp, JsonSerializer.Serialize(_doc, JsonOpts));
        File.Move(_tmp, _file, true);
    }

    private static ItemDocument? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<ItemDocument>(File.ReadAllText(path), JsonOpts);
            if (doc is null || doc.SchemaVersion != 1) return null;
            doc.Items ??= new List<ItemEntry>();
            return doc;
        }
        catch
        {
            return null;
        }
    }

    // ---- queries -----------------------------------------------------------

    public IReadOnlyList<string> AllCategories() =>
        _doc.Items.Where(i => !i.Hidden)
            .Select(i => i.Category).Distinct().OrderBy(c => c).ToList();

    public IReadOnlyList<ItemEntry> ItemsIn(string category, bool freqSort) =>
        _doc.Items
            .Where(i => i.Category == category && !i.Hidden)
            .OrderBy(i => freqSort ? -i.UsageCount : i.ManualOrder)
            .ToList();

    /// <summary>How many entries the user pulled out of the cards (restorable).</summary>
    public int HiddenCount => _doc.Items.Count(i => i.Hidden);

    /// <summary>Seed the whole catalog at once (first run). Persists a single time.</summary>
    public void Seed(IReadOnlyList<ItemEntry> items)
    {
        _doc.Items = new List<ItemEntry>(items);
        Save();
    }

    public ItemEntry? ById(string id) => _doc.Items.FirstOrDefault(i => i.Id == id);

    // ---- mutations (each persists) ----------------------------------------

    /// <summary>Add a reference to a real file/folder. Never moves or copies the file.</summary>
    public ItemEntry AddReference(string originalPath, string displayName, string category, string kind = "file")
    {
        // Dragging a previously removed item back should restore it, not duplicate it.
        var existing = _doc.Items.FirstOrDefault(i =>
            string.Equals(i.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Hidden = false;
            existing.Category = category;
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName;
            RenumberCategory(category);
            Save();
            return existing;
        }

        var inCat = _doc.Items.Where(i => i.Category == category && !i.Hidden).ToList();
        int maxOrder = inCat.Count == 0 ? 0 : inCat.Max(i => i.ManualOrder);
        var e = new ItemEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalPath = originalPath,
            DisplayName = displayName,
            Category = category,
            Kind = kind,
            ManualOrder = maxOrder + 1,
            UsageCount = 0
        };
        _doc.Items.Add(e);
        Save();
        return e;
    }

    public void MoveToCategory(ItemEntry e, string toCategory)
    {
        if (e is null) return;
        e.Category = toCategory;
        RenumberCategory(toCategory);
        Save();
    }

    /// <summary>Reorder within a category: move item at fromIndex to toIndex.</summary>
    public void ReorderWithin(string category, int fromIndex, int toIndex)
    {
        var list = _doc.Items.Where(i => i.Category == category)
            .OrderBy(i => i.ManualOrder).ToList();
        if (fromIndex < 0 || fromIndex >= list.Count) return;
        if (toIndex < 0 || toIndex >= list.Count) return;
        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, item);
        for (int k = 0; k < list.Count; k++) list[k].ManualOrder = k;
        Save();
    }

    /// <summary>
    /// Pull a tile out of the panel. Soft delete: only a flag in OUR json changes.
    /// The real file/folder is never touched, and the entry can be restored later.
    /// </summary>
    public void Remove(string id)
    {
        var e = ById(id);
        if (e is null) return;
        e.Hidden = true;
        RenumberCategory(e.Category);
        Save();
    }

    /// <summary>Bring one removed tile back (used by self-test / future per-item undo).</summary>
    public bool Restore(string id)
    {
        var e = ById(id);
        if (e is null || !e.Hidden) return false;
        e.Hidden = false;
        RenumberCategory(e.Category);
        Save();
        return true;
    }

    /// <summary>Bring every removed tile back to its card. Returns how many were restored.</summary>
    public int RestoreAll()
    {
        int n = 0;
        foreach (var e in _doc.Items.Where(i => i.Hidden))
        {
            e.Hidden = false;
            n++;
        }
        if (n == 0) return 0;
        foreach (var cat in AllCategories()) RenumberCategory(cat);
        Save();
        return n;
    }

    /// <summary>Hard delete used only by self-test cleanup (never a user action).</summary>
    public bool Purge(string id)
    {
        var e = ById(id);
        if (e is null) return false;
        _doc.Items.Remove(e);
        RenumberCategory(e.Category);
        Save();
        return true;
    }

    public void TouchUsage(string id)
    {
        var e = ById(id);
        if (e is null) return;
        e.UsageCount++;
        Save();
    }

    private void RenumberCategory(string category)
    {
        var list = _doc.Items.Where(i => i.Category == category && !i.Hidden)
            .OrderBy(i => i.ManualOrder).ToList();
        for (int k = 0; k < list.Count; k++) list[k].ManualOrder = k;
    }
}

public sealed class ItemDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ItemEntry> Items { get; set; } = new();
}

/// <summary>A reference only. Never a real user file. Original name is always kept.</summary>
public sealed class ItemEntry
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";   // file/folder/.lnk path, or AUMID for UWP
    public string DisplayName { get; set; } = "";     // original name, never rewritten
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "file";        // "file" | "uwp" | "folder" | "lnk"
    public int ManualOrder { get; set; }
    public int UsageCount { get; set; }
    public string? IconKey { get; set; }              // set after first icon extraction

    /// <summary>True = pulled out of the card by the user; only a flag, nothing deleted.</summary>
    public bool Hidden { get; set; }
}
