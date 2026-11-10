# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [tkinter 占用主线程] Tk 的创建、mainloop、销毁全部在主线程完成。
   托盘消息循环跑在子线程里（它只是 GetMessage，与 Tk 无关）。
   反过来做会踩两个坑：
     * 跨线程建 Tk 时 geometry 静默失效 —— 窗口落到屏幕左上角 (0,0)；
     * 跨线程销毁 Tcl 解释器 → Python 3.13+ 直接抛
       "Tcl_AsyncDelete: async handler deleted by the wrong thread"
       并让进程以 0x80000003 崩溃。
2. [指令队列] 其他线程一律通过 post() 投递指令，由主线程的 after() 循环取走。
3. [编码] UTF-8 with BOM。

卡片控制器：管理那唯一的卡片窗口
"""

from __future__ import annotations

import queue
import traceback


class CardController:
    """卡片窗口的唯一管理者。

    必须由主线程调用 run()（它内部跑 tkinter 的 mainloop）；
    其它线程只允许调用 toggle() / refresh() / shutdown()。
    """

    # 队列轮询间隔（毫秒）。60ms 对手感完全够用，又几乎不占 CPU。
    POLL_MS = 60

    def __init__(self, devices_provider, anchor_provider):
        """
        devices_provider: () -> list[CardDevice]
        anchor_provider:  () -> (x, y) | None
        """
        self._devices = devices_provider
        self._anchor = anchor_provider
        self._requests: queue.Queue = queue.Queue()
        self._root = None
        self._card = None
        self._alive = False

    # ------------------------------------------------------------ 对外
    def toggle(self):
        self._requests.put("toggle")

    def refresh(self):
        if self._card is not None:
            self._requests.put("refresh")

    def shutdown(self):
        self._requests.put("shutdown")

    # ------------------------------------------------------------ 主线程
    def run(self):
        """在主线程跑 Tk 事件循环，直到 shutdown()。返回后进程可以安全退出"""
        try:
            import tkinter as tk
        except Exception:
            traceback.print_exc()
            return

        root = tk.Tk()
        root.withdraw()
        try:
            root.tk.call("tk", "scaling", 1.0)
        except Exception:
            pass
        self._root = root
        self._alive = True

        def poll():
            if not self._alive:
                return
            try:
                while True:
                    self._handle(self._requests.get_nowait())
            except queue.Empty:
                pass
            except Exception:
                traceback.print_exc()
            if self._alive:
                try:
                    root.after(self.POLL_MS, poll)
                except Exception:
                    self._alive = False

        root.after(self.POLL_MS, poll)
        try:
            root.mainloop()
        finally:
            self._alive = False
            self._close_card()
            try:
                root.destroy()
            except Exception:
                pass
            self._root = None

    # ------------------------------------------------------------ 动作
    def _handle(self, action: str):
        if action == "shutdown":
            self._alive = False
            try:
                self._root.quit()
            except Exception:
                pass
            return
        if action == "toggle":
            if self._card is not None:
                self._close_card()
            else:
                self._open_card()
            return
        if action == "refresh":
            self._render_into_existing()

    def _open_card(self):
        try:
            from .card import BatteryCard
        except Exception:
            traceback.print_exc()
            return
        try:
            anchor = None
            try:
                anchor = self._anchor()
            except Exception:
                anchor = None
            self._card = BatteryCard(self._root, self._devices(), anchor=anchor)
        except Exception:
            traceback.print_exc()
            self._card = None

    def _close_card(self):
        if self._card is None:
            return
        try:
            self._card.dismiss()
        except Exception:
            pass
        self._card = None

    def _render_into_existing(self):
        if self._card is None:
            return
        try:
            self._card.render(self._devices())
            self._card.replace()
        except Exception:
            traceback.print_exc()
