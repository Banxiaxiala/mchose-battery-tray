# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [只做真机冒烟] 本脚本会真的打开 HID 设备读电量，不上传、不写注册表。
   它用来验证「换机器 / 换固件后协议是否还通」。
2. [编码] UTF-8 with BOM。

用法：
    python tools/smoke_test.py            # 读一次并打印
    python tools/smoke_test.py --loop 10  # 连续读 10 次，观察稳定性与耗时
"""

from __future__ import annotations

import argparse
import os
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from src.hardware.logitech import LogitechReader
from src.hardware.razer import RazerReader


def _print(title: str, reading, error: str, elapsed_ms: float):
    print(f"\n{title}  ({elapsed_ms:.0f}ms)")
    if not reading:
        print(f"  读取失败：{error}")
        return False
    state = reading.status or "未知"
    if reading.charging:
        state += "（充电中）"
    print(f"  名称    {reading.name or '(未命名)'}")
    print(f"  协议    {reading.transport}")
    print(f"  电量    {reading.percent}%")
    print(f"  状态    {state}")
    print(f"  在线    {'是' if reading.connected else '否'}")
    return True


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="真机冒烟测试")
    parser.add_argument("--loop", type=int, default=1, help="连续读取次数")
    args = parser.parse_args()

    logitech = LogitechReader()
    razer = RazerReader()
    ok_count = 0

    for index in range(max(1, args.loop)):
        if args.loop > 1:
            print(f"\n--- 第 {index + 1} 次 ---")

        start = time.perf_counter()
        try:
            reading, error = logitech.read()
        except Exception as exc:
            reading, error = None, f"异常：{exc}"
        if _print("[罗技]", reading, error,
                  (time.perf_counter() - start) * 1000):
            ok_count += 1

        start = time.perf_counter()
        try:
            reading, error = razer.read()
        except Exception as exc:
            reading, error = None, f"异常：{exc}"
        if _print("[雷蛇]", reading, error,
                  (time.perf_counter() - start) * 1000):
            ok_count += 1

    total = max(1, args.loop) * 2
    print()
    print("=" * 60)
    print(f"成功 {ok_count}/{total}")
    return 0 if ok_count == total else 1


if __name__ == "__main__":
    sys.exit(main())
