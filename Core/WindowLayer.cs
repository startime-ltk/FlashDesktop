using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlashDesktop.UI;

namespace FlashDesktop.Core;

/// <summary>
/// B+ core: one independent window per CATEGORY, blank gaps contain NO window.
/// No SetParent, no WorkerW, no global hooks, no timers. Windows are rebuilt from
/// CardContext on any data change (theme / sort / drop / move).
/// </summary>
public sealed class WindowLayer
{
    private readonly LayoutConfig _layout;
    private readonly DesktopState _state;
    private readonly CardContext _ctx;
    private readonly List<Card> _cards = new();
    private readonly int _taskbarCreated;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOP = new(0);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string text);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public sealed record Card(Window Window, IntPtr Handle, Rect Rect, string Category);

    public WindowLayer(LayoutConfig layout, DesktopState state, CardContext ctx)
    {
        _layout = layout;
        _state = state;
        _ctx = ctx;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _ctx.DataChanged += () => Rebuild();
    }

    public IReadOnlyList<Card> Cards => _cards;
    public int CardCount => _cards.Count;

    public void Show()
    {
        if (_cards.Count > 0) return;
        BuildWindows();
        _state.SetCardsVisible(true);
    }

    public void Hide()
    {
        foreach (var card in _cards)
        {
            try { card.Window.Close(); } catch { /* already gone */ }
        }
        _cards.Clear();
        _state.SetCardsVisible(false);
    }

    /// <summary>Rebuild all windows in place after a data/settings change.</summary>
    private void Rebuild()
    {
        if (_cards.Count == 0) return;
        bool wasVisible = _state.CardsVisible;
        Hide();
        if (wasVisible) Show();
    }

    private void BuildWindows()
    {
        var cats = _ctx.Categories();
        if (cats.Count == 0) return;

        var area = SystemParameters.WorkArea; // already excludes the taskbar
        double left = area.Left + _layout.Margin;
        double top = area.Top + _layout.Margin;
        double x = left;
        double y = top;
        int i = 0;

        foreach (var cat in cats)
        {
            double px, py;

            if (_layout.Placements.TryGetValue(cat, out var saved) && saved is not null)
            {
                // The user dragged this card before: put it back exactly there.
                (px, py) = Clamp(saved.Left, saved.Top, area);
            }
            else
            {
                if (y + _layout.CardHeight > area.Bottom) break; // no room on the auto grid
                px = x;
                py = y;
                i++;
                if (i % _layout.Columns == 0)
                {
                    x = left;
                    y += _layout.CardHeight + _layout.GapY;
                }
                else
                {
                    x += _layout.CardWidth + _layout.GapX;
                }
            }

            var view = new CardView(_ctx, cat);
            var window = new Window
            {
                Title = "FD " + cat,
                Left = px,
                Top = py,
                Width = _layout.CardWidth,
                Height = _layout.CardHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
                Background = Brushes.Transparent,
                Content = view
            };

            view.DragStarted += () => BeginDrag(cat, window);
            view.DragMoved += () => UpdateDrag(window);
            view.DragEnded += () => EndDrag(cat, window);

            window.SourceInitialized += (_, _) => OnSourceReady(window);
            window.Show(); // SW_SHOWNA because ShowActivated=false

            var handle = new WindowInteropHelper(window).Handle;

            // Showing a window raises it, which undoes the Z-order set in
            // SourceInitialized. Apply again after Show, and once more after the
            // first frame. Both are one-shot events - no timer, no polling.
            ApplyZOrder(handle);
            window.ContentRendered += (_, _) => ApplyZOrder(handle);

            _cards.Add(new Card(window, handle,
                new Rect(px, py, _layout.CardWidth, _layout.CardHeight), cat));
        }
    }

    // ---- card moving (drag by header, position persisted) --------------------

    private bool _dragging;
    private Window? _dragWindow;
    private int _dragStartX, _dragStartY;
    private double _dragStartLeft, _dragStartTop;

    private void BeginDrag(string category, Window window)
    {
        if (!GetCursorPos(out var p)) return;
        _dragging = true;
        _dragWindow = window;
        _dragStartX = p.X;
        _dragStartY = p.Y;
        _dragStartLeft = window.Left;
        _dragStartTop = window.Top;
    }

    private void UpdateDrag(Window window)
    {
        if (!_dragging || !ReferenceEquals(window, _dragWindow)) return;
        if (!GetCursorPos(out var p)) return;

        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0) scale = 1;

        window.Left = _dragStartLeft + (p.X - _dragStartX) / scale;
        window.Top = _dragStartTop + (p.Y - _dragStartY) / scale;
    }

    private void EndDrag(string category, Window window)
    {
        if (!_dragging || !ReferenceEquals(window, _dragWindow)) return;
        _dragging = false;
        _dragWindow = null;

        var (px, py) = Clamp(window.Left, window.Top, SystemParameters.WorkArea);
        window.Left = px;
        window.Top = py;

        var idx = _cards.FindIndex(c => c.Category == category);
        if (idx >= 0)
            _cards[idx] = _cards[idx] with
                { Rect = new Rect(px, py, _layout.CardWidth, _layout.CardHeight) };

        _layout.Placements[category] = new CardPlacement
        {
            Left = System.Math.Round(px),
            Top = System.Math.Round(py)
        };
        try { _layout.Save(_layout.FilePath); } catch { /* read-only: keep in memory */ }

        // Moving a window can raise it above other apps; sink it back onto the
        // desktop band so "above desktop, below every app" still holds.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) ApplyZOrder(hwnd);
    }

    /// <summary>Programmatic move (self-test / future layout presets).</summary>
    public bool SetCardPosition(string category, double left, double top, bool save)
    {
        var idx = _cards.FindIndex(c => c.Category == category);
        if (idx < 0) return false;

        var card = _cards[idx];
        card.Window.Left = left;
        card.Window.Top = top;
        _cards[idx] = card with
            { Rect = new Rect(left, top, _layout.CardWidth, _layout.CardHeight) };

        if (save)
        {
            _layout.Placements[category] = new CardPlacement { Left = left, Top = top };
            try { _layout.Save(_layout.FilePath); } catch { /* read-only: keep in memory */ }
        }
        return true;
    }

    /// <summary>Forget every dragged position and go back to the auto grid.</summary>
    public void ResetCardPositions()
    {
        _layout.Placements.Clear();
        try { _layout.Save(_layout.FilePath); } catch { /* read-only: keep in memory */ }
        Rebuild();
    }

    private (double Left, double Top) Clamp(double px, double py, Rect area)
    {
        // Keep the whole card inside the work area so it can never be lost off-screen.
        double maxLeft = System.Math.Max(area.Left, area.Right - _layout.CardWidth);
        double maxTop = System.Math.Max(area.Top, area.Bottom - _layout.CardHeight);
        return (System.Math.Clamp(px, area.Left, maxLeft),
                System.Math.Clamp(py, area.Top, maxTop));
    }

    public void ReassertZOrder()
    {
        foreach (var card in _cards)
        {
            if (card.Handle != IntPtr.Zero) ApplyZOrder(card.Handle);
        }
    }

    private void OnSourceReady(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndHook);

        ApplyZOrder(hwnd);
    }

    /// <summary>
    /// Put the card just above Progman (the desktop) in Z-order, i.e. above the
    /// icon view but below every ordinary application window. Event-driven only:
    /// called on creation and on TaskbarCreated (explorer restart).
    /// </summary>
    /// <summary>
    /// Put the card immediately ABOVE the whole desktop band (Progman / WorkerW /
    /// the icon view), which also puts it BELOW every ordinary application window.
    /// One-shot and event-driven: called on creation and on TaskbarCreated only.
    /// </summary>
    private static void ApplyZOrder(IntPtr hwnd)
    {
        var bandTop = FindDesktopBandTop();
        if (bandTop == IntPtr.Zero)
        {
            // No desktop found: sink to the bottom rather than float on top.
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            return;
        }

        // Insert *after* the window that currently sits right above the band.
        // Result: [apps] ... [card] [desktop band]. Never above a normal app.
        uint myPid = (uint)Environment.ProcessId;
        IntPtr above = GetWindow(bandTop, GW_HWNDPREV);
        while (above != IntPtr.Zero && IsOwnWindow(above, myPid))
            above = GetWindow(above, GW_HWNDPREV);

        var anchor = above != IntPtr.Zero ? above : HWND_TOP;
        SetWindowPos(hwnd, anchor, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static bool IsOwnWindow(IntPtr h, uint pid)
    {
        GetWindowThreadProcessId(h, out uint p);
        return p == pid;
    }

    /// <summary>
    /// Topmost VISIBLE window of the desktop band, walking the top-level Z order.
    /// Hidden windows are ignored: the band is what the user actually sees, and
    /// anchoring to a hidden window would leave the card above visible apps.
    /// </summary>
    private static IntPtr FindDesktopBandTop()
    {
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 1024)
        {
            if (IsWindowVisible(h) && IsDesktopClass(ClassNameOf(h))) return h;
            h = GetWindow(h, GW_HWNDNEXT);
        }
        return FindWindow("Progman", null);
    }

    private static bool IsDesktopClass(string c) =>
        c == "Progman" || c == "WorkerW" || c.Contains("SHELLDLL") || c == "SysListView32";

    private static string ClassNameOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            ReassertZOrder();
        }
        return IntPtr.Zero;
    }
}
