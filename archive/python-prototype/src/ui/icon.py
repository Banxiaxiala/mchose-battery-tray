# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [零第三方依赖] 本文件不许 import PIL / numpy。抗锯齿靠「渲染到 4 倍大
   再用盒式滤波缩小」，纯 Python 足够 —— 图标只在读数变化时重绘一次。
2. [配色跟随主题] 深色任务栏用白线、浅色用深灰线，必须读注册表而不是写死。
3. [编码] UTF-8 with BOM。

图标语义：外圈=鼠标，内圈=耳机。
  * 平时纯白/纯黑，进度弧按百分比点亮
  * <=30% 变黄，<=15% 变红
  * 充电时整圈点亮并变绿
"""

from __future__ import annotations

import math
import winreg

from ..models import Reading

ICON_SIZE = 64
SUPERSAMPLE = 4

COLOR_RED = (255, 90, 90)
COLOR_AMBER = (255, 196, 66)
COLOR_GREEN = (86, 200, 120)
COLOR_LIGHT_FG = (32, 32, 32)
COLOR_DARK_FG = (255, 255, 255)

LOW_LIMIT = 15
MID_LIMIT = 30

# 外圈/内圈的几何参数（以 64px 逻辑尺寸为基准）
OUTER_BOX = (3, 3, ICON_SIZE - 4, ICON_SIZE - 4)
OUTER_WIDTH = 3.8
INNER_BOX = (19, 19, ICON_SIZE - 20, ICON_SIZE - 20)
INNER_WIDTH = 3.0


# ================================================================ 主题
def taskbar_is_light() -> bool:
    """任务栏是否浅色。读不到就按深色（白线）处理，与系统默认一致"""
    try:
        with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
            value, _ = winreg.QueryValueEx(key, "SystemUsesLightTheme")
            return bool(value)
    except OSError:
        return False


def foreground_color(is_light: bool | None = None) -> tuple[int, int, int]:
    if is_light is None:
        is_light = taskbar_is_light()
    return COLOR_LIGHT_FG if is_light else COLOR_DARK_FG


# ================================================================ 光栅
class Bitmap:
    """极简 RGBA 画布：只支持画圆环与圆弧（图标需要的全部）"""

    def __init__(self, size: int):
        self.size = size
        self.pixels = bytearray(size * size * 4)

    def set(self, x: int, y: int, rgba: tuple[float, float, float, float]):
        if not (0 <= x < self.size and 0 <= y < self.size):
            return
        offset = (y * self.size + x) * 4
        r, g, b, a = rgba
        if a <= 0:
            return
        dst = self.pixels
        if a >= 1.0:
            dst[offset] = min(255, max(0, int(r)))
            dst[offset + 1] = min(255, max(0, int(g)))
            dst[offset + 2] = min(255, max(0, int(b)))
            dst[offset + 3] = 255
            return
        # 简单的 source-over 合成
        inv = 1.0 - a
        dst[offset] = max(0, min(255, int(r * a + dst[offset] * inv)))
        dst[offset + 1] = max(0, min(255, int(g * a + dst[offset + 1] * inv)))
        dst[offset + 2] = max(0, min(255, int(b * a + dst[offset + 2] * inv)))
        dst[offset + 3] = min(255, int(255 * a + dst[offset + 3] * inv))

    def downscale(self, factor: int) -> "Bitmap":
        """盒式滤波缩小，得到抗锯齿效果"""
        target = Bitmap(self.size // factor)
        f2 = factor * factor
        for y in range(target.size):
            for x in range(target.size):
                r = g = b = a = 0
                for dy in range(factor):
                    base = ((y * factor + dy) * self.size + x * factor) * 4
                    for dx in range(factor):
                        o = base + dx * 4
                        a += self.pixels[o + 3]
                        if self.pixels[o + 3]:
                            r += self.pixels[o]
                            g += self.pixels[o + 1]
                            b += self.pixels[o + 2]
                cover = a // f2
                if cover == 0:
                    continue
                alpha = min(1.0, cover / 255.0)
                # 颜色按有覆盖的像素平均，避免边缘发灰
                denom = max(1, a // 255)
                target.set(x, y, (r / denom, g / denom, b / denom, alpha))
        return target


def _distance(x: float, y: float, cx: float, cy: float) -> float:
    return math.hypot(x - cx, y - cy)


def draw_ring(bitmap: Bitmap, box, width: float, color: tuple[int, int, int],
              alpha: float, start_deg: float = 0.0, sweep_deg: float = 360.0,
              scale: int = 1):
    """在 box 指定的椭圆上画一段圆环（alpha 控制淡色轨道）"""
    x0, y0, x1, y1 = (v * scale for v in box)
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    rx, ry = (x1 - x0) / 2.0, (y1 - y0) / 2.0
    half = (width * scale) / 2.0
    full = sweep_deg >= 359.9

    xs = max(0, int(cx - rx - half - 2))
    xe = min(bitmap.size, int(cx + rx + half + 3))
    ys = max(0, int(cy - ry - half - 2))
    ye = min(bitmap.size, int(cy + ry + half + 3))

    for y in range(ys, ye):
        for x in range(xs, xe):
            # 归一化到椭圆坐标系后求径向距离
            nx = (x + 0.5 - cx) / rx if rx else 0.0
            ny = (y + 0.5 - cy) / ry if ry else 0.0
            norm = math.hypot(nx, ny)
            if not (0.6 < norm < 1.5):
                continue
            # 到圆环中心线的距离（按平均半径换算回像素）
            avg_r = (rx + ry) / 2.0
            dist = abs(norm - 1.0) * avg_r
            cover = (half + 0.5 - dist)
            if cover <= 0:
                continue
            cover = min(1.0, cover)

            if not full:
                angle = math.degrees(math.atan2(ny, nx))
                # 图标里 -90 度是 12 点方向
                rel = (angle - (start_deg - 90.0) - 0.0) % 360.0
                if rel > sweep_deg:
                    continue

            bitmap.set(x, y, (color[0], color[1], color[2], alpha * cover))


def _arc_color(reading: Reading | None, fg: tuple[int, int, int]) -> tuple[int, int, int]:
    if reading is None:
        return fg
    if reading.charging:
        return COLOR_GREEN
    percent = reading.percent
    if percent is None:
        return fg
    if percent <= LOW_LIMIT:
        return COLOR_RED
    if percent <= MID_LIMIT:
        return COLOR_AMBER
    return fg


def render(mouse: Reading | None, headset: Reading | None,
           is_light: bool | None = None, size: int = ICON_SIZE) -> Bitmap:
    """渲染托盘图标，返回 size×size 的 RGBA 位图"""
    fg = foreground_color(is_light)
    big = Bitmap(size * SUPERSAMPLE)

    def ring(box, width, reading):
        color = _arc_color(reading, fg)
        # 淡色底环
        draw_ring(big, box, width, fg, 0.18, scale=SUPERSAMPLE)
        if reading is None:
            return
        if reading.charging:
            draw_ring(big, box, width, color, 1.0, scale=SUPERSAMPLE)
            return
        percent = reading.percent
        if percent and percent > 0:
            draw_ring(big, box, width, color, 1.0,
                      start_deg=0.0, sweep_deg=360.0 * min(percent, 100) / 100.0,
                      scale=SUPERSAMPLE)

    ring(OUTER_BOX, OUTER_WIDTH, mouse)
    ring(INNER_BOX, INNER_WIDTH, headset)
    return big.downscale(SUPERSAMPLE)


def to_bgra_bottom_up(bitmap: Bitmap) -> bytes:
    """转成 CreateDIBSection 需要的 BGRA 自底向上排列"""
    size = bitmap.size
    out = bytearray(size * size * 4)
    for y in range(size):
        src = (y * size) * 4
        dst = ((size - 1 - y) * size) * 4
        for x in range(size):
            s = src + x * 4
            d = dst + x * 4
            out[d] = bitmap.pixels[s + 2]      # B
            out[d + 1] = bitmap.pixels[s + 1]  # G
            out[d + 2] = bitmap.pixels[s]      # R
            out[d + 3] = bitmap.pixels[s + 3]  # A
    return bytes(out)
