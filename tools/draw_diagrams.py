#!/usr/bin/env py
# -*- coding: utf-8 -*-
"""
生成 README 用的架构示意图（PNG）。

为什么用代码画而不是 handcraft SVG：
项目零第三方依赖，README 图也不该依赖外部渲染服务。用 PIL 直接画，
结果可复现，改一行就能重生成。字体走系统自带的微软雅黑/等线。

用法：py -3.13 tools/draw_diagrams.py
"""

from __future__ import annotations

import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets")

# ---------------------------------------------------------------- 主题
BG = (13, 17, 23)
PANEL = (22, 27, 34)
PANEL2 = (32, 39, 47)
BORDER = (61, 68, 77)
TEXT = (230, 237, 243)
MUTED = (139, 148, 158)
FAINT = (110, 118, 129)
BLUE = (74, 144, 217)
PURPLE = (122, 91, 214)
GREEN = (46, 160, 67)
ORANGE = (219, 109, 40)
CYAN = (90, 200, 250)
RED = (240, 80, 80)

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\Deng.ttf",
    r"C:\Windows\Fonts\simhei.ttf",
]


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    """尽量用微软雅黑；缺字体时回退到默认（英文仍可读）。"""
    order = FONT_CANDIDATES[1:] + FONT_CANDIDATES[:1] if bold else FONT_CANDIDATES
    for path in order:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                continue
    return ImageFont.load_default()


def rounded(draw, box, radius, fill=None, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def text(draw, xy, s, size=12, color=TEXT, bold=False, anchor="la"):
    draw.text(xy, s, font=font(size, bold), fill=color, anchor=anchor)


def arrow(draw, x1, y1, x2, y2, color=FAINT, width=2):
    draw.line((x1, y1, x2, y2), fill=color, width=width)
    # 箭头三角
    if abs(y2 - y1) > abs(x2 - x1):
        d = 1 if y2 > y1 else -1
        draw.polygon([(x2, y2), (x2 - 5, y2 - 7 * d), (x2 + 5, y2 - 7 * d)], fill=color)
    else:
        d = 1 if x2 > x1 else -1
        draw.polygon([(x2, y2), (x2 - 7 * d, y2 - 5), (x2 - 7 * d, y2 + 5)], fill=color)


# ================================================================ 架构图
def draw_architecture() -> str:
    W, H = 900, 664
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    text(d, (26, 22), "分层架构 · 依赖严格单向", 17, TEXT, bold=True)
    text(d, (26, 46), "每条依赖只允许指向下方，下层不得 import 上层", 11, MUTED)

    layers = [
        # (y, h, 名称, 副标题, 强调色, [模块...])
        (68, 76, "Program.cs", "装配层 —— 只接线，不含协议字节与窗口布局", BLUE,
         [("BuildDevices()", "设备表唯一来源"), ("RefreshNow()", "刷新心跳"),
          ("SyncChartToCard()", "图表切换")]),
        (162, 116, "UI", "WPF · 亚克力卡片 · 自绘图表 · 托盘图标", PURPLE,
         [("BatteryCard", "亚克力窗口"), ("SparkChart", "柱状"), ("LineChart", "折线"),
          ("TrayIcon", "三环"), ("SettingsPanel", "设置"), ("Palette", "配色")]),
        (296, 108, "Services", "纯逻辑 + 文件持久化，可脱机单测", GREEN,
         [("History", "三层聚合 + 预测"), ("Settings", "便携目录"),
          ("AutoStart", "三重对齐"), ("TooltipText", "63 字符")]),
        (422, 108, "Hardware", "I/O 编排；Protocols 是零 I/O 的纯函数", ORANGE,
         [("Protocols", "三套协议"), ("Readers", "罗技/雷蛇"),
          ("Max102Reader", "双通道"), ("Tracer", "日志")]),
        (548, 76, "Core / Models", "Win32 唯一入口 · HID 常驻读 · 蓝牙属性 · 纯数据", CYAN,
         [("Win32", "全部 P/Invoke"), ("Hid", "挂起读"),
          ("BluetoothBattery", "两棵 PnP 树")]),
    ]

    for (y, h, name, sub, accent, mods) in layers:
        rounded(d, (26, y, W - 26, y + h), 10, fill=PANEL, outline=BORDER)
        # 左侧强调条
        d.rounded_rectangle((26, y + 8, 30, y + h - 8), radius=2, fill=accent)
        text(d, (44, y + 11), name, 14, TEXT, bold=True)
        text(d, (44, y + 31), sub, 10, MUTED)

        # 模块卡片：放在层名右侧，垂直居中于整层（避免压住副标题）
        n = len(mods)
        left = 268
        avail = (W - 40) - left
        gap = 7
        mw = (avail - gap * (n - 1)) / n
        mh = 42
        my = y + (h - mh) / 2
        for i, (label, note) in enumerate(mods):
            x = left + i * (mw + gap)
            rounded(d, (x, my, x + mw, my + mh), 6, fill=PANEL2, outline=accent)
            text(d, (x + 9, my + 8), label, 11, TEXT)
            text(d, (x + 9, my + 24), note, 10, MUTED)

    # 依赖箭头
    for (y, h, *_rest) in layers[:-1]:
        arrow(d, W // 2, y + h + 2, W // 2, y + h + 16)

    text(d, (26, H - 26),
         "Models 不依赖任何其它层  ·  Hardware 不得 import UI  ·  唯一外部依赖是构建期的 .NET SDK 8",
         11, FAINT)

    path = os.path.join(OUT, "architecture.png")
    img.save(path)
    return path


# ================================================================ 读取降级链
def draw_fallback() -> str:
    W, H = 900, 384
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    text(d, (26, 22), "读取降级链 · 每台设备自动选能用的通道", 17, TEXT, bold=True)
    text(d, (26, 46), "先试信息量大的私有协议，失败再回落系统 API；全程零轮询、零独占",
         11, MUTED)

    devices = [
        ("鼠标", "罗技 LIGHTSPEED", [
            ("HID++ 2.0", "特性 0x1004 → 0x1000 → 0x1001", GREEN),
            ("直连端点", "插着数据线时，同协议单槽位", (90, 140, 90)),
        ]),
        ("耳机", "雷蛇 BlackShark", [
            ("2.4G PA/PI", "64 字节报文：电量/充电/连接", ORANGE),
            ("蓝牙", "BTHENUM 电量属性，按 MAC 聚合", (150, 100, 60)),
        ]),
        ("键盘", "维咖 MAX102", [
            ("2.4G VIA", "cmd 20 + 连接状态与指示灯", PURPLE),
            ("蓝牙", "BTHLE 电量属性，DEV_ 节点", (100, 80, 150)),
        ]),
    ]

    x0 = 26
    colw = (W - 52 - 24) / 3
    for ci, (label, model, steps) in enumerate(devices):
        x = x0 + ci * (colw + 12)
        rounded(d, (x, 72, x + colw, H - 22), 10, fill=PANEL, outline=BORDER)
        text(d, (x + 14, 84), label, 13, TEXT, bold=True)
        text(d, (x + 14, 104), model, 10, MUTED)

        y = 140
        for si, (main, note, accent) in enumerate(steps):
            # 优先级徽标
            badge = ["首选", "回落"][si] if len(steps) > 1 else "唯一"
            bw = 36
            rounded(d, (x + 14, y, x + 14 + bw, y + 17), 8, fill=accent)
            text(d, (x + 14 + bw / 2, y + 9), badge, 9, (10, 14, 18),
                 bold=True, anchor="mm")

            text(d, (x + 58, y + 1), main, 12, TEXT, bold=True)
            text(d, (x + 58, y + 20), note, 10, MUTED)

            # 步骤之间的分隔线，暗示「失败才往下走」
            if si < len(steps) - 1:
                ly = y + 46
                d.line((x + 14, ly, x + colw - 14, ly), fill=(48, 54, 61), width=1)
                text(d, (x + 16, ly + 4), "↓ 读不到时回落", 9, FAINT)

            y += 84

        # 失败提示
        text(d, (x + 14, H - 54), "两条都失败时如实上报", 10, FAINT)
        text(d, (x + 14, H - 36), "绝不伪造 0%", 10, FAINT)

    path = os.path.join(OUT, "fallback-chain.png")
    img.save(path)
    return path


# ================================================================ 数据流
def draw_dataflow() -> str:
    W, H = 900, 330
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    text(d, (26, 22), "一次刷新的数据流", 17, TEXT, bold=True)
    text(d, (26, 46), "30 秒一次的心跳 · 读不到不覆盖当前值 · 陈旧读数不进历史",
         11, MUTED)

    boxes = [
        ("① 采集", "Readers / Protocols\nHID 或 PnP 属性", ORANGE, 180),
        ("② 判活", "ReadingDebounce\n连续 3 次失败才算离线", BLUE, 190),
        ("③ 入库", "History.Add\n跳变过滤 + 三层折叠", GREEN, 180),
        ("④ 呈现", "BatteryCard / TrayIcon\n图表 · 圆环 · 悬停提示", PURPLE, 180),
    ]

    x = 26
    y = 82
    for i, (title, body, accent, w) in enumerate(boxes):
        rounded(d, (x, y, x + w, y + 92), 10, fill=PANEL, outline=accent, width=2)
        text(d, (x + 14, y + 12), title, 13, accent, bold=True)
        for li, line in enumerate(body.split("\n")):
            text(d, (x + 14, y + 38 + li * 17), line, 11, TEXT if li == 0 else MUTED)
        if i < len(boxes) - 1:
            arrow(d, x + w + 3, y + 46, x + w + 22, y + 46, color=FAINT, width=2)
        x += w + 26

    # 下方两条护栏
    guards = [
        ("读不到 ≠ 0%", "Percent 保持 null，绝不拿历史值顶替"),
        ("离线 ≠ 立刻", "2.4G 空口会丢帧，单帧丢失不判离线"),
        ("陈旧值不入库", "蓝牙属性在离线后仍保留上次值"),
    ]
    gy = 206
    gw = (W - 52 - 24) / 3
    for i, (title, note) in enumerate(guards):
        gx = 26 + i * (gw + 12)
        rounded(d, (gx, gy, gx + gw, gy + 62), 8, fill=(28, 20, 20), outline=RED)
        # 画一个小三角代替 ⚠（字体缺该字形时会变成豆腐块）
        d.polygon([(gx + 14, gy + 20), (gx + 19, gy + 10), (gx + 24, gy + 20)],
                  fill=(255, 160, 160))
        text(d, (gx + 15.5, gy + 20), "!", 8, (28, 20, 20), bold=True, anchor="la")
        text(d, (gx + 32, gy + 10), title, 12, (255, 160, 160), bold=True)
        text(d, (gx + 12, gy + 32), note, 10, MUTED)

    text(d, (26, H - 22),
         "三处判据都由脱机断言锁住，改动前先看 docs/AGENTS.md 第 9 节",
         11, FAINT)

    path = os.path.join(OUT, "dataflow.png")
    img.save(path)
    return path


# ================================================================ 存储分层
def draw_storage() -> str:
    W, H = 900, 340
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    text(d, (26, 22), "历史数据：三层聚合", 17, TEXT, bold=True)
    text(d, (26, 46), "看得越久，分辨率越粗 —— 用 200 KB 换来 24 个月的趋势", 11, MUTED)

    rows = [
        ("原始样本", "48 小时", "20~30 秒 / 条", "~150 KB", "折线与 24h 柱状图", GREEN, 0.55),
        ("小时聚合", "30 天", "1 小时 / 条", "~30 KB", "7 天 / 30 天窗口", BLUE, 0.32),
        ("天聚合", "24 个月", "1 天 / 条", "~30 KB", "1 年 / 2 年窗口", PURPLE, 0.16),
    ]

    y = 82
    for name, span, step, size, usage, accent, ratio in rows:
        rounded(d, (26, y, W - 26, y + 66), 8, fill=PANEL, outline=BORDER)
        d.rounded_rectangle((26, y + 8, 30, y + 58), radius=2, fill=accent)
        text(d, (44, y + 10), name, 13, TEXT, bold=True)
        text(d, (190, y + 12), "保留 " + span, 11, MUTED)
        text(d, (300, y + 12), step, 11, MUTED)
        text(d, (470, y + 12), size, 11, MUTED)
        text(d, (560, y + 12), "→ " + usage, 11, accent)

        # 体积条（相对比例，纯示意）
        barw = 760 * ratio
        rounded(d, (44, y + 40, 44 + barw, y + 50), 4, fill=accent)

        y += 78

    text(d, (26, H - 24),
         "折叠必须发生在裁剪之前  ·  同一桶按样本数加权合并  ·  永远保留最新一条",
         11, FAINT)

    path = os.path.join(OUT, "storage-layers.png")
    img.save(path)
    return path


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    for fn in (draw_architecture, draw_fallback, draw_dataflow, draw_storage):
        print("生成", fn())
    return 0


if __name__ == "__main__":
    sys.exit(main())
