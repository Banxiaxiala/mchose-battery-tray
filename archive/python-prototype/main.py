# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [分层依赖] 本文件是装配层，只允许「把各层接起来」，不许写协议字节、
   不许写窗口布局。所有逻辑都在 src/ 对应分层里。
2. [工作目录重置] 自启动时工作目录可能变成 C:\\Windows\\System32，
   启动首行必须切到程序所在目录（AGENT_RULES.md 第 5 条）。
3. [单实例] 用全局互斥量，第二次启动直接退出。
4. [编码] UTF-8 with BOM。

2.4G 外设电量托盘 —— 入口
------------------------------------------------
一个托盘图标，同时显示鼠标和耳机的电量，左键单击弹卡片看历史与续航预测。

支持的设备（协议均为真机实测）：
  * 罗技 LIGHTSPEED 接收器（HID++ 2.0）
  * 雷蛇无线耳机（64 字节 PA/PI 中断报文）
"""

from __future__ import annotations

import os
import sys

# 自启动时工作目录会落在 System32，先切回程序目录再 import
try:
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
except Exception:
    pass

import argparse
import ctypes
import threading
import time

APP_NAME = "2.4G设备电量"
MUTEX_NAME = "Global\\PeripheralBatteryTray"
REFRESH_SEC = 30
LOW_BATTERY = 10


def _setup_console():
    """Windows 控制台默认 GBK，中文会乱码；--noconsole 打包后没有控制台"""
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            try:
                setattr(sys, name, open(os.devnull, "w", encoding="utf-8"))
            except Exception:
                pass
            continue
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


# ---------------------------------------------------------------- 单实例
def acquire_single_instance():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    from ctypes import wintypes as wt
    kernel32.CreateMutexW.restype = wt.HANDLE
    handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
    return (ctypes.get_last_error() != 183), handle


# ---------------------------------------------------------------- 应用
class App:
    """把硬件读取、历史服务、托盘、卡片串起来"""

    def __init__(self):
        from src.hardware.logitech import LogitechReader
        from src.hardware.razer import RazerReader
        from src.services.history import History
        from src.services import autostart
        from src.ui.card_host import CardController
        from src.ui.icon import render
        from src.ui.tray import (TrayIcon, CMD_OPEN_CARD, CMD_REFRESH,
                                 CMD_AUTOSTART, CMD_QUIT)

        self._autostart = autostart
        self._cmd = {
            "open": CMD_OPEN_CARD, "refresh": CMD_REFRESH,
            "autostart": CMD_AUTOSTART, "quit": CMD_QUIT,
        }
        self._render = render

        self.logitech = LogitechReader()
        self.razer = RazerReader()
        self.mouse_history = History("logitech")
        self.headset_history = History("razer")

        self.mouse = None
        self.headset = None
        self.mouse_analysis = None
        self.headset_analysis = None
        self.mouse_error = ""
        self.headset_error = ""
        self._notified = {"mouse": False, "headset": False}
        self._lock = threading.Lock()
        self._running = True

        self.card = CardController(self._card_devices, self._card_anchor)

        self.tray = TrayIcon(f"{APP_NAME}\n正在读取…", self._build_hicon)
        self.tray.on_click = self.card.toggle
        self.tray.on_menu = self._on_menu
        self._refresh_menu()

    # ------------------------------------------------------------ 图标
    def _build_hicon(self):
        from src.core.win32 import create_icon_from_bgra
        from src.ui.icon import to_bgra_bottom_up
        bitmap = self._render(self.mouse, self.headset)
        return create_icon_from_bgra(bitmap.size, bitmap.size,
                                     to_bgra_bottom_up(bitmap))

    def _card_anchor(self):
        """托盘图标中心点；拿不到就让卡片自己放右下角"""
        rect = self.tray.icon_rect()
        if not rect:
            return None
        left, top, right, bottom = rect
        return ((left + right) // 2, (top + bottom) // 2)

    # ------------------------------------------------------------ 刷新
    def refresh(self):
        try:
            mouse, self.mouse_error = self.logitech.read()
        except Exception as exc:
            mouse, self.mouse_error = None, f"罗技读取异常：{exc}"
        try:
            headset, self.headset_error = self.razer.read()
        except Exception as exc:
            headset, self.headset_error = None, f"雷蛇读取异常：{exc}"

        # 只有真读到才记样本；读不到只回填 last_known，不改当前读数
        mouse_analysis = self.mouse_analysis
        headset_analysis = self.headset_analysis
        try:
            if mouse is not None:
                self.mouse_history.add(mouse.percent, mouse.charging)
            mouse_analysis = self.mouse_history.analyze(
                mouse.percent if mouse else None,
                bool(mouse and mouse.charging))
            self.mouse_analysis = mouse_analysis
            if mouse is not None:
                mouse.forecast = self._forecast_text(mouse_analysis)
        except Exception:
            pass
        try:
            if headset is not None:
                self.headset_history.add(headset.percent, headset.charging)
            headset_analysis = self.headset_history.analyze(
                headset.percent if headset else None,
                bool(headset and headset.charging))
            self.headset_analysis = headset_analysis
            if headset is not None:
                headset.forecast = self._forecast_text(headset_analysis)
        except Exception:
            pass

        with self._lock:
            if mouse is not None:
                self.mouse = mouse
            if headset is not None:
                self.headset = headset
            cur_mouse, cur_headset = self.mouse, self.headset

        if mouse is None and cur_mouse is not None:
            cur_mouse.connected = False
            cur_mouse.forecast = ""
        if headset is None and cur_headset is not None:
            cur_headset.connected = False
            cur_headset.forecast = ""

        self.tray.set_tooltip(self._tooltip(cur_mouse, cur_headset,
                                            mouse_analysis, headset_analysis))
        self.tray.set_icon(self._build_hicon)

        self._check_low(cur_mouse, "mouse")
        self._check_low(cur_headset, "headset")
        self.card.refresh()

    @staticmethod
    def _forecast_text(analysis):
        if analysis is None or analysis.forecast.hours is None:
            return ""
        return f"预计剩余 {analysis.forecast.text}"

    def _tooltip(self, mouse, headset, mouse_analysis, headset_analysis):
        lines = [APP_NAME]
        for reading, analysis, label, error in (
            (mouse, mouse_analysis, "鼠标", self.mouse_error),
            (headset, headset_analysis, "耳机", self.headset_error),
        ):
            if reading is not None:
                lines.append(reading.line(reading.name or label))
                continue
            line = error or f"{label}  --"
            if analysis is not None:
                known = analysis.describe_last_known()
                if known:
                    line += f"  ({known})"
            lines.append(line)
        return "\n".join(lines)

    # ------------------------------------------------------------ 提醒
    def _check_low(self, reading, key):
        if reading is None or reading.percent is None:
            return
        if reading.charging:
            self._notified[key] = False
            return
        if reading.percent <= LOW_BATTERY:
            if not self._notified.get(key):
                self._notified[key] = True
                self.tray.show_notification(
                    APP_NAME, f"{reading.name or key} 电量仅剩 {reading.percent}%")
        else:
            self._notified[key] = False

    # ------------------------------------------------------------ 卡片数据
    def _card_devices(self):
        from src.ui.card import CardDevice
        from src.services.history import analyze as analyze_samples

        with self._lock:
            mouse, headset = self.mouse, self.headset

        devices = []
        for reading, history, analysis, label in (
            (mouse, self.mouse_history, self.mouse_analysis, "鼠标"),
            (headset, self.headset_history, self.headset_analysis, "耳机"),
        ):
            if analysis is None:
                try:
                    analysis = history.analyze(
                        reading.percent if reading else None,
                        bool(reading and reading.charging))
                except Exception:
                    continue
            devices.append(CardDevice(
                label=label,
                name=reading.name if reading else "",
                percent=reading.percent if reading and reading.connected else None,
                charging=bool(reading and reading.charging and reading.connected),
                status=reading.status if reading else "",
                analysis=analysis,
            ))
        return devices

    # ------------------------------------------------------------ 菜单
    def _refresh_menu(self):
        self.tray.set_menu([
            (self._cmd["open"], "查看电量详情", None),
            (self._cmd["refresh"], "立即刷新", None),
            (self._cmd["autostart"], "开机自启",
             self._autostart.enabled()),
            ("-", "-", None),
            (self._cmd["quit"], "退出", None),
        ])

    def _on_menu(self, cmd):
        if cmd == self._cmd["open"]:
            self.card.toggle()
        elif cmd == self._cmd["refresh"]:
            threading.Thread(target=self.refresh, daemon=True).start()
        elif cmd == self._cmd["autostart"]:
            self._autostart.set_enabled(not self._autostart.enabled())
            self._refresh_menu()
        elif cmd == self._cmd["quit"]:
            self.shutdown()

    def shutdown(self):
        if not self._running:
            return
        self._running = False
        # 先让主线程的 tkinter 退出，再停托盘（托盘占了子线程）
        self.card.shutdown()
        for history in (self.mouse_history, self.headset_history):
            try:
                history.flush()
            except Exception:
                pass
        self.tray.stop()

    # ------------------------------------------------------------ 主循环
    def run(self):
        """主线程跑 tkinter，托盘与轮询都在子线程。

        不能让托盘占主线程、tkinter 占子线程：那样销毁 Tcl 解释器时会在
        错误的线程上释放 async handler，Python 直接以 0x80000003 崩溃
        （Tcl_AsyncDelete: async handler deleted by the wrong thread）。
        """
        if not self.tray.start():
            return 1
        threading.Thread(target=self._loop, daemon=True).start()

        # tkinter 的 mainloop 会阻塞在这里，直到 shutdown() 让它 quit()
        self.card.run()
        self._running = False
        return 0

    def _loop(self):
        while self._running:
            try:
                self.refresh()
            except Exception:
                pass
            for _ in range(REFRESH_SEC):
                if not self._running:
                    return
                time.sleep(1)


# ---------------------------------------------------------------- 命令行
def probe():
    from src.hardware.logitech import LogitechReader
    from src.hardware.razer import RazerReader

    _setup_console()
    print("设备探测")
    print("=" * 60)
    for title, reader in (("[罗技]", LogitechReader()), ("[雷蛇]", RazerReader())):
        print(f"\n{title}")
        try:
            reading, error = reader.read()
        except Exception as exc:
            reading, error = None, f"异常：{exc}"
        if reading:
            print(f"  {reading.name or '(未命名)'}")
            print(f"    协议    {reading.transport}")
            print(f"    电量    {reading.percent}%")
            state = reading.status or "未知"
            if reading.charging:
                state += "（充电中）"
            print(f"    状态    {state}")
            print(f"    在线    {'是' if reading.connected else '否'}")
        else:
            print(f"  读取失败：{error}")
    print()


def main():
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--probe", action="store_true",
                        help="只读一次并打印结果，不起托盘")
    args = parser.parse_args()

    if args.probe:
        probe()
        return 0

    _setup_console()
    ok, _handle = acquire_single_instance()
    if not ok:
        return 0

    return App().run()


if __name__ == "__main__":
    sys.exit(main())
