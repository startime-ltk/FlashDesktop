using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashDesktop.Core;

/// <summary>Saved top-left of one card (DIPs, relative to the virtual screen).</summary>
public sealed class CardPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
}

/// <summary>
/// Reversibility slot #1: all card geometry lives in layout.json, never hard-coded in XAML.
/// Changing icon visibility later only means tuning these numbers.
/// </summary>
public sealed class LayoutConfig
{
    public int SchemaVersion { get; set; } = 1;
    public int CardWidth { get; set; } = 420;
    public int CardHeight { get; set; } = 460;
    public int GapX { get; set; } = 40;
    public int GapY { get; set; } = 40;
    public int Margin { get; set; } = 48;
    public int Columns { get; set; } = 3;

    /// <summary>Cards dragged by the user. Missing category = use the auto grid.</summary>
    public Dictionary<string, CardPlacement> Placements { get; set; } = new();

    /// <summary>Where this config was loaded from, so a drag can persist itself.</summary>
    [JsonIgnore]
    public string FilePath { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static LayoutConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<LayoutConfig>(
                    File.ReadAllText(path), JsonOpts);
                if (cfg is not null && cfg.SchemaVersion == 1 &&
                    cfg.CardWidth > 50 && cfg.CardHeight > 50 &&
                    cfg.Columns is >= 1 and <= 8)
                {
                    cfg.Placements ??= new Dictionary<string, CardPlacement>();
                    cfg.FilePath = path;
                    return cfg;
                }
            }
        }
        catch
        {
            // A broken layout file must never stop the desktop from working.
        }

        var fallback = new LayoutConfig { FilePath = path };
        try { fallback.Save(path); } catch { /* read-only dir: run with defaults */ }
        return fallback;
    }

    /// <summary>Atomic replace: write .tmp, then move. Never overwrite in place.</summary>
    public void Save(string path)
    {
        if (string.IsNullOrEmpty(path)) path = FilePath;
        if (string.IsNullOrEmpty(path)) return;

        FilePath = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, true);
    }
}
