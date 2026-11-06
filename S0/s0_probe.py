# -*- coding: utf-8 -*-
"""
FlashDesktop S0 probe - one-shot, NOT part of the main project.
Zero dependency: Python 3.x + ctypes calling Win32 directly (no .NET SDK needed).

Checks:
  1. no_focus_steal      (GetForegroundWindow unchanged after SW_SHOWNA)
  2. card_point          (WindowFromPoint inside card rect is our HWND)
  3. blank_uncovered     (blank points belong to SysListView32, not us)
  4. taskbar_not_covered (card rect does not intersect Shell_TrayWnd rect)
  5. dblclick_received   (real mouse input produces WM_LBUTTONDBLCLK)
  6. win_d_follow        (card vs. a normal control window, with validity check)

Side effect: a blank card + a control window appear briefly, the mouse cursor
moves for the double-click test and is restored, and the desktop is toggled
twice. The previous foreground window is restored at the end.
"""
import ctypes
import json
import os
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
g32 = ctypes.WinDLL("gdi32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

WPARAM_T = ctypes.c_size_t
LPARAM_T = ctypes.c_ssize_t
LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, w.HWND, w.UINT, WPARAM_T, LPARAM_T)
MSG_LOG = {}


class WNDCLASSW(ctypes.Structure):
    _fields_ = [("style", w.UINT), ("lpfnWndProc", WNDPROC),
                ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                ("hInstance", w.HINSTANCE), ("hIcon", w.HICON),
                ("hCursor", w.HGDIOBJ), ("hbrBackground", w.HBRUSH),
                ("lpszMenuName", w.LPCWSTR), ("lpszClassName", w.LPCWSTR)]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class MSG(ctypes.Structure):
    _fields_ = [("hwnd", w.HWND), ("message", w.UINT),
                ("wParam", WPARAM_T), ("lParam", LPARAM_T),
                ("time", w.DWORD), ("pt", POINT), ("lPrivate", w.DWORD)]


u32.DefWindowProcW.argtypes = [w.HWND, w.UINT, WPARAM_T, LPARAM_T]
u32.DefWindowProcW.restype = LRESULT
u32.RegisterClassW.argtypes = [ctypes.POINTER(WNDCLASSW)]
u32.RegisterClassW.restype = w.ATOM
u32.CreateWindowExW.argtypes = [w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD,
                                ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, w.HWND, w.HMENU,
                                w.HINSTANCE, w.LPVOID]
u32.CreateWindowExW.restype = w.HWND
u32.DestroyWindow.argtypes = [w.HWND]
u32.ShowWindow.argtypes = [w.HWND, ctypes.c_int]
u32.SetWindowPos.argtypes = [w.HWND, w.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, w.UINT]
u32.GetForegroundWindow.restype = w.HWND
u32.SetForegroundWindow.argtypes = [w.HWND]
u32.IsWindowVisible.argtypes = [w.HWND]
u32.IsWindowVisible.restype = w.BOOL
u32.IsIconic.argtypes = [w.HWND]
u32.IsIconic.restype = w.BOOL
u32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(RECT)]
u32.WindowFromPoint.argtypes = [POINT]
u32.WindowFromPoint.restype = w.HWND
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.FindWindowW.argtypes = [w.LPCWSTR, w.LPCWSTR]
u32.FindWindowW.restype = w.HWND
u32.SystemParametersInfoW.argtypes = [w.UINT, w.UINT, w.LPVOID, w.UINT]
u32.GetSystemMetrics.argtypes = [ctypes.c_int]
u32.PeekMessageW.argtypes = [ctypes.POINTER(MSG), w.HWND, w.UINT, w.UINT, w.UINT]
u32.TranslateMessage.argtypes = [ctypes.POINTER(MSG)]
u32.DispatchMessageW.argtypes = [ctypes.POINTER(MSG)]
u32.keybd_event.argtypes = [w.BYTE, w.BYTE, w.DWORD, ctypes.c_size_t]
u32.mouse_event.argtypes = [w.DWORD, w.DWORD, w.DWORD, w.DWORD, ctypes.c_size_t]
u32.GetCursorPos.argtypes = [ctypes.POINTER(POINT)]
u32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
k32.GetModuleHandleW.argtypes = [w.LPCWSTR]
k32.GetModuleHandleW.restype = w.HMODULE
g32.GetStockObject.argtypes = [ctypes.c_int]
g32.GetStockObject.restype = w.HGDIOBJ

WS_POPUP = 0x80000000
WS_OVERLAPPEDWINDOW = 0x00CF0000
WS_EX_NOACTIVATE = 0x08000000
WS_EX_TOOLWINDOW = 0x00000080
CS_DBLCLKS = 0x0008
SW_SHOWNA = 8
SW_SHOWNORMAL = 1
PM_REMOVE = 0x0001
SPI_GETWORKAREA = 0x0030
HWND_TOPMOST = w.HWND(-1)
HWND_NOTOPMOST = w.HWND(-2)
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
VK_LWIN = 0x5B
VK_D = 0x44
KEYEVENTF_KEYUP = 0x0002
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONDBLCLK = 0x0203

CLASS_CARD = "FD_S0_Card"
CLASS_CONTROL = "FD_S0_Control"


@WNDPROC
def _wndproc(hwnd, msg, wparam, lparam):
    MSG_LOG[msg] = MSG_LOG.get(msg, 0) + 1
    if msg == 0x0002:
        u32.PostQuitMessage(0)
        return 0
    return u32.DefWindowProcW(hwnd, msg, wparam, lparam)


def pump(seconds=0.3):
    end = time.time() + seconds
    msg = MSG()
    while time.time() < end:
        while u32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            u32.TranslateMessage(ctypes.byref(msg))
            u32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.01)


def class_name(hwnd):
    if not hwnd:
        return ""
    buf = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def rect_of(hwnd):
    r = RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return {"left": r.left, "top": r.top, "right": r.right, "bottom": r.bottom}


def intersects(a, b):
    return not (a["right"] <= b["left"] or b["right"] <= a["left"] or
                a["bottom"] <= b["top"] or b["bottom"] <= a["top"])


def state(hwnd):
    return {"visible": bool(u32.IsWindowVisible(hwnd)),
            "iconic": bool(u32.IsIconic(hwnd))}


def toggle_desktop():
    u32.keybd_event(VK_LWIN, 0, 0, 0)
    u32.keybd_event(VK_D, 0, 0, 0)
    u32.keybd_event(VK_D, 0, KEYEVENTF_KEYUP, 0)
    u32.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0)
    pump(0.7)


def create_window(cls, ex_style, style, x, y, cw, ch):
    hinst = k32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.style = CS_DBLCLKS
    wc.lpfnWndProc = _wndproc
    wc.hInstance = hinst
    wc.hbrBackground = g32.GetStockObject(0)
    wc.lpszClassName = cls
    if not u32.RegisterClassW(ctypes.byref(wc)):
        err = ctypes.get_last_error()
        if err != 1410:
            raise OSError("RegisterClassW failed: %s" % err)
    hwnd = u32.CreateWindowExW(ex_style, cls, cls, style,
                               x, y, cw, ch, None, None, hinst, None)
    if not hwnd:
        raise OSError("CreateWindowExW failed: %s" % ctypes.get_last_error())
    return hwnd


def real_double_click(px, py):
    old = POINT()
    u32.GetCursorPos(ctypes.byref(old))
    u32.SetCursorPos(px, py)
    for _ in range(2):
        u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
        u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
        time.sleep(0.04)
    pump(0.35)
    u32.SetCursorPos(old.x, old.y)
    return {"dblclk": MSG_LOG.get(WM_LBUTTONDBLCLK, 0),
            "lbuttondown": MSG_LOG.get(WM_LBUTTONDOWN, 0)}


def main():
    result = {"probe": "FlashDesktop S0", "bits": ctypes.sizeof(ctypes.c_void_p) * 8,
              "checks": {}}

    sw, sh = u32.GetSystemMetrics(0), u32.GetSystemMetrics(1)
    wa = RECT()
    u32.SystemParametersInfoW(SPI_GETWORKAREA, 0, ctypes.byref(wa), 0)
    work = {"left": wa.left, "top": wa.top, "right": wa.right, "bottom": wa.bottom}
    tray = u32.FindWindowW("Shell_TrayWnd", None)
    tray_rect = rect_of(tray) if tray else None
    result["screen"] = {"w": sw, "h": sh}
    result["work_area"] = work
    result["taskbar"] = {"class": class_name(tray), "rect": tray_rect}

    x, y = work["left"] + 60, work["top"] + 60
    cw, ch = 420, 300
    card = {"left": x, "top": y, "right": x + cw, "bottom": y + ch}
    result["card_rect"] = card

    prev_fg = u32.GetForegroundWindow()
    result["foreground_before"] = {"hwnd": int(prev_fg or 0),
                                   "class": class_name(prev_fg)}

    ex_card = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    card_hwnd = None
    ctrl_hwnd = None
    try:
        card_hwnd = create_window(CLASS_CARD, ex_card, WS_POPUP, x, y, cw, ch)
        u32.ShowWindow(card_hwnd, SW_SHOWNA)
        pump(0.3)

        after_fg = u32.GetForegroundWindow()
        result["checks"]["no_focus_steal"] = {
            "pass": bool(after_fg == prev_fg),
            "before": int(prev_fg or 0), "after": int(after_fg or 0),
            "after_class": class_name(after_fg)}

        u32.SetWindowPos(card_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE)
        pump(0.15)

        inside = POINT(x + cw // 2, y + ch // 2)
        inside_hwnd = u32.WindowFromPoint(inside)
        result["checks"]["card_point"] = {
            "pass": bool(inside_hwnd == card_hwnd),
            "hwnd": int(inside_hwnd or 0), "class": class_name(inside_hwnd),
            "ours": int(card_hwnd)}

        blanks = []
        for (px, py) in [(x + cw + 80, y + 40), (x + cw // 2, y + ch + 80),
                         (work["right"] - 30, work["top"] + 30),
                         (work["left"] + 20, work["bottom"] - 20)]:
            if not (work["left"] <= px < work["right"] and
                    work["top"] <= py < work["bottom"]):
                continue
            h = u32.WindowFromPoint(POINT(px, py))
            blanks.append({"point": [px, py], "hwnd": int(h or 0),
                           "class": class_name(h), "is_ours": bool(h == card_hwnd)})
        result["checks"]["blank_uncovered"] = {
            "pass": bool(blanks) and all(not b["is_ours"] for b in blanks),
            "samples": blanks}

        result["checks"]["taskbar_not_covered"] = {
            "pass": bool(tray_rect is None or not intersects(card, tray_rect)),
            "card_intersects_taskbar": bool(tray_rect and intersects(card, tray_rect))}

        MSG_LOG.clear()
        clicks = real_double_click(inside.x, inside.y)
        result["checks"]["dblclick_received"] = {
            "pass": bool(clicks["dblclk"] > 0), "detail": clicks}

        u32.SetWindowPos(card_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE)
        pump(0.15)

        # normal control window: proves the Win+D simulation actually worked
        cx = max(work["left"] + 20, work["right"] - 520)
        cy = work["top"] + 420
        ctrl_hwnd = create_window(CLASS_CONTROL, 0, WS_OVERLAPPEDWINDOW,
                                  cx, cy, 320, 220)
        u32.ShowWindow(ctrl_hwnd, SW_SHOWNORMAL)
        pump(0.3)

        before = {"card": state(card_hwnd), "control": state(ctrl_hwnd)}
        toggle_desktop()
        during = {"card": state(card_hwnd), "control": state(ctrl_hwnd)}
        fg_during = u32.GetForegroundWindow()
        toggle_desktop()
        after = {"card": state(card_hwnd), "control": state(ctrl_hwnd)}

        sim_ok = bool((not during["control"]["visible"]) or during["control"]["iconic"])
        result["wind_test"] = {
            "simulation_valid": sim_ok,
            "method": "keybd_event Win+D",
            "foreground_during": {"hwnd": int(fg_during or 0),
                                  "class": class_name(fg_during)},
            "before": before, "during": during, "after_restore": after,
            "card_follows": bool((not during["card"]["visible"]) or
                                 during["card"]["iconic"]),
            "control_minimised": sim_ok,
        }
    finally:
        if ctrl_hwnd:
            u32.DestroyWindow(ctrl_hwnd)
        if card_hwnd:
            u32.DestroyWindow(card_hwnd)
        pump(0.2)

    if prev_fg:
        u32.SetForegroundWindow(prev_fg)

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "s0-result.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=True, indent=2)
    print(json.dumps(result, ensure_ascii=True, indent=2))
    print("RESULT_FILE=" + out)


if __name__ == "__main__":
    main()
