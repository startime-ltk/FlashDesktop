using System.IO;
using System.Text.Json;

namespace FlashDesktop.Core;

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
                    return cfg;
            }
        }
        catch
        {
            // A broken layout file must never stop the desktop from working.
        }

        var fallback = new LayoutConfig();
        try { fallback.Save(path); } catch { /* read-only dir: run with defaults */ }
        return fallback;
    }

    /// <summary>Atomic replace: write .tmp, then move. Never overwrite in place.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, true);
    }
}
