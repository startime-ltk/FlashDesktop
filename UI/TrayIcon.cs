using System.Runtime.InteropServices;
using FlashDesktop.Core;

namespace FlashDesktop.UI;

/// <summary>
/// Pure Win32 tray icon: Shell_NotifyIconW + a hidden message-only window.
/// No WinForms / System.Drawing assembly is loaded at all (private-byte budget,
/// red line 2). The tray is the ONLY way back after "hide panel", so it lives
/// independently of the card windows and survives their destruction.
///
/// V1 decision (2026-11-06, boss): no autostart entry is ever written, so the
/// terminal menu item is simply "退出" and there is nothing to clean up.
/// Win11 may fold inactive icons into the notification overflow arrow - we do
/// not (and cannot) force visibility; we guarantee the icon always exists and
/// show a one-time hint on first hide.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new();

    private const string ClassName = "FlashDesktopTrayWindow";
    private const uint TrayCallback = 0x8001;              // WM_APP + 1
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_NULL = 0x0000;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01, NIIF_NOSOUND = 0x10;

    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080;
    private const uint IdToggle = 1, IdClient = 2, IdUpdate = 3, IdAbout = 4, IdExit = 5;

    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const int HotkeyId = 1;
    private const uint VkSpace = 0x20;

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly DesktopState _state;
    private readonly WindowLayer _layer;
    private readonly System.Windows.Application _app;
    private readonly CardContext _ctx;
    private bool _hotkeyRegistered;
    private readonly WndProc _wndProc;                 // keep alive: native callback
    private readonly UIntPtr _classAtom;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated;
    private readonly string _classNameBuffer;
    private IntPtr _classNamePtr;
    private bool _firstHide = true;
    private bool _created;
    private bool _disposed;
    private ClientWindow? _client;

    public bool IsCreated => _created && !_disposed;
    public bool FirstHideHintShown => !_firstHide;

    /// <summary>
    /// Test hook used by --self-test only. Injects the same message the shell
    /// sends on a real tray double-click, so the full WndProc -> Toggle ->
    /// WindowLayer path is exercised without driving the physical mouse.
    /// </summary>
    public void SimulateTrayMessage(uint mouseMessage) =>
        WndProcImpl(_hwnd, TrayCallback, IntPtr.Zero, new IntPtr(mouseMessage));

    public TrayIcon(DesktopState state, WindowLayer layer, System.Windows.Application app, CardContext ctx)
    {
        _state = state;
        _layer = layer;
        _app = app;
        _ctx = ctx;
        _wndProc = WndProcImpl;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _classNameBuffer = ClassName + "_" + Environment.ProcessId;
        _classNamePtr = Marshal.StringToHGlobalUni(_classNameBuffer);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _classNamePtr
        };
        _classAtom = RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, _classNameBuffer, "", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("tray message window creation failed");

        Instances[_hwnd] = this;

        _icon = LoadIcon(IntPtr.Zero, IdiApplication);
        var data = BaseData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.szTip = "FlashDesktop";
        _created = Shell_NotifyIcon(NIM_ADD, ref data);

        if (_ctx.Settings.ShowOverlayHotkey && _hwnd != IntPtr.Zero)
        {
            _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_ALT, VkSpace);
        }

        _state.Changed += OnStateChanged;
    }

    private NOTIFYICONDATA BaseData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = TrayCallback,
        hIcon = _icon,
        szTip = "FlashDesktop",
        szInfo = "",
        szInfoTitle = "",
        guidItem = Guid.Empty
    };

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hWnd, out var self))
            return DefWindowProc(hWnd, msg, wParam, lParam);

        if (msg == TrayCallback)
        {
            uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WM_LBUTTONDBLCLK || mouse == WM_LBUTTONUP)
                self.Toggle();
            else if (mouse == WM_RBUTTONUP)
                self.ShowMenu();
            return IntPtr.Zero;
        }

        if (msg == WM_HOTKEY && _hotkeyRegistered)
        {
            // V2 overlay: Alt+Space. Event-driven, not a hook, not a timer.
            self.ShowOverlay();
            return IntPtr.Zero;
        }

        if (self._taskbarCreated != 0 && msg == (uint)self._taskbarCreated)
        {
            // explorer restarted: re-add the icon, then re-assert card Z-order.
            // Event-driven, no timer, no polling.
            var data = self.BaseData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            data.szTip = self._state.CardsVisible
                ? "FlashDesktop"
                : "FlashDesktop · 面板已隐藏，点此恢复";
            self._created = Shell_NotifyIcon(NIM_ADD, ref data);
            self._layer.ReassertZOrder();
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, (UIntPtr)IdToggle,
                _state.CardsVisible ? "隐藏面板" : "显示面板");
            AppendMenu(menu, MF_STRING, (UIntPtr)IdClient, "设置中心");
            AppendMenu(menu, MF_STRING, (UIntPtr)IdUpdate, "检查更新");
            AppendMenu(menu, MF_STRING, (UIntPtr)IdAbout, "关于");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, "");
            AppendMenu(menu, MF_STRING, (UIntPtr)IdExit, "退出");

            GetCursorPos(out var pt);
            // Standard pattern so the menu dismisses correctly when clicking away.
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch ((uint)cmd)
            {
                case IdToggle: Toggle(); break;
                case IdClient: ShowClient("外观风格"); break;
                case IdUpdate: ShowClient("检查更新"); break;
                case IdAbout: ShowClient("关于"); break;
                case IdExit: Exit(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) { /* menu text is rebuilt on each open */ }

    private void ShowClient(string page)
    {
        try
        {
            if (_client is { IsLoaded: true })
            {
                if (_client.WindowState == System.Windows.WindowState.Minimized)
                    _client.WindowState = System.Windows.WindowState.Normal;
                _client.NavigateTo(page);
                _client.Activate();
                return;
            }

            _client = new ClientWindow(_ctx, _layer, _state, page);
            _client.Closed += (_, _) => _client = null;
            _client.Show();
            _client.Activate();
        }
        catch { /* never block the desktop over a dialog */ }
    }

    private void ShowOverlay()
    {
        try { new HotkeyLauncher(_ctx).Show(); }
        catch { /* never block the desktop over a transient window */ }
    }

    private void Toggle()
    {
        if (_state.CardsVisible)
        {
            _layer.Hide(); // destroy windows -> original desktop, instantly
            if (_firstHide)
            {
                _firstHide = false;
                // One-time hint. Balloon can be swallowed by Focus Assist, so the
                // tooltip is also updated permanently as a reliable fallback.
                Balloon("FlashDesktop", "面板已隐藏，可从托盘图标一键恢复", true);
                SetTip("FlashDesktop · 面板已隐藏，点此恢复");
            }
        }
        else
        {
            _layer.Show();
            SetTip("FlashDesktop");
        }
    }

    private void SetTip(string tip)
    {
        if (!IsCreated) return;
        var data = BaseData(NIF_TIP);
        data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Balloon(string title, string text, bool withTipChange)
    {
        if (!IsCreated) return;
        var data = BaseData(NIF_INFO | (withTipChange ? 0u : NIF_TIP));
        data.szInfo = text;
        data.szInfoTitle = title;
        data.szTip = _state.CardsVisible
            ? "FlashDesktop"
            : "FlashDesktop · 面板已隐藏，点此恢复";
        data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        data.uTimeoutOrVersion = 3000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Exit()
    {
        // V1 writes no autostart entry, no registry key, no shortcut in Startup.
        // There is literally nothing to clean up - exit is a pure process exit.
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnStateChanged;

        if (_created)
        {
            var data = BaseData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _created = false;
        }
        if (_hotkeyRegistered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }
        Instances.Remove(_hwnd);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_classAtom != UIntPtr.Zero) UnregisterClass(_classNameBuffer, GetModuleHandle(null));
        if (_classNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern UIntPtr RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr owner, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
