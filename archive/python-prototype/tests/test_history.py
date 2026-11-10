# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [纯逻辑] 这些断言只测 services/history.py 的纯函数与文件读写，
   不许碰硬件、不许碰 UI。
2. [编码] UTF-8 with BOM。

历史与预测的回归用例。算法对齐 yixing233/logi-tray 的 BatteryService。
"""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from src.models import Sample, format_age
from src.services import history as H

NOW = 1_800_000_000.0


class ForecastTests(unittest.TestCase):
    def test_linear_discharge(self):
        # 4 小时从 90% 掉到 74%（每小时 4%），当前 74% -> 18.5 小时
        samples = [Sample(NOW - h * 3600, p, False) for h, p in
                   [(4, 90), (3, 86), (2, 81), (1, 78), (0, 74)]]
        f = H.forecast(samples, 74, False, NOW)
        self.assertAlmostEqual(f.hours, 18.5, places=2)
        self.assertAlmostEqual(f.rate_per_hour, 4.0, places=2)
        # 4 小时不算「大于 4 小时」，置信度是中
        self.assertEqual(f.confidence, "中")
        self.assertEqual(f.text, "18 小时")

    def test_high_confidence_over_four_hours(self):
        samples = [Sample(NOW - h * 3600, 90 + h, False) for h in range(5, -1, -1)]
        f = H.forecast(samples, 90, False, NOW)
        self.assertEqual(f.confidence, "高")
        self.assertAlmostEqual(f.hours, 90.0, places=1)

    def test_too_short_span(self):
        samples = [Sample(NOW - 1200, 80, False), Sample(NOW, 79, False)]
        f = H.forecast(samples, 79, False, NOW)
        self.assertIsNone(f.hours)
        self.assertEqual(f.confidence, "积累中")
        self.assertEqual(f.text, "放电数据积累中")

    def test_charging_and_offline(self):
        samples = [Sample(NOW - 3600, 80, False), Sample(NOW, 74, False)]
        self.assertEqual(H.forecast(samples, 74, True, NOW).text, "充电中")
        self.assertEqual(H.forecast(samples, -1, False, NOW).text, "设备离线")
        self.assertEqual(H.forecast(samples, None, False, NOW).text, "设备离线")

    def test_charging_samples_excluded(self):
        # 充电中的样本涨幅不是耗电，必须排除
        samples = [
            Sample(NOW - 5 * 3600, 40, False),
            Sample(NOW - 4 * 3600, 60, True),      # 充电中，应被忽略
            Sample(NOW - 3 * 3600, 80, True),
            Sample(NOW - 2 * 3600, 38, False),
            Sample(NOW - 1 * 3600, 36, False),
        ]
        f = H.forecast(samples, 36, False, NOW)
        # 跨度 4 小时（40% -> 36%），每小时 1%
        self.assertAlmostEqual(f.rate_per_hour, 1.0, places=2)

    def test_text_formats(self):
        from src.models import Forecast
        self.assertEqual(Forecast(hours=30.5).text, "1 天 6 小时")
        self.assertEqual(Forecast(hours=0.5).text, "30 分钟")
        self.assertEqual(Forecast(hours=5.4).text, "5 小时")


class BucketTests(unittest.TestCase):
    def test_twenty_four_buckets(self):
        samples = [Sample(NOW - h * 3600, 52 + h * 2, False) for h in range(24, -1, -1)]
        buckets = H.build_buckets(samples, NOW, 51, 51)
        self.assertEqual(len(buckets), 24)
        self.assertGreater(sum(1 for b in buckets if b.count > 0), 0)

    def test_sleeping_bucket_forward_fill(self):
        # 只给 3 个样本，其余时段应沿用上一个已知值并标记休眠
        samples = [Sample(NOW - 2 * 3600, 70, False),
                   Sample(NOW - 3600, 68, False)]
        buckets = H.build_buckets(samples, NOW, None, 68)
        self.assertTrue(any(b.sleeping for b in buckets))
        for b in buckets:
            if b.sleeping:
                self.assertGreater(b.percent, 0)

    def test_empty_buckets(self):
        buckets = H.build_buckets([], NOW, None, -1)
        self.assertEqual(len(buckets), 24)
        self.assertTrue(all(b.count == 0 for b in buckets))


class PruneTests(unittest.TestCase):
    def test_keeps_newest_always(self):
        old = [Sample(NOW - 100 * 3600, 55, False),
               Sample(NOW - 99 * 3600, 54, False)]
        kept = H.prune(old, NOW)
        self.assertEqual(len(kept), 1)
        self.assertEqual(kept[0].percent, 54)

    def test_drops_expired_keeps_recent(self):
        samples = [Sample(NOW - 100 * 3600, 55, False),
                   Sample(NOW - 2 * 3600, 60, False),
                   Sample(NOW - 60, 59, False)]
        kept = H.prune(samples, NOW)
        self.assertEqual(len(kept), 2)


class AnalyzeTests(unittest.TestCase):
    def test_min_max_and_last_known(self):
        samples = [Sample(NOW - 3 * 3600, 80, False),
                   Sample(NOW - 2 * 3600, 70, False),
                   Sample(NOW - 3600, 75, False)]
        a = H.analyze(samples, 74, False, NOW, samples[-1])
        self.assertEqual(a.min_percent, 70)
        self.assertEqual(a.max_percent, 80)
        self.assertEqual(a.last_known_percent, 75)
        self.assertIn("上次 75%", a.describe_last_known(NOW + 60))

    def test_offline_keeps_current_none(self):
        samples = [Sample(NOW - 3600, 75, False)]
        a = H.analyze(samples, None, False, NOW, samples[-1])
        # 当前读数为 None 时 forecast 必须说离线，绝不拿历史值冒充
        self.assertEqual(a.forecast.text, "设备离线")
        self.assertEqual(a.last_known_percent, 75)


class FormatAgeTests(unittest.TestCase):
    def test_buckets(self):
        self.assertEqual(format_age(NOW - 30, NOW), "刚刚")
        self.assertEqual(format_age(NOW - 300, NOW), "5 分钟前")
        self.assertEqual(format_age(NOW - 7200, NOW), "2 小时前")
        self.assertEqual(format_age(NOW - 200000, NOW), "2 天前")

    def test_clock_rollback_clamped(self):
        # 时钟回拨会算出负数，必须夹到「刚刚」而不是「-1 分钟前」
        self.assertEqual(format_age(NOW + 5000, NOW), "刚刚")


class HistoryStorageTests(unittest.TestCase):
    def test_persist_and_reload(self):
        with tempfile.TemporaryDirectory() as td:
            history = H.History("logitech:PRO X 2", td)
            for i in range(5):
                history.add(80 - i, False, NOW + i * 25)
            history.flush(NOW)

            reloaded = H.History("logitech:PRO X 2", td)
            analysis = reloaded.analyze(76, False, NOW + 200)
            self.assertEqual(analysis.sample_count, 5)
            self.assertEqual(analysis.last_known_percent, 76)

    def test_dedupe_same_value(self):
        with tempfile.TemporaryDirectory() as td:
            history = H.History("dup", td)
            for i in range(10):
                history.add(50, False, NOW + i * 2)
            self.assertEqual(len(history._samples), 1)

    def test_add_ignores_invalid(self):
        with tempfile.TemporaryDirectory() as td:
            history = H.History("bad", td)
            history.add(None, False, NOW)
            history.add(0, False, NOW)
            history.add(101, False, NOW)
            # _samples 是延迟加载的，用公开接口断言
            self.assertIsNone(history.last_sample())

    def test_corrupt_file_recovered(self):
        with tempfile.TemporaryDirectory() as td:
            path = os.path.join(td, "history_broken.json")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("{not json")
            history = H.History("broken", td)
            self.assertEqual(history.last_sample(), None)
            history.add(60, False, NOW)
            self.assertEqual(history.last_sample().percent, 60)

    def test_no_bom_in_file(self):
        # 带 BOM 的 JSON 会让解析器报错
        with tempfile.TemporaryDirectory() as td:
            history = H.History("bom", td)
            history.add(70, False, NOW)
            history.flush(NOW)
            with open(history.path, "rb") as handle:
                self.assertFalse(handle.read(3) == b"\xef\xbb\xbf")


if __name__ == "__main__":
    unittest.main(verbosity=2)
