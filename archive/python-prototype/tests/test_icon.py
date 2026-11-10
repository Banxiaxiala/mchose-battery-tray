# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [零依赖] 图标渲染测试不许 import PIL；用自带的 PNG 编码器判尺寸与像素。
2. [编码] UTF-8 with BOM。
"""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from src.models import Reading
from src.ui import icon as I


class IconRenderTests(unittest.TestCase):
    def test_size_and_channels(self):
        bitmap = I.render(Reading("PRO X 2", 52), Reading("BS", 48), is_light=False)
        self.assertEqual(bitmap.size, I.ICON_SIZE)
        self.assertEqual(len(bitmap.pixels), I.ICON_SIZE * I.ICON_SIZE * 4)

    def test_dark_theme_uses_white_foreground(self):
        self.assertEqual(I.foreground_color(is_light=False), I.COLOR_DARK_FG)
        self.assertEqual(I.foreground_color(is_light=True), I.COLOR_LIGHT_FG)

    def test_low_battery_is_red(self):
        red = I._arc_color(Reading("x", 10), I.COLOR_DARK_FG)
        self.assertEqual(red, I.COLOR_RED)

    def test_mid_battery_is_amber(self):
        amber = I._arc_color(Reading("x", 25), I.COLOR_DARK_FG)
        self.assertEqual(amber, I.COLOR_AMBER)

    def test_normal_battery_uses_foreground(self):
        fg = I._arc_color(Reading("x", 80), I.COLOR_DARK_FG)
        self.assertEqual(fg, I.COLOR_DARK_FG)

    def test_charging_overrides_color(self):
        charging = I._arc_color(Reading("x", 10, charging=True), I.COLOR_DARK_FG)
        self.assertEqual(charging, I.COLOR_GREEN)

    def test_none_reading_uses_foreground(self):
        self.assertEqual(I._arc_color(None, I.COLOR_DARK_FG), I.COLOR_DARK_FG)

    def test_pixels_non_empty_for_normal_reading(self):
        bitmap = I.render(Reading("x", 100), Reading("y", 100), is_light=False)
        opaque = sum(1 for i in range(3, len(bitmap.pixels), 4)
                     if bitmap.pixels[i] > 0)
        self.assertGreater(opaque, 200)

    def test_bgra_bottom_up_flips_rows(self):
        bitmap = I.Bitmap(2)
        # 顶行涂红，底行涂蓝
        bitmap.pixels[0:4] = bytes([255, 0, 0, 255])
        bitmap.pixels[4:8] = bytes([255, 0, 0, 255])
        bitmap.pixels[8:12] = bytes([0, 0, 255, 255])
        bitmap.pixels[12:16] = bytes([0, 0, 255, 255])
        out = I.to_bgra_bottom_up(bitmap)
        # 自底向上：第一行应是原来的底行（蓝 -> BGRA 里 B=255）
        self.assertEqual(out[0], 255)
        self.assertEqual(out[2], 0)
        # 最后一行应是原来的顶行（红 -> BGRA 里 R=255 在 [2]）
        self.assertEqual(out[12], 0)
        self.assertEqual(out[14], 255)

    def test_downscale_preserves_alpha(self):
        big = I.Bitmap(8)
        for i in range(0, len(big.pixels), 4):
            big.pixels[i:i + 4] = bytes([255, 255, 255, 255])
        small = big.downscale(4)
        self.assertEqual(small.size, 2)
        self.assertEqual(small.pixels[3], 255)


class ModelTests(unittest.TestCase):
    def test_reading_line_online(self):
        line = Reading("PRO X 2", 52, status="放电中").line("鼠标")
        self.assertIn("52%", line)
        self.assertIn("放电中", line)

    def test_reading_line_offline(self):
        line = Reading("PRO X 2", None, connected=False).line("鼠标")
        self.assertIn("--", line)
        self.assertIn("离线", line)

    def test_reading_line_forecast_only_when_connected(self):
        reading = Reading("x", 50, forecast="预计剩余 8 小时")
        self.assertIn("预计剩余", reading.line("鼠标"))
        reading.connected = False
        reading.forecast = ""
        self.assertNotIn("预计剩余", reading.line("鼠标"))

    def test_bucket_has_data(self):
        from src.models import Bucket
        self.assertTrue(Bucket("14:00", 50, 3, False).has_data)
        self.assertFalse(Bucket("14:00", 50, 0, True).has_data)
        self.assertFalse(Bucket("14:00", 0, 0, False).has_data)


if __name__ == "__main__":
    unittest.main(verbosity=2)
