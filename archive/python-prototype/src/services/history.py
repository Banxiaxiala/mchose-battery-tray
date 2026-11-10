# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [纯逻辑与存储分离] 分桶、斜率、文案全部是纯函数，必须能脱机单测；
   只有 History 类碰文件。新增算法先写 tests/test_history.py 的断言。
2. [样本不可篡改当前读数] 读不到时只回填 last_known，绝不许把历史值写回
   Reading.percent —— 那会让人以为设备还活着。
3. [编码] UTF-8 with BOM。

实现对齐 yixing233/logi-tray 的 BatteryService（两个参考项目里唯一有这块
功能的实现，FerrisPulse 没有）：

  * 样本 {t, percent, charging}
  * 保留 48 小时；但永远保留最新那一条 —— 它是「睡前还剩多少」的唯一来源
  * 落盘节流 60 秒；电量没变且 20 秒内刚记过就不重复记
  * 24 小时按小时分桶，无样本的时段沿用上一个已知值并标记为休眠柱
  * 预测用 24h 内放电样本的首尾斜率：剩余时间 = 当前电量 / rate
"""

from __future__ import annotations

import json
import os
import threading
import time
from pathlib import Path

from ..models import Analysis, Bucket, Forecast, Sample, format_age

# 保留窗口：只用于图表与预测，48 小时足够，也能防止文件无限增长
RETAIN_SECONDS = 48 * 3600
# 落盘节流。文件在 48 小时窗口下最大约 170 KB，一分钟一写无感，
# 好处是进程被强杀时最多只丢 1 分钟样本。
PERSIST_INTERVAL = 60
# 两个样本之间的最小间隔，避免密集轮询把文件撑大
MIN_SAMPLE_GAP = 20
# 预测窗口
PREDICT_WINDOW = 24 * 3600
# 斜率至少要跨这么久才算可信
MIN_PREDICT_SPAN = 0.5 * 3600
# 跨过这么久标「高置信度」
HIGH_CONFIDENCE_SPAN = 4 * 3600

DEFAULT_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "2.4G设备电量"


# ================================================================ 纯逻辑
def prune(samples: list[Sample], now: float) -> list[Sample]:
    """丢掉窗口外的旧样本，但永远保留最新那一条（睡前电量的唯一来源）"""
    cutoff = now - RETAIN_SECONDS
    kept = [s for s in samples if s.t >= cutoff]
    if not kept and samples:
        return [samples[-1]]
    if samples and kept and kept[-1] is not samples[-1]:
        kept.append(samples[-1])
    return kept


def build_buckets(samples: list[Sample], now: float,
                  current_percent: int | None = None,
                  last_known: int = -1) -> list[Bucket]:
    """24 根柱子，从「23 小时前」到「当前小时」"""
    ordered = sorted(samples, key=lambda s: s.t)
    this_hour = int(now // 3600) * 3600
    window_start = this_hour - 23 * 3600

    # 窗口起点之前的最后一个样本，作为第一根柱的延续基线
    baseline = -1
    for s in ordered:
        if s.t >= window_start:
            break
        baseline = s.percent
    if baseline <= 0:
        baseline = last_known if last_known > 0 else (current_percent or -1)

    buckets: list[Bucket] = []
    idx = 0
    running = baseline
    for i in range(24):
        start = this_hour - (23 - i) * 3600
        end = start + 3600
        count = 0
        value = running
        while idx < len(ordered) and ordered[idx].t < start:
            idx += 1
        j = idx
        while j < len(ordered) and ordered[j].t < end:
            count += 1
            value = ordered[j].percent
            j += 1
        if count > 0:
            running = value

        label = time.strftime("%H:00", time.localtime(start))
        buckets.append(Bucket(label, value, count, count == 0 and value > 0))
    return buckets


def forecast(samples: list[Sample], current_percent: int | None,
             charging: bool, now: float) -> Forecast:
    """用窗口内放电样本的首尾斜率算剩余时间"""
    if current_percent is None or current_percent <= 0:
        return Forecast(reason="设备离线")
    if charging:
        return Forecast(reason="充电中")

    window = [s for s in sorted(samples, key=lambda s: s.t)
              if s.t >= now - PREDICT_WINDOW and not s.charging and s.percent > 0]
    if len(window) < 2:
        return Forecast(reason="放电数据积累中", confidence="积累中")

    first, last = window[0], window[-1]
    span = last.t - first.t
    drop = first.percent - last.percent
    span_hours = span / 3600.0

    if span < MIN_PREDICT_SPAN or drop <= 0:
        return Forecast(reason="放电数据积累中", confidence="积累中",
                        span_hours=span_hours)

    rate = drop / span_hours
    if rate <= 0:
        return Forecast(reason="放电数据积累中", confidence="积累中",
                        span_hours=span_hours)

    return Forecast(
        hours=current_percent / rate,
        rate_per_hour=rate,
        confidence="高" if span > HIGH_CONFIDENCE_SPAN else "中",
        span_hours=span_hours,
    )


def analyze(samples: list[Sample], current_percent: int | None, charging: bool,
            now: float | None = None,
            last_sample: Sample | None = None) -> Analysis:
    """一次算完图表 + 预测 + 上下界"""
    now = time.time() if now is None else now
    ordered = sorted(samples, key=lambda s: s.t)

    result = Analysis(
        sample_count=len(ordered),
        last_known_percent=last_sample.percent if last_sample else -1,
        last_known_at=last_sample.t if last_sample else 0.0,
    )

    recent = [s for s in ordered if s.t >= now - PREDICT_WINDOW and s.percent > 0]
    if recent:
        result.min_percent = min(s.percent for s in recent)
        result.max_percent = max(s.percent for s in recent)

    result.buckets = build_buckets(ordered, now, current_percent,
                                   result.last_known_percent)
    result.active_hours = sum(1 for b in result.buckets if b.count > 0)
    result.forecast = forecast(ordered, current_percent, charging, now)
    return result


# ================================================================ 存储
class History:
    """一台设备一条历史文件；读写都带节流，失败不影响读数"""

    def __init__(self, device_key: str, directory: Path | None = None):
        safe = "".join(c if c.isalnum() or c in "-_" else "_" for c in device_key)
        self.path = Path(directory or DEFAULT_DIR) / f"history_{safe}.json"
        self._lock = threading.Lock()
        self._samples: list[Sample] | None = None
        self._last_persist = 0.0

    # ------------------------------------------------------------ 载入
    def _load(self) -> list[Sample]:
        if self._samples is not None:
            return self._samples

        samples: list[Sample] = []
        try:
            if self.path.exists():
                raw = json.loads(self.path.read_text(encoding="utf-8"))
                for item in raw.get("samples", []):
                    try:
                        percent = int(item["percent"])
                        if 0 < percent <= 100:
                            samples.append(Sample(float(item["t"]), percent,
                                                  bool(item.get("charging", False))))
                    except (KeyError, TypeError, ValueError):
                        continue
                samples.sort(key=lambda s: s.t)
        except Exception:
            samples = []          # 文件坏了不该影响读数，重新开始
        self._samples = samples
        return samples

    # ------------------------------------------------------------ 记录
    def add(self, percent: int | None, charging: bool, now: float | None = None):
        """记一笔；读不到电量时什么也不做"""
        if percent is None or not 0 < percent <= 100:
            return
        now = time.time() if now is None else now
        with self._lock:
            samples = self._load()
            if samples:
                last = samples[-1]
                if last.percent == percent and now - last.t < MIN_SAMPLE_GAP:
                    return
            samples.append(Sample(now, percent, bool(charging)))
            self._samples = prune(samples, now)
            if now - self._last_persist >= PERSIST_INTERVAL:
                self._persist(now)

    def flush(self, now: float | None = None):
        """强制落盘（退出前调用）"""
        now = time.time() if now is None else now
        with self._lock:
            self._persist(now)

    def last_sample(self) -> Sample | None:
        with self._lock:
            samples = self._load()
            return samples[-1] if samples else None

    def analyze(self, current_percent: int | None, charging: bool,
                now: float | None = None) -> Analysis:
        now = time.time() if now is None else now
        with self._lock:
            samples = self._load()
            return analyze(samples, current_percent, charging, now,
                           samples[-1] if samples else None)

    # ------------------------------------------------------------ 落盘
    def _persist(self, now: float):
        self._last_persist = now
        samples = self._samples or []
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            payload = {
                "version": 1,
                "samples": [
                    {"t": round(s.t, 3), "percent": s.percent, "charging": s.charging}
                    for s in samples
                ],
            }
            tmp = self.path.with_suffix(".tmp")
            # 不写 BOM：带 BOM 的 JSON 会让 Python 解析器直接报错
            tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=1),
                           encoding="utf-8")
            tmp.replace(self.path)
        except Exception:
            pass        # 写不进去只损失记忆，不影响读数


def describe_last_known(analysis: Analysis, now: float | None = None) -> str:
    """「上次 77% · 2 小时前」；无记忆返回空串"""
    return analysis.describe_last_known(now)


__all__ = [
    "History", "analyze", "build_buckets", "forecast", "prune",
    "describe_last_known", "format_age",
]
