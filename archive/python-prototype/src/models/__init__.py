# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [契约同步更新] 改动本文件的数据结构时，必须同步更新 ARCHITECTURE.md
   与 tests/test_models.py 里的断言。
2. [纯数据] 本文件只允许放数据结构与纯换算，严禁出现任何 I/O、ctypes、
   tkinter、hid 调用 —— 它要能被单元测试无硬件导入。
3. [编码] 本文件保存为 UTF-8 with BOM。
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class Reading:
    """一台设备的一次读数。

    percent 为 None 表示**本轮没读到**，绝不拿历史值顶替 —— 「现在还有 77%」
    和「睡前还剩 77%」是两件事，混在一起会让人以为设备还活着。
    """

    name: str = ""
    percent: int | None = None
    charging: bool = False
    connected: bool = True
    status: str = ""            # 设备原始状态文本：充电中 / 放电中 / 已充满
    transport: str = ""         # 协议标识：HID++ / PA-PI
    forecast: str = ""          # 历史层算好的「预计剩余 8 小时」

    @property
    def known(self) -> bool:
        return self.percent is not None

    def line(self, label: str) -> str:
        """托盘悬停提示里的一行"""
        pct = "--" if self.percent is None else f"{self.percent}%"
        text = f"{label}  {pct}"
        if not self.connected:
            text += "  (离线)"
        elif self.charging:
            text += "  充电中"
        elif self.status:
            text += f"  {self.status}"
        if self.forecast and self.connected and not self.charging:
            text += f"  · {self.forecast}"
        return text


@dataclass
class Sample:
    """一个历史样本"""

    t: float            # Unix 时间戳（秒）
    percent: int
    charging: bool


@dataclass
class Bucket:
    """24 小时柱状图的一根柱"""

    hour: str           # "14:00"
    percent: int        # 该小时最后一个已知电量；<=0 表示无数据
    count: int          # 该小时内的样本数
    sleeping: bool      # 该小时没有任何样本（设备休眠 / 关机）

    @property
    def has_data(self) -> bool:
        return self.percent > 0 and not self.sleeping


@dataclass
class Forecast:
    """续航预测"""

    hours: float | None = None
    rate_per_hour: float = 0.0
    confidence: str = ""          # 高 / 中 / 积累中
    span_hours: float = 0.0
    reason: str = ""              # 无法预测时的原因

    @property
    def text(self) -> str:
        if self.hours is None:
            return self.reason or "放电数据积累中"
        if self.hours >= 24:
            return f"{int(self.hours // 24)} 天 {int(self.hours % 24)} 小时"
        if self.hours < 1:
            return f"{int(self.hours * 60)} 分钟"
        return f"{int(self.hours)} 小时"


@dataclass
class Analysis:
    """一次完整的分析结果"""

    buckets: list[Bucket] = field(default_factory=list)
    forecast: Forecast = field(default_factory=Forecast)
    min_percent: int = -1
    max_percent: int = -1
    active_hours: int = 0
    last_known_percent: int = -1
    last_known_at: float = 0.0
    sample_count: int = 0

    def describe_last_known(self, now: float | None = None) -> str:
        """「上次 77% · 2 小时前」；无记忆时返回空串（绝不编数字）"""
        if self.last_known_percent < 0 or not self.last_known_at:
            return ""
        age = format_age(self.last_known_at, now)
        return (f"上次 {self.last_known_percent}% · {age}" if age
                else f"上次 {self.last_known_percent}%")


def format_age(at: float, now: float | None = None) -> str:
    """相对时长。时钟回拨会算出负值，夹到 0 就是「刚刚」"""
    import time as _time
    now = _time.time() if now is None else now
    delta = max(0.0, now - at)
    if delta < 60:
        return "刚刚"
    if delta < 3600:
        return f"{int(delta // 60)} 分钟前"
    if delta < 86400:
        return f"{int(delta // 3600)} 小时前"
    return f"{int(delta // 86400)} 天前"
