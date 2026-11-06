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

    public sealed record Card(Window Window, IntPtr Handle, Rect Rect);

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
            if (y + _layout.CardHeight > area.Bottom) break;

            var window = new Window
            {
                Title = "FD " + cat,
                Left = x,
                Top = y,
                Width = _layout.CardWidth,
                Height = _layout.CardHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
                Background = Brushes.Transparent,
                Content = new CardView(_ctx, cat)
            };

            window.SourceInitialized += (_, _) => OnSourceReady(window);
            window.Show(); // SW_SHOWNA because ShowActivated=false

            var handle = new WindowInteropHelper(window).Handle;
            _cards.Add(new Card(window, handle, new Rect(x, y, _layout.CardWidth, _layout.CardHeight)));

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
    private static void ApplyZOrder(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        var insertAfter = progman != IntPtr.Zero ? progman : HWND_BOTTOM;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
