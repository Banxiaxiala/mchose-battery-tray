# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [零硬件] 本脚本只跑 tests/ 下的脱机断言，不打开任何 HID 句柄、
   不弹任何窗口。真机验证请用 tools/smoke_test.py。
2. [编码] UTF-8 with BOM。

用法：
    python tools/run_all_tests.py
退出码 0 = 全部通过。
"""

from __future__ import annotations

import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    loader = unittest.TestLoader()
    suite = loader.discover(os.path.join(ROOT, "tests"), top_level_dir=ROOT)
    result = unittest.TextTestRunner(verbosity=2).run(suite)

    print()
    print("=" * 60)
    print(f"共 {result.testsRun} 项，失败 {len(result.failures)}，"
          f"错误 {len(result.errors)}")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
