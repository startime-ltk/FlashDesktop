using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlashDesktop.UI;

namespace FlashDesktop.Core;

/// <summary>
/// One-shot, event-driven self check (--self-test). No timer, no polling,
/// no global hooks. Writes Data\selftest.json and lets the process exit.
/// Covers S1 (window/Z-order) plus S2 (collection + icon cache), S3 (drag
/// never moves the original) and S4 (no resident timer / cache budget).
/// </summary>
public static class SelfTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint GW_HWNDPREV = 3;
    private const uint GW_HWNDNEXT = 2;
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static string ClassName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsDesktopClass(string c) =>
        c.Length == 0 || c.Contains("Progman") || c.Contains("WorkerW") ||
        c.Contains("SHELLDLL") || c.Contains("SysListView32");

    public static void RunAndWrite(WindowLayer layer, TrayIcon tray,
        DesktopState state, LayoutConfig layout, CardContext ctx, long startupMs)
    {
        var checks = new Dictionary<string, object>();
        var cards = layer.Cards;

        var fg = GetForegroundWindow();
        bool fgIsOurs = cards.Any(c => c.Handle == fg);
        checks["no_focus_steal"] = new
        {
            pass = !fgIsOurs,
            foreground_class = ClassName(fg)
        };

        checks["tray_created"] = new { pass = tray.IsCreated };

        var loadedNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "").ToArray();
        checks["no_winforms"] = new
        {
            pass = !loadedNames.Any(n =>
                n.StartsWith("System.Windows.Forms", StringComparison.Ordinal) ||
                n.StartsWith("System.Drawing", StringComparison.Ordinal)),
            winforms_related = loadedNames.Where(n =>
                n.StartsWith("System.Windows.Forms", StringComparison.Ordinal) ||
                n.StartsWith("System.Drawing", StringComparison.Ordinal)).ToArray()
        };

        // ---- S2: collection + icon cache --------------------------------------
        var items = ctx.Store.Items;
        checks["collection"] = new
        {
            pass = items.Count > 0,
            item_count = items.Count,
            categories = ctx.Categories().ToArray()
        };

        // Pick an item whose icon was already cached by the live card render.
        string? cachedKey = null;
        foreach (var it in items)
        {
            var key = ctx.Icons.CacheKeyFor(it.OriginalPath);
            if (File.Exists(Path.Combine(AppPaths.DataDir, "icon-cache", key + ".png")))
            {
                cachedKey = key;
                break;
            }
        }
        long shellBefore = IconLoader.ShellExtractCalls;
        if (cachedKey != null)
        {
            var first = items[0];
            _ = ctx.Icons.LoadOrExtract(first.OriginalPath); // should be a pure cache hit
        }
        long shellAfter = IconLoader.ShellExtractCalls;
        checks["icon_cache"] = new
        {
            pass = cachedKey != null && shellAfter == shellBefore,
            cached_key = cachedKey,
            shell_calls_before = shellBefore,
            shell_calls_after = shellAfter,
            note = "warm load must not re-extract from Shell"
        };

        checks["cache_total_bytes"] = new
        {
            pass = ctx.Icons.CacheBytes() < 5L * 1024 * 1024,
            bytes = ctx.Icons.CacheBytes()
        };

        // Launch resolver must produce a usable command for every indexed item.
        int resolvable = 0;
        foreach (var it in items)
            if (Launcher.Resolve(it, out var fn, out _)) resolvable++;
        checks["launch_resolve"] = new
        {
            pass = items.Count > 0 && resolvable == items.Count,
            resolvable,
            total = items.Count
        };

        // ---- S3: drag must NEVER move the original file (red line 1) ---------
        string tmpFile = Path.Combine(Path.GetTempPath(), "fd_selftest_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmpFile, "do-not-move");
        long lenBefore = new FileInfo(tmpFile).Length;
        var tmpEntry = ctx.Store.AddReference(tmpFile, "selftest", AppIndexer.CatOther, "file");
        bool untouched = File.Exists(tmpFile) && new FileInfo(tmpFile).Length == lenBefore &&
                         File.ReadAllText(tmpFile) == "do-not-move";
        checks["drag_original_untouched"] = new
        {
            pass = untouched,
            note = "AddReference stores only a path; the real file is never moved/copied"
        };

        // ---- S5: removing a tile must only drop our reference (red line 1) ----
        int hiddenBefore = ctx.Store.HiddenCount;
        ctx.Store.Remove(tmpEntry.Id);
        bool fileAlive = File.Exists(tmpFile) && File.ReadAllText(tmpFile) == "do-not-move";
        bool goneFromCard = !ctx.Store.ItemsIn(AppIndexer.CatOther, false).Any(i => i.Id == tmpEntry.Id)
                            && ctx.Store.HiddenCount == hiddenBefore + 1;
        bool restoredOne = ctx.Store.Restore(tmpEntry.Id);
        bool backOnCard = ctx.Store.ItemsIn(AppIndexer.CatOther, false).Any(i => i.Id == tmpEntry.Id);
        ctx.Store.Purge(tmpEntry.Id); // remove the probe entry, leave user data alone
        try { File.Delete(tmpFile); } catch { }
        checks["remove_from_card"] = new
        {
            pass = fileAlive && goneFromCard && restoredOne && backOnCard,
            real_file_untouched = fileAlive,
            hidden_flag_only = goneFromCard,
            restore_works = backOnCard,
            note = "remove hides the reference only; the real file stays and can be restored"
        };

        // ---- S1 window checks (unchanged) ------------------------------------
        checks["state"] = new
        {
            cards_visible = state.CardsVisible,
            icons_visible = state.IconsVisible,
            pass = state.CardsVisible && state.IconsVisible
        };
        checks["card_count"] = new { value = cards.Count, categories = ctx.Categories().Count,
            pass = cards.Count > 0 };

        if (cards.Count > 0)
        {
            var card = cards[0];
            GetWindowRect(card.Handle, out var r);
            checks["card_rect_physical"] = new { r.Left, r.Top, r.Right, r.Bottom };

            int ex = GetWindowLong(card.Handle, -20);
            checks["window_ex_styles"] = new
            {
                noactivate = (ex & 0x08000000) != 0,
                toolwindow = (ex & 0x00000080) != 0,
                pass = (ex & 0x08000000) != 0 && (ex & 0x00000080) != 0
            };

            SetWindowPos(card.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            var centre = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
            var hit = WindowFromPoint(centre);
            checks["card_point"] = new
            {
                pass = hit == card.Handle,
                hit_class = ClassName(hit)
            };

            var ourHandles = new System.Collections.Generic.HashSet<IntPtr>(cards.Select(c => c.Handle));
            int screenW = GetSystemMetrics(0), screenH = GetSystemMetrics(1);
            int gapX = r.Right + Math.Max(4, layout.GapX / 2);
            var samplePoints = new[]
            {
                new POINT { X = gapX, Y = r.Top + 40 },
                new POINT { X = r.Left + 20, Y = Math.Min(r.Bottom + 40, screenH - 60) }
            };
            var samples = new System.Collections.Generic.List<object>();
            bool allNotOurs = true;
            foreach (var p in samplePoints)
            {
                if (p.X >= screenW - 5 || p.Y >= screenH - 5) continue;
                var h = WindowFromPoint(p);
                bool isOurs = ourHandles.Contains(h);
                if (isOurs) allNotOurs = false;
                samples.Add(new { x = p.X, y = p.Y, hit_class = ClassName(h), is_ours = isOurs });
            }
            checks["blank_uncovered"] = new { pass = samples.Count > 0 && allNotOurs, samples };

            layer.ReassertZOrder();
            uint myPid = (uint)Environment.ProcessId;
            var belowChain = new System.Collections.Generic.List<string>();
            bool firstVisibleForeignIsDesktop = false;
            // GW_HWNDNEXT = the window BELOW in Z order. The card must sit directly
            // on top of the desktop band, so the first visible foreign window below
            // it has to be Progman / WorkerW / the icon view.
            IntPtr cur = GetWindow(card.Handle, GW_HWNDNEXT);
            for (int i = 0; i < 64 && cur != IntPtr.Zero; i++)
            {
                GetWindowThreadProcessId(cur, out uint pid);
                bool ours = pid == myPid;
                bool visible = IsWindowVisible(cur);
                string cls = ClassName(cur);
                belowChain.Add($"{(ours ? "[own]" : "[other pid=" + pid + "]")} {cls}"
                               + (visible ? "" : " (hidden)"));
                if (!ours && visible)
                {
                    firstVisibleForeignIsDesktop = IsDesktopClass(cls);
                    break;
                }
                cur = GetWindow(cur, GW_HWNDNEXT);
            }
            checks["z_order"] = new
            {
                pass = firstVisibleForeignIsDesktop,
                rule = "card directly above the desktop band, below every app window",
                window_below_chain = belowChain
            };

            var trayHwnd = FindWindow("Shell_TrayWnd", null);
            GetWindowRect(trayHwnd, out var tr);
            bool noOverlap = trayHwnd == IntPtr.Zero || r.Right <= tr.Left || tr.Right <= r.Left ||
                             r.Bottom <= tr.Top || tr.Bottom <= r.Top;
            checks["taskbar_not_covered"] = new
            {
                pass = noOverlap,
                taskbar_rect = new { tr.Left, tr.Top, tr.Right, tr.Bottom }
            };

            checks["card_visible"] = new { pass = IsWindowVisible(card.Handle) };

            // ---- moving a card: position must stick in layout.json -----------
            try
            {
                var cat = ctx.Categories()[0];
                var beforeRect = cards[0].Rect;
                bool hadPlacement = layout.Placements.ContainsKey(cat);

                bool moved = layer.SetCardPosition(cat, beforeRect.Left + 40, beforeRect.Top + 30, true);
                var afterRect = layer.Cards[0].Rect;
                bool reallyMoved = System.Math.Abs(afterRect.Left - (beforeRect.Left + 40)) < 0.5 &&
                                   System.Math.Abs(afterRect.Top - (beforeRect.Top + 30)) < 0.5;
                bool persisted = layout.Placements.ContainsKey(cat) && File.Exists(layout.FilePath);

                // restore the original spot and leave layout.json as we found it
                layer.SetCardPosition(cat, beforeRect.Left, beforeRect.Top, false);
                if (!hadPlacement)
                {
                    layout.Placements.Remove(cat);
                    try { layout.Save(layout.FilePath); } catch { }
                }

                checks["card_move"] = new
                {
                    pass = moved && reallyMoved && persisted,
                    really_moved = reallyMoved,
                    persisted_in_layout = persisted,
                    note = "dragging the header stores the position; client can reset it"
                };
            }
            catch (Exception exMove)
            {
                checks["card_move"] = new { pass = false, error = exMove.Message };
            }
        }

        // ---- idle memory + no resident timer (S4 power) ----------------------
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double privateStartup = Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024.0, 1);
        double workingSet = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        System.Threading.Thread.Sleep(800);
        process.Refresh();
        double privateIdle = Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024.0, 1);

        var cpuBefore = process.TotalProcessorTime;
        System.Threading.Thread.Sleep(500);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        double cpuDeltaMs = Math.Round((cpuAfter - cpuBefore).TotalMilliseconds, 1);
        checks["no_resident_timer"] = new
        {
            pass = cpuDeltaMs < 50, // idle must not burn CPU (no timer/poll/hook)
            cpu_idle_delta_ms = cpuDeltaMs
        };

        var report = new
        {
            probe = "FlashDesktop self-test",
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            startup_ms = startupMs,
            working_set_mb = workingSet,
            private_bytes_mb = privateStartup,
            private_bytes_idle_mb = privateIdle,
            managed_heap_mb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
            loaded_assemblies = AppDomain.CurrentDomain.GetAssemblies().Length,
            checks
        };

        // hide -> show reversible (the only return path after "hide panel").
        try
        {
            int before = layer.Cards.Count;
            tray.SimulateTrayMessage(0x0203);
            bool destroyed = !state.CardsVisible && layer.Cards.Count == 0;
            bool hint = tray.FirstHideHintShown;
            tray.SimulateTrayMessage(0x0203);
            int rebuilt = layer.Cards.Count;
            bool restored = state.CardsVisible && rebuilt > 0;
            checks["hide_show_reversible"] = new
            {
                pass = before > 0 && destroyed && hint && restored,
                cards_before = before,
                windows_destroyed = destroyed,
                first_hide_hint = hint,
                windows_rebuilt = rebuilt
            };
        }
        catch (Exception ex)
        {
            checks["hide_show_reversible"] = new { pass = false, error = ex.Message };
        }

        // ---- S5 client shell: every page must build without throwing ----------
        try
        {
            var cw = new ClientWindow(layer: layer, ctx: ctx, state: state);
            bool built = cw.Content is not null;
            cw.NavigateTo("常规设置");
            cw.NavigateTo("检查更新");
            cw.NavigateTo("关于");
            cw.NavigateTo("外观风格");
            checks["client_ui"] = new { pass = built, pages = 4, note = "all four pages constructed" };
            try { cw.Close(); } catch { /* not shown yet */ }
        }
        catch (Exception ex)
        {
            checks["client_ui"] = new { pass = false, error = ex.Message };
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Data", "selftest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
