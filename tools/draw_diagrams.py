#!/usr/bin/env py
# -*- coding: utf-8 -*-
"""
生成 README 用的架构示意图。

设计：**先构造与渲染无关的图元列表，再分别输出 SVG 与 PNG**。

为什么这么做：
  * SVG 是主产物。README 在 GitHub 上由浏览器矢量渲染，放大不糊；
    PNG 只要换一次缩放或换台 DPI 更高的机器就得重出，维护成本高。
  * PNG 仍然保留（`--png`），供本地快速预览与不支持 SVG 的场景兜底。
  * 两个渲染器吃同一份图元，改设计只需要改一处，不会出现
    「SVG 改了 PNG 没改」这种两张图不一致的情况。

约束：本脚本是**构建期工具**，不是程序依赖。程序本身仍然零第三方依赖，
这里用 PIL 只是为了本地出 PNG。
"""

from __future__ import annotations

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets")

# ---------------------------------------------------------------- 主题
BG = "#0d1117"
PANEL = "#161b22"
PANEL2 = "#20272f"
BORDER = "#3d444d"
TEXT = "#e6edf3"
MUTED = "#8b949e"
FAINT = "#6e7681"
BLUE = "#4a90d9"
PURPLE = "#7a5bd6"
GREEN = "#2ea043"
ORANGE = "#db6d28"
CYAN = "#5ac8fa"
RED = "#f05050"
PINK = "#ffa0a0"

# GitHub 上用系统字体渲染，不内嵌字体（内嵌会让文件大一倍，且中文字体有授权问题）
FONT_STACK = "'Microsoft YaHei UI','PingFang SC','Hiragino Sans GB','Segoe UI',sans-serif"


# ================================================================ 图元
class Scene:
    """与渲染无关的画布：只记录图元，不关心输出格式。"""

    def __init__(self, width: int, height: int):
        self.width = width
        self.height = height
        self.items: list[tuple] = []

    def rect(self, x, y, w, h, fill=None, stroke=None, width=1, r=0):
        self.items.append(("rect", x, y, w, h, fill, stroke, width, r))

    def text(self, x, y, s, size=12, color=TEXT, bold=False, anchor="start"):
        self.items.append(("text", x, y, s, size, color, bold, anchor))

    def line(self, x1, y1, x2, y2, color=FAINT, width=2):
        self.items.append(("line", x1, y1, x2, y2, color, width))

    def arrow(self, x1, y1, x2, y2, color=FAINT, width=2):
        """竖直线段 + 箭头三角（示意图里只用到垂直与水平两种）"""
        self.line(x1, y1, x2, y2, color, width)
        if abs(y2 - y1) >= abs(x2 - x1):
            d = 1 if y2 > y1 else -1
            self.items.append(("poly", [(x2, y2), (x2 - 5, y2 - 8 * d),
                                        (x2 + 5, y2 - 8 * d)], color))
        else:
            d = 1 if x2 > x1 else -1
            self.items.append(("poly", [(x2, y2), (x2 - 8 * d, y2 - 5),
                                        (x2 - 8 * d, y2 + 5)], color))


# ================================================================ SVG 渲染
def render_svg(scene: Scene) -> str:
    out = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{scene.width}" '
        f'height="{scene.height}" viewBox="0 0 {scene.width} {scene.height}" '
        f'font-family="{FONT_STACK}">',
        # 固定深色底：GitHub 用 <img> 引用 SVG 时不会下发 prefers-color-scheme，
        # 主题自适应在 README 里不可靠，索性固定一套，在深浅两种背景下都成立。
        f'<rect width="{scene.width}" height="{scene.height}" fill="{BG}"/>',
    ]

    for item in scene.items:
        kind = item[0]

        if kind == "rect":
            _, x, y, w, h, fill, stroke, sw, r = item
            attrs = [f'x="{_n(x)}"', f'y="{_n(y)}"', f'width="{_n(w)}"',
                     f'height="{_n(h)}"']
            attrs.append(f'fill="{fill}"' if fill else 'fill="none"')
            if stroke:
                attrs.append(f'stroke="{stroke}"')
                attrs.append(f'stroke-width="{_n(sw)}"')
            if r:
                attrs.append(f'rx="{_n(r)}"')
            out.append("<rect " + " ".join(attrs) + "/>")

        elif kind == "text":
            _, x, y, s, size, color, bold, anchor = item
            # SVG 的 y 是基线，PIL 的 y 是顶端；这里 text 统一按「顶端」传，
            # 渲染时 +size*0.82 近似转成基线，让两种输出的视觉位置一致。
            baseline = y + size * 0.82
            anchor_attr = {
                "start": "start", "middle": "middle", "end": "end",
            }.get(anchor, "start")
            weight = ' font-weight="600"' if bold else ""
            out.append(
                f'<text x="{_n(x)}" y="{_n(baseline)}" font-size="{_n(size)}" '
                f'fill="{color}" text-anchor="{anchor_attr}"{weight}>'
                f'{_esc(s)}</text>')

        elif kind == "line":
            _, x1, y1, x2, y2, color, width = item
            out.append(f'<line x1="{_n(x1)}" y1="{_n(y1)}" x2="{_n(x2)}" '
                       f'y2="{_n(y2)}" stroke="{color}" stroke-width="{_n(width)}"/>')

        elif kind == "poly":
            _, points, Fill = item
            pts = " ".join(f"{_n(px)},{_n(py)}" for px, py in points)
            out.append(f'<polygon points="{pts}" fill="{Fill}"/>')

    out.append("</svg>")
    return "\n".join(out)


def _n(value: float) -> str:
    """数字格式化：去掉无意义的 .0，减小文件体积"""
    if abs(value - round(value)) < 1e-6:
        return str(int(round(value)))
    return f"{value:.2f}".rstrip("0").rstrip(".")


def _esc(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


# ================================================================ PNG 渲染
def render_png(scene: Scene, path: str) -> None:
    try:
        from PIL import Image, ImageDraw, ImageFont
    except Exception as exc:                       # pragma: no cover
        print("跳过 PNG（缺 PIL）：" + str(exc))
        return

    def rgb(hex_color: str):
        h = hex_color.lstrip("#")
        return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))

    font_path = None
    for candidate in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\Deng.ttf",
                      r"C:\Windows\Fonts\simhei.ttf"):
        if os.path.exists(candidate):
            font_path = candidate
            break

    def font(size, bold=False):
        if font_path:
            try:
                return ImageFont.truetype(font_path, int(round(size)))
            except Exception:
                pass
        return ImageFont.load_default()

    img = Image.new("RGB", (scene.width, scene.height), rgb(BG))
    d = ImageDraw.Draw(img)

    for item in scene.items:
        kind = item[0]
        if kind == "rect":
            _, x, y, w, h, fill, stroke, sw, r = item
            d.rounded_rectangle((x, y, x + w, y + h), radius=r,
                                fill=rgb(fill) if fill else None,
                                outline=rgb(stroke) if stroke else None,
                                width=max(1, int(sw)))
        elif kind == "text":
            _, x, y, s, size, color, bold, anchor = item
            d.text((x, y), s, font=font(size, bold), fill=rgb(color),
                   anchor={"start": "la", "middle": "ma", "end": "ra"}[anchor])
        elif kind == "line":
            _, x1, y1, x2, y2, color, width = item
            d.line((x1, y1, x2, y2), fill=rgb(color), width=max(1, int(width)))
        elif kind == "poly":
            _, points, Fill = item
            d.polygon(points, fill=rgb(Fill))

    img.save(path)


# ================================================================ 场景：架构
def scene_architecture() -> Scene:
    W, H = 900, 664
    s = Scene(W, H)

    s.text(26, 22, "分层架构 · 依赖严格单向", 17, TEXT, bold=True)
    s.text(26, 46, "每条依赖只允许指向下方，下层不得 import 上层", 11, MUTED)

    layers = [
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
         [("DeviceCatalog", "设备表"), ("Protocols", "三套协议"),
          ("Readers", "罗技/雷蛇"), ("Max102Reader", "双通道")]),
        (548, 76, "Core / Models", "Win32 唯一入口 · HID 常驻读 · 蓝牙属性 · 纯数据", CYAN,
         [("Win32", "全部 P/Invoke"), ("Hid", "挂起读"),
          ("BluetoothBattery", "两棵 PnP 树")]),
    ]

    for (y, h, name, sub, accent, mods) in layers:
        s.rect(26, y, W - 52, h, fill=PANEL, stroke=BORDER, r=10)
        s.rect(26, y + 8, 4, h - 16, fill=accent, r=2)
        s.text(44, y + 11, name, 14, TEXT, bold=True)
        s.text(44, y + 31, sub, 10, MUTED)

        n = len(mods)
        left = 268
        gap = 7
        avail = (W - 40) - left
        mw = (avail - gap * (n - 1)) / n
        mh = 42
        my = y + (h - mh) / 2
        for i, (label, note) in enumerate(mods):
            x = left + i * (mw + gap)
            s.rect(x, my, mw, mh, fill=PANEL2, stroke=accent, r=6)
            s.text(x + 9, my + 8, label, 11, TEXT)
            s.text(x + 9, my + 24, note, 10, MUTED)

    for (y, h, *_rest) in layers[:-1]:
        s.arrow(W // 2, y + h + 2, W // 2, y + h + 16)

    s.text(26, H - 26,
           "Models 不依赖任何其它层  ·  Hardware 不得 import UI  ·  "
           "唯一外部依赖是构建期的 .NET SDK 8", 11, FAINT)
    return s


# ================================================================ 场景：降级链
def scene_fallback() -> Scene:
    W, H = 900, 384
    s = Scene(W, H)

    s.text(26, 22, "读取降级链 · 每台设备自动选能用的通道", 17, TEXT, bold=True)
    s.text(26, 46, "先试信息量大的私有协议，失败再回落系统 API；全程零轮询、零独占",
           11, MUTED)

    devices = [
        ("鼠标", "罗技 LIGHTSPEED", [
            ("HID++ 2.0", "特性 0x1004 → 0x1000 → 0x1001", GREEN),
            ("直连端点", "插着数据线时，同协议单槽位", "#5a8c5a"),
        ]),
        ("耳机", "雷蛇 BlackShark", [
            ("2.4G PA/PI", "64 字节报文：电量/充电/连接", ORANGE),
            ("蓝牙", "BTHENUM 电量属性，按 MAC 聚合", "#96643c"),
        ]),
        ("键盘", "维咖 MAX102", [
            ("2.4G VIA", "cmd 20 + 连接状态与指示灯", PURPLE),
            ("蓝牙", "BTHLE 电量属性，DEV_ 节点", "#645096"),
        ]),
    ]

    x0 = 26
    colw = (W - 52 - 24) / 3
    for ci, (label, model, steps) in enumerate(devices):
        x = x0 + ci * (colw + 12)
        s.rect(x, 72, colw, H - 94, fill=PANEL, stroke=BORDER, r=10)
        s.text(x + 14, 84, label, 13, TEXT, bold=True)
        s.text(x + 14, 104, model, 10, MUTED)

        y = 140
        for si, (main, note, accent) in enumerate(steps):
            badge = ["首选", "回落"][si] if len(steps) > 1 else "唯一"
            s.rect(x + 14, y, 36, 17, fill=accent, r=8)
            s.text(x + 32, y + 3, badge, 9, "#0a0e12", bold=True, anchor="middle")

            s.text(x + 58, y + 1, main, 12, TEXT, bold=True)
            s.text(x + 58, y + 20, note, 10, MUTED)

            if si < len(steps) - 1:
                ly = y + 46
                s.line(x + 14, ly, x + colw - 14, ly, "#30363d", 1)
                s.text(x + 16, ly + 4, "↓ 读不到时回落", 9, FAINT)

            y += 84

        s.text(x + 14, H - 54, "两条都失败时如实上报", 10, FAINT)
        s.text(x + 14, H - 36, "绝不伪造 0%", 10, FAINT)
    return s


# ================================================================ 场景：数据流
def scene_dataflow() -> Scene:
    W, H = 900, 330
    s = Scene(W, H)

    s.text(26, 22, "一次刷新的数据流", 17, TEXT, bold=True)
    s.text(26, 46, "30 秒一次的心跳 · 读不到不覆盖当前值 · 陈旧读数不进历史",
           11, MUTED)

    boxes = [
        ("① 采集", ["Readers / Protocols", "HID 或 PnP 属性"], ORANGE, 180),
        ("② 判活", ["ReadingDebounce", "连续 3 次失败才算离线"], BLUE, 190),
        ("③ 入库", ["History.Add", "跳变过滤 + 三层折叠"], GREEN, 180),
        ("④ 呈现", ["BatteryCard / TrayIcon", "图表 · 圆环 · 悬停提示"], PURPLE, 180),
    ]

    x = 26
    y = 82
    for i, (title, body, accent, w) in enumerate(boxes):
        s.rect(x, y, w, 92, fill=PANEL, stroke=accent, width=2, r=10)
        s.text(x + 14, y + 12, title, 13, accent, bold=True)
        for li, line in enumerate(body):
            s.text(x + 14, y + 38 + li * 17, line, 11, TEXT if li == 0 else MUTED)
        if i < len(boxes) - 1:
            s.arrow(x + w + 3, y + 46, x + w + 22, y + 46)
        x += w + 26

    guards = [
        ("读不到 ≠ 0%", "Percent 保持 null，绝不拿历史值顶替"),
        ("离线 ≠ 立刻", "2.4G 空口会丢帧，单帧丢失不判离线"),
        ("陈旧值不入库", "蓝牙属性在离线后仍保留上次值"),
    ]
    gy = 206
    gw = (W - 52 - 24) / 3
    for i, (title, note) in enumerate(guards):
        gx = 26 + i * (gw + 12)
        s.rect(gx, gy, gw, 62, fill="#1c1414", stroke=RED, r=8)
        # 画一个小三角代替 ⚠（字体缺该字形时会渲染成豆腐块）
        s.items.append(("poly", [(gx + 14, gy + 20), (gx + 19, gy + 10),
                                 (gx + 24, gy + 20)], PINK))
        s.text(gx + 15.5, gy + 11, "!", 8, "#1c1414", bold=True)
        s.text(gx + 32, gy + 10, title, 12, PINK, bold=True)
        s.text(gx + 12, gy + 32, note, 10, MUTED)

    s.text(26, H - 22, "三处判据都由脱机断言锁住，改动前先看 docs/AGENTS.md 第 9 节",
           11, FAINT)
    return s


# ================================================================ 场景：存储分层
def scene_storage() -> Scene:
    W, H = 900, 340
    s = Scene(W, H)

    s.text(26, 22, "历史数据：三层聚合", 17, TEXT, bold=True)
    s.text(26, 46, "看得越久，分辨率越粗 —— 用 200 KB 换来 24 个月的趋势", 11, MUTED)

    rows = [
        ("原始样本", "48 小时", "20~30 秒 / 条", "~150 KB", "折线与 24h 柱状图", GREEN, 0.55),
        ("小时聚合", "30 天", "1 小时 / 条", "~30 KB", "7 天 / 30 天窗口", BLUE, 0.32),
        ("天聚合", "24 个月", "1 天 / 条", "~30 KB", "1 年 / 2 年窗口", PURPLE, 0.16),
    ]

    y = 82
    for name, span, step, size, usage, accent, ratio in rows:
        s.rect(26, y, W - 52, 66, fill=PANEL, stroke=BORDER, r=8)
        s.rect(26, y + 8, 4, 50, fill=accent, r=2)
        s.text(44, y + 10, name, 13, TEXT, bold=True)
        s.text(190, y + 12, "保留 " + span, 11, MUTED)
        s.text(300, y + 12, step, 11, MUTED)
        s.text(470, y + 12, size, 11, MUTED)
        s.text(560, y + 12, "→ " + usage, 11, accent)
        s.rect(44, y + 40, 760 * ratio, 10, fill=accent, r=4)
        y += 78

    s.text(26, H - 24,
           "折叠必须发生在裁剪之前  ·  同一桶按样本数加权合并  ·  永远保留最新一条",
           11, FAINT)
    return s


SCENES = {
    "architecture": scene_architecture,
    "fallback-chain": scene_fallback,
    "dataflow": scene_dataflow,
    "storage-layers": scene_storage,
}


def main(argv: list[str]) -> int:
    os.makedirs(OUT, exist_ok=True)
    want_png = "--png" in argv

    for name, builder in SCENES.items():
        scene = builder()
        svg_path = os.path.join(OUT, name + ".svg")
        with open(svg_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(render_svg(scene))
        size = os.path.getsize(svg_path)
        print(f"{svg_path}  ({size / 1024:.1f} KB)")

        if want_png:
            png_path = os.path.join(OUT, name + ".png")
            render_png(scene, png_path)
            print(f"  └─ {png_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
