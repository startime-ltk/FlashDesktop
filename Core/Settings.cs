using System;
using System.IO;
using System.Text.Json;

namespace FlashDesktop.Core;

/// <summary>User preferences. Stored as settings.json, atomic write, no system state.</summary>
public sealed class Settings
{
    public int SchemaVersion { get; set; } = 2;
    public bool Animation { get; set; } = true;      // §5 animation switch (default on)
    public bool FreqSort { get; set; } = false;      // 常用置顶 (default off = keep manual order)
    public string Theme { get; set; } = "auto";      // auto | dark | light (§4)
    public int GridDensity { get; set; } = 24;       // icons per card target (§3.1)
    public bool ShowOverlayHotkey { get; set; } = true; // V2 Alt+Space overlay

    // ---- S5 client: appearance -------------------------------------------
    public string Accent { get; set; } = "#60CDFF";   // 主色调 (hex)
    public string CardStyle { get; set; } = "gradient"; // solid | translucent | gradient
    public int CornerRadius { get; set; } = 8;        // 0..16
    public int TileSize { get; set; } = 84;           // 64..110

    // ---- S5 client: update ------------------------------------------------
    /// <summary>Default manifest lives in the public source repo.</summary>
    public const string DefaultUpdateUrl =
        "https://raw.githubusercontent.com/startime-ltk/FlashDesktop/main/version.json";

    public string UpdateUrl { get; set; } = DefaultUpdateUrl; // version.json manifest

    private static readonly string[] CardStyles = { "solid", "translucent", "gradient" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOpts);
                if (s is not null && s.SchemaVersion is 1 or 2)
                {
                    s.Normalize();
                    // Persist the migration once, so later reads (and the client's
                    // update field) see the same values as this run.
                    try { s.Save(path); } catch { /* read-only: keep in-memory */ }
                    return s;
                }
            }
        }
        catch { /* broken settings must never break the desktop */ }

        var fallback = new Settings();
        fallback.Normalize();
        try { fallback.Save(path); } catch { /* read-only: run with defaults */ }
        return fallback;
    }

    /// <summary>Clamp/repair anything a hand-edited or older settings.json could hold.</summary>
    private void Normalize()
    {
        SchemaVersion = 2;

        if (Theme is not ("auto" or "dark" or "light")) Theme = "auto";
        if (Array.IndexOf(CardStyles, CardStyle) < 0) CardStyle = "gradient";

        GridDensity = Math.Clamp(GridDensity, 4, 80);
        CornerRadius = Math.Clamp(CornerRadius, 0, 16);
        TileSize = Math.Clamp(TileSize, 64, 110);

        if (!IsHexColor(Accent)) Accent = "#60CDFF";
        if (string.IsNullOrWhiteSpace(UpdateUrl)) UpdateUrl = DefaultUpdateUrl;
    }

    private static bool IsHexColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length != 4 && t.Length != 7 && t.Length != 9) return false;
        if (t[0] != '#') return false;
        foreach (var c in t.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, true);
    }
}
