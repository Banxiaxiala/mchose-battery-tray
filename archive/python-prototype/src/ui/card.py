# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [只用 tkinter] 不许引入 matplotlib / PyQt 等第三方 GUI 库，
   单文件 exe 的体积与启动时间都靠这条守住。
2. [定位同步执行] 见下方「位置固定规则」，不得改回 after() 延后定位。
3. [编码] UTF-8 with BOM。

电量卡片窗口
------------------------------------------------
左键单击托盘图标弹出来的无边框小卡片：大号电量、充放电状态、
续航预测、24 小时柱状图。

用 tkinter（Python 自带，不额外引入依赖）。刻意不引入 matplotlib ——
只需要 24 根柱，自己用 Canvas 画几十行就够，还能精确控制配色。

排版参照 yixing233/logi-tray 的 lite/DetailsForm.cs：
  * 大号数字 22pt，右侧状态列独立成列，避免「100%」挤掉状态文字
  * 离线时大号数字收窄成「--」，右侧改说「上次 77% · 2 小时前」
  * 柱状图底部留时间刻度；低电量区间抬升基线放大差异
  * 休眠柱半透明

⚠️ 位置固定规则（这是用户报的「窗口都在左上角」的根因）：

   1. 几何位置只在「窗口建好之后」同步设置一次，并且必须先用
      update_idletasks() 让 tk 算出真实高度。用 after(N, place) 延后设置
      会被后续 pack 的布局重算覆盖，窗口就落到 (0,0)。
   2. 更有决定性的原因：早先每点一次托盘就在**新线程**里创建 Tk()。
      tkinter 的 Tk() 与 mainloop 必须在同一个线程里，跨线程创建时
      geometry 常常静默失效。现在由 card_host.CardController 把 tkinter
      钉在唯一一个专属线程里（见 card_host.py 的说明）。
   3. 锚点来自 Shell_NotifyIconGetRect，而不是猜任务栏窗口坐标 ——
      图标被折叠进「^」或拖到副屏时只有系统自己知道它在哪。
"""

from __future__ import annotations

import tkinter as tk
from dataclasses import dataclass

from ..models import Analysis

# ---------------------------------------------------------------- 配色
_TEXT = "#1F2328"
_TEXT_DIM = "#6B7280"
_TEXT_FAINT = "#9CA3AF"
_CARD = "#FFFFFF"
_BORDER = "#E5E7EB"
_TRACK = "#EDEFF2"
_ACCENT = "#2F6FEB"

_COLOR_RED = "#F87171"
_COLOR_AMBER = "#F0B429"
_COLOR_GREEN = "#4ADE80"

LOW_LIMIT = 15
MID_LIMIT = 30

FONT_UI = "Microsoft YaHei UI"

# 卡片宽度；高度由内容决定
CARD_WIDTH = 300
# 卡片与托盘图标之间的间隙
ANCHOR_GAP = 10
# 离屏幕边缘的最小留白
SCREEN_MARGIN = 8


def battery_color(percent: int, charging: bool = False, known: bool = True) -> str:
    if not known:
        return _TEXT_FAINT
    if charging:
        return _COLOR_GREEN
    if percent <= LOW_LIMIT:
        return _COLOR_RED
    if percent <= MID_LIMIT:
        return _COLOR_AMBER
    return _ACCENT


@dataclass
class CardDevice:
    """一张卡片要显示的一台设备"""

    label: str                  # 「鼠标」/「耳机」
    name: str                   # 「PRO X 2」
    percent: int | None
    charging: bool
    status: str
    analysis: Analysis


# ---------------------------------------------------------------- 柱状图
class Chart(tk.Canvas):
    """24 小时电量柱状图"""

    BAR_W = 7
    GAP = 3
    SCALE_BAND = 16

    def __init__(self, master, **kw):
        kw.setdefault("bg", _CARD)
        kw.setdefault("highlightthickness", 0)
        kw.setdefault("width", 240)
        super().__init__(master, height=88, **kw)
        self._buckets = []
        self.bind("<Configure>", lambda e: self.redraw())

    def set_buckets(self, buckets):
        self._buckets = buckets or []
        self.redraw()

    def redraw(self):
        self.delete("all")
        w = self.winfo_width()
        h = self.winfo_height()
        if w <= 1:
            return

        if not self._buckets:
            self.create_text(2, h // 2, text="暂无历史记录", anchor="w",
                             fill=_TEXT_FAINT, font=(FONT_UI, 8))
            return

        baseline = h - self.SCALE_BAND
        chart_h = max(10, baseline - 2)

        # 低电量区间（20..45）抬升基线放大差异，与 logi-tray 规则一致
        values = [b.percent for b in self._buckets if b.has_data]
        min_p = min(values) if values else 0
        max_p = max(values) if values else 0
        base_line = max(0, min_p - 15) if (max_p - min_p <= 25 and min_p >= 20) else 0
        span = max(1, 100 - base_line)

        total_w = len(self._buckets) * (self.BAR_W + self.GAP) - self.GAP
        x = max(0, (w - total_w) / 2)

        for b in self._buckets:
            if b.has_data:
                norm = min(max((b.percent - base_line) / span, 0.10), 1.0)
                bar_h = min(max(chart_h * norm, 3.0), chart_h)
                color = _COLOR_FAINT if b.sleeping else battery_color(b.percent)
                self.create_rectangle(x, baseline - bar_h, x + self.BAR_W, baseline,
                                      fill=color, outline="")
            else:
                # 无数据：画一条 2px 的横线占位
                self.create_rectangle(x, baseline - 2, x + self.BAR_W, baseline,
                                      fill=_TRACK, outline="")
            x += self.BAR_W + self.GAP

        self.create_text(0, baseline + 2, text="-24h", anchor="nw",
                         fill=_TEXT_FAINT, font=(FONT_UI, 7))
        self.create_text(w, baseline + 2, text="现在", anchor="ne",
                         fill=_TEXT_FAINT, font=(FONT_UI, 7))


# ---------------------------------------------------------------- 设备面板
class DevicePanel(tk.Frame):
    def __init__(self, master, device: CardDevice):
        super().__init__(master, bg=_CARD)
        d = device
        pad = 16

        # ---- 第一行：标签 + 型号 ----
        head = tk.Frame(self, bg=_CARD)
        head.pack(fill="x", padx=pad, pady=(pad, 0))
        tk.Label(head, text=d.label, bg=_CARD, fg=_TEXT_DIM,
                 font=(FONT_UI, 8)).pack(side="left")
        model = f"  {d.name}" if d.name else ""
        tk.Label(head, text=model, bg=_CARD, fg=_TEXT_FAINT,
                 font=(FONT_UI, 8)).pack(side="left")

        # ---- 第二行：大号电量 + 状态列 ----
        body = tk.Frame(self, bg=_CARD)
        body.pack(fill="x", padx=pad, pady=(2, 0))

        online = d.percent is not None and d.percent > 0
        big = f"{d.percent}%" if online else "--"
        big_color = battery_color(d.percent, d.charging) if online else _TEXT_FAINT
        tk.Label(body, text=big, bg=_CARD, fg=big_color,
                 font=(FONT_UI, 22, "bold"), width=4 if online else 2,
                 anchor="w").pack(side="left")

        right = tk.Frame(body, bg=_CARD)
        right.pack(side="left", fill="x", expand=True, pady=(6, 0))

        if online:
            status = "充电中" if d.charging else (d.status or "放电中")
            tk.Label(right, text=status, bg=_CARD, fg=_TEXT,
                     font=(FONT_UI, 9), anchor="w").pack(fill="x")

            forecast = d.analysis.forecast
            if forecast.hours is not None:
                conf = f" · 置信 {forecast.confidence}" if forecast.confidence else ""
                line, color = f"预计剩余 {forecast.text}{conf}", _TEXT_DIM
            else:
                line, color = forecast.text, _TEXT_FAINT
            tk.Label(right, text=line, bg=_CARD, fg=color,
                     font=(FONT_UI, 8), anchor="w").pack(fill="x")
        else:
            tk.Label(right, text="已离线", bg=_CARD, fg=_TEXT,
                     font=(FONT_UI, 9), anchor="w").pack(fill="x")
            sub = d.analysis.describe_last_known()
            if sub:
                tk.Label(right, text=sub, bg=_CARD, fg=_TEXT_FAINT,
                         font=(FONT_UI, 8), anchor="w").pack(fill="x")

        # ---- 第三行：24 小时区间 ----
        a = d.analysis
        if a.min_percent >= 0 and a.max_percent >= 0:
            scope = (f"24 小时区间 {a.min_percent}%"
                     if a.min_percent == a.max_percent
                     else f"24 小时区间 {a.min_percent}% – {a.max_percent}%")
            if a.forecast.rate_per_hour:
                scope += f"   · 每小时 {a.forecast.rate_per_hour:.1f}%"
            if a.active_hours:
                scope += f"   · 在线 {a.active_hours}h"
        else:
            scope = "24 小时区间 暂无数据"
        tk.Label(self, text=scope, bg=_CARD, fg=_TEXT_FAINT,
                 font=(FONT_UI, 7), anchor="w").pack(fill="x", padx=pad, pady=(4, 2))

        chart = Chart(self)
        chart.pack(fill="x", padx=pad, pady=(0, pad))
        chart.set_buckets(a.buckets)


# ---------------------------------------------------------------- 卡片窗口
class BatteryCard(tk.Toplevel):
    """无边框卡片；重复 render 时原地更新，不重建窗口"""

    def __init__(self, master, devices: list[CardDevice], anchor=None):
        super().__init__(master)
        self._anchor = anchor
        self.overrideredirect(True)
        try:
            self.attributes("-topmost", True)
        except Exception:
            pass

        outer = tk.Frame(self, bg=_BORDER, padx=1, pady=1)
        outer.pack(fill="both", expand=True)
        self._body = tk.Frame(outer, bg=_CARD)
        self._body.pack(fill="both", expand=True)

        bar = tk.Frame(self._body, bg=_CARD)
        bar.pack(fill="x", padx=16, pady=(12, 0))
        tk.Label(bar, text="2.4G 设备电量", bg=_CARD, fg=_TEXT,
                 font=(FONT_UI, 10, "bold")).pack(side="left")
        close = tk.Label(bar, text="\u2715", bg=_CARD, fg=_TEXT_FAINT,
                         font=(FONT_UI, 9), cursor="hand2")
        close.pack(side="right")
        close.bind("<Button-1>", lambda e: self.dismiss())
        # overrideredirect 窗口没有标题栏，允许拖动
        for widget in (bar, self._body):
            widget.bind("<Button-1>", self._start_drag)
            widget.bind("<B1-Motion>", self._on_drag)

        self._content = tk.Frame(self._body, bg=_CARD)
        self._content.pack(fill="both", expand=True)

        self.render(devices)
        self.bind("<Escape>", lambda e: self.dismiss())
        # 建好之后立刻定位。必须同步，不能延后（见文件头的说明）
        self.replace()

    # ------------------------------------------------------------ 内容
    def render(self, devices: list[CardDevice]):
        for child in self._content.winfo_children():
            child.destroy()
        for i, d in enumerate(devices):
            if i:
                tk.Frame(self._content, bg=_BORDER, height=1).pack(fill="x", padx=16)
            DevicePanel(self._content, d).pack(fill="x")

    # ------------------------------------------------------------ 定位
    def replace(self):
        """按内容重新算大小，并贴到托盘图标正上方"""
        self.update_idletasks()
        w = CARD_WIDTH
        h = self._content.winfo_reqheight() + self._header_height()
        x, y = self._target_position(w, h)
        self.geometry(f"{w}x{h}+{x}+{y}")
        self.update_idletasks()
        try:
            self.lift()
        except Exception:
            pass

    def _header_height(self) -> int:
        try:
            return self._body.winfo_children()[0].winfo_reqheight() + 12
        except Exception:
            return 34

    def _target_position(self, w: int, h: int):
        """优先贴在托盘图标正上方；拿不到锚点就贴屏幕右下角"""
        try:
            sw = self.winfo_screenwidth()
            sh = self.winfo_screenheight()
        except Exception:
            return (200, 200)

        ax, ay = self._anchor if self._anchor else (0, 0)
        if ax and ay:
            # 图标中心对齐卡片右边，与系统气泡的方向一致
            x = ax - w // 2
            y = ay - h - ANCHOR_GAP
        else:
            x = sw - w - SCREEN_MARGIN - 12
            y = sh - h - 60

        x = min(max(SCREEN_MARGIN, x), sw - w - SCREEN_MARGIN)
        y = min(max(SCREEN_MARGIN, y), sh - h - SCREEN_MARGIN)
        return (x, y)

    # ------------------------------------------------------------ 拖动
    def _start_drag(self, event):
        self._drag_origin = (event.x_root, event.y_root,
                             self.winfo_rootx(), self.winfo_rooty())

    def _on_drag(self, event):
        origin = getattr(self, "_drag_origin", None)
        if not origin:
            return
        ox, oy, wx, wy = origin
        self.geometry(f"+{wx + event.x_root - ox}+{wy + event.y_root - oy}")

    def dismiss(self):
        try:
            self.destroy()
        except Exception:
            pass
