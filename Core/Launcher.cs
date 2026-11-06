using System;
using System.Diagnostics;

namespace FlashDesktop.Core;

/// <summary>
/// Launches a reference entry. Red line 1: we only ever START the target, never
/// move/delete/copy it. UWP apps are launched via explorer + shell:AppsFolder.
/// </summary>
public static class Launcher
{
    /// <summary>Resolve an entry to a launchable command. Pure, no side effects (testable).</summary>
    public static bool Resolve(ItemEntry e, out string fileName, out string? arguments)
    {
        fileName = e.OriginalPath;
        arguments = null;
        if (string.IsNullOrWhiteSpace(e.OriginalPath)) return false;

        if (e.Kind == "uwp" || e.OriginalPath.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "explorer.exe";
            arguments = "shell:AppsFolder\\" + e.OriginalPath.Replace("shell:AppsFolder\\", "", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        return true;
    }

    public static void Launch(ItemEntry e)
    {
        if (!Resolve(e, out var fn, out var args)) return;
        var psi = new ProcessStartInfo(fn)
        {
            UseShellExecute = true,
            Arguments = args ?? ""
        };
        try { Process.Start(psi); }
        catch { /* user may have deleted the target; the tile will show as missing */ }
    }
}
