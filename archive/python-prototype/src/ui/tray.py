# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [单一消息泵] 全进程只能有一个托盘窗口与一条 GetMessage 循环。tkinter 的
   卡片窗口跑在它自己的线程里，不许在这里创建任何 Tk 对象。
2. [左键单击即弹卡片] 按用户要求：WM_LBUTTONUP 直接打开卡片（不是双击）。
   右键才弹菜单。改回「双击」会让单击变成无反应的死键。
3. [WM_TASKBARCREATED 自愈] explorer.exe 重启后托盘图标会消失，必须监听
   RegisterWindowMessage("TaskbarCreated") 并重新 Shell_NotifyIcon(NIM_ADD)。
   这是 AGENT_RULES.md 第 5 条的硬性要求。
4. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

import ctypes
import queue
import threading
from ctypes import wintypes as wt

from ..core import win32 as w32

# ---------------------------------------------------------------- 消息常量
WM_DESTROY = 0x0002
WM_CLOSE = 0x0010
WM_QUIT = 0x0012
WM_COMMAND = 0x0111
WM_CONTEXTMENU = 0x007B
WM_APP = 0x8000
WM_TRAYICON = WM_APP + 1
WM_APP_QUIT = WM_APP + 2
WM_APP_REFRESH = WM_APP + 3

WM_LBUTTONUP = 0x0202
WM_RBUTTONUP = 0x0205
WM_LBUTTONDBLCLK = 0x0203

NIM_ADD = 0x00
NIM_MODIFY = 0x01
NIM_DELETE = 0x02
NIF_MESSAGE = 0x01
NIF_ICON = 0x02
NIF_TIP = 0x04
NIF_INFO = 0x10
NIIF_INFO = 0x01

TPM_RIGHTALIGN = 0x0008
TPM_BOTTOMALIGN = 0x0020
TPM_RETURNCMD = 0x0100

MF_STRING = 0x0000
MF_SEPARATOR = 0x0800
MF_CHECKED = 0x0008
MF_UNCHECKED = 0x0000
MF_DEFAULT = 0x1000

# 菜单项 ID
CMD_OPEN_CARD = 1001
CMD_REFRESH = 1002
CMD_AUTOSTART = 1003
CMD_QUIT = 1004

WNDPROC = ctypes.WINFUNCTYPE(wt.LPARAM, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)


class TrayIcon:
    """一个 Shell_NotifyIcon 托盘图标 + 它自己的消息循环"""

    CLASS_NAME = "PeripheralBatteryTrayWnd"

    def __init__(self, tooltip: str, icon_builder):
        """
        tooltip:       初始悬停文本
        icon_builder:  () -> HICON，每次更新图标时调用
        """
        self._tooltip = tooltip
        self._build_icon = icon_builder
        self._hicon = None
        self._hwnd = None
        self._thread = None
        self._running = False
        self._ready = threading.Event()

        # 由外部注册的回调
        self.on_click = None            # 左键单击
        self.on_menu = None             # 右键菜单弹出前调用，可返回菜单状态

        self._taskbar_created = w32.user32.RegisterWindowMessageW("TaskbarCreated")
        self._menu_items = []           # [(id, 标签, 是否勾选|None)]
        self._commands: queue.Queue = queue.Queue()

    # ------------------------------------------------------------ 生命周期
    def start(self):
        self._thread = threading.Thread(target=self._run, name="tray", daemon=True)
        self._thread.start()
        self._ready.wait(5.0)
        return self._hwnd is not None

    def is_alive(self) -> bool:
        """托盘消息循环是否还在跑（供主线程判断退出）"""
        return bool(self._thread and self._thread.is_alive() and self._hwnd)

    def stop(self):
        self._running = False
        if self._hwnd:
            w32.user32.PostMessageW(self._hwnd, WM_APP_QUIT, 0, 0)

    def set_tooltip(self, text: str):
        if text == self._tooltip:
            return
        self._tooltip = text
        self._post(self._update_tooltip)

    def set_icon(self, builder=None):
        """重建图标。传 None 表示用构造时给的 builder"""
        if builder is not None:
            self._build_icon = builder
        self._post(self._update_icon)

    def show_notification(self, title: str, message: str):
        self._post(lambda: self._balloon(title, message))

    def set_menu(self, items):
        """items: [(id, 标签, checked|None)]，None 表示非勾选项"""
        self._menu_items = list(items)

    # ------------------------------------------------------------ 线程内部
    def _post(self, fn):
        self._commands.put(fn)
        if self._hwnd:
            w32.user32.PostMessageW(self._hwnd, WM_APP_REFRESH, 0, 0)

    def _drain_commands(self):
        while True:
            try:
                fn = self._commands.get_nowait()
            except queue.Empty:
                return
            try:
                fn()
            except Exception:
                pass

    def _run(self):
        self._wndproc_ref = WNDPROC(self._wndproc)      # 必须保持引用，否则被 GC
        hinstance = w32.kernel32.GetModuleHandleW(None)

        wc = w32.WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(w32.WNDCLASSEXW)
        wc.style = 0
        wc.lpfnWndProc = ctypes.cast(self._wndproc_ref, ctypes.c_void_p)
        wc.hInstance = hinstance
        wc.lpszClassName = self.CLASS_NAME
        w32.user32.RegisterClassExW(ctypes.byref(wc))

        # 顶层窗口（WS_POPUP 且父窗口为 0）：只有顶层窗口才收得到
        # TaskbarCreated 这类广播消息，消息专用窗口（HWND_MESSAGE）收不到。
        self._hwnd = w32.user32.CreateWindowExW(
            0, self.CLASS_NAME, self.CLASS_NAME, 0,
            0, 0, 0, 0, None, None, hinstance, None)
        if not self._hwnd:
            self._ready.set()
            return

        self._running = True
        self._update_icon()
        self._add()
        self._ready.set()

        msg = w32.MSG()
        while self._running:
            ret = w32.user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
            if ret in (0, -1):
                break
            if msg.message == WM_APP_QUIT:
                break
            w32.user32.TranslateMessage(ctypes.byref(msg))
            w32.user32.DispatchMessageW(ctypes.byref(msg))

        self._remove()
        self._destroy_icon()
        if self._hwnd:
            w32.user32.DestroyWindow(self._hwnd)
            self._hwnd = None

    # ------------------------------------------------------------ 窗口过程
    def _wndproc(self, hwnd, msg, wparam, lparam):
        try:
            if msg == WM_TRAYICON:
                self._on_tray_event(lparam)
                return 0
            if msg == self._taskbar_created:
                # explorer 重启：重新挂上图标
                self._add()
                return 0
            if msg == WM_APP_REFRESH:
                self._drain_commands()
                return 0
            if msg == WM_COMMAND:
                self._on_command(int(wparam) & 0xFFFF)
                return 0
            if msg == WM_CLOSE:
                self._running = False
                return 0
        except Exception:
            pass
        return w32.user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def _on_tray_event(self, event):
        event = int(event) & 0xFFFF
        if event == WM_LBUTTONUP:
            if self.on_click:
                self.on_click()
        elif event in (WM_RBUTTONUP, WM_CONTEXTMENU):
            self._show_menu()

    def _on_command(self, cmd: int):
        if self.on_menu:
            try:
                self.on_menu(cmd)
            except Exception:
                pass

    # ------------------------------------------------------------ 菜单
    def _show_menu(self):
        if not self._menu_items:
            return
        menu = w32.user32.CreatePopupMenu()
        if not menu:
            return
        try:
            for item_id, label, checked in self._menu_items:
                if label == "-":
                    w32.user32.AppendMenuW(menu, MF_SEPARATOR, 0, None)
                    continue
                flags = MF_STRING
                if checked is not None:
                    flags |= MF_CHECKED if checked else MF_UNCHECKED
                if item_id == CMD_OPEN_CARD:
                    flags |= MF_DEFAULT
                w32.user32.AppendMenuW(menu, flags, item_id, label)

            point = wt.POINT()
            w32.user32.GetCursorPos(ctypes.byref(point))
            # TrackPopupMenuEx 要求弹出菜单的宿主窗口是前台窗口，否则会不消失
            w32.user32.SetForegroundWindow(self._hwnd)
            index = w32.user32.TrackPopupMenuEx(
                menu,
                TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                point.x, point.y, self._hwnd, None)
            if index > 0:
                self._on_command(int(index))
        finally:
            w32.user32.DestroyMenu(menu)

    # ------------------------------------------------------------ 图标
    def _make_nid(self):
        nid = w32.NOTIFYICONDATAW()
        nid.cbSize = ctypes.sizeof(w32.NOTIFYICONDATAW)
        nid.hWnd = self._hwnd
        nid.uID = 1
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP
        nid.uCallbackMessage = WM_TRAYICON
        nid.hIcon = self._hicon or 0
        nid.szTip = self._tooltip[:127]
        return nid

    def _add(self):
        if not self._hwnd:
            return
        nid = self._make_nid()
        w32.shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))

    def _remove(self):
        if not self._hwnd:
            return
        nid = w32.NOTIFYICONDATAW()
        nid.cbSize = ctypes.sizeof(w32.NOTIFYICONDATAW)
        nid.hWnd = self._hwnd
        nid.uID = 1
        w32.shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))

    def _update_icon(self):
        if not self._hwnd:
            return
        new_icon = self._build_icon()
        if not new_icon:
            return
        old = self._hicon
        self._hicon = new_icon
        nid = self._make_nid()
        w32.shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(nid))
        if old:
            w32.user32.DestroyIcon(old)

    def _update_tooltip(self):
        if not self._hwnd:
            return
        nid = self._make_nid()
        w32.shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(nid))

    def _balloon(self, title: str, message: str):
        if not self._hwnd:
            return
        nid = self._make_nid()
        nid.uFlags = NIF_INFO
        nid.dwInfoFlags = NIIF_INFO
        nid.szInfo = message[:255]
        nid.szInfoTitle = title[:63]
        w32.shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(nid))

    def _destroy_icon(self):
        if self._hicon:
            w32.user32.DestroyIcon(self._hicon)
            self._hicon = None

    # ------------------------------------------------------------ 位置
    def icon_rect(self):
        """托盘图标的屏幕矩形 (left, top, right, bottom)；拿不到返回 None

        用 Shell_NotifyIconGetRect 而不是猜 Shell_TrayWnd 的坐标：图标可能
        被折叠进「^」浮出面板，也可能被用户拖到别的显示器上，只有系统自己
        知道它此刻在哪。
        """
        if not self._hwnd:
            return None
        identifier = w32.NOTIFYICONIDENTIFIER()
        identifier.cbSize = ctypes.sizeof(w32.NOTIFYICONIDENTIFIER)
        identifier.hWnd = self._hwnd
        identifier.uID = 1
        rect = wt.RECT()
        result = w32.shell32.Shell_NotifyIconGetRect(
            ctypes.byref(identifier), ctypes.byref(rect))
        if result != 0:
            return None
        return (rect.left, rect.top, rect.right, rect.bottom)
