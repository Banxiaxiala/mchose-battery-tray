# -*- coding: utf-8 -*-
# ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
#
# 1. [只读遍历] 本脚本不修改任何源码，只输出文件树与关键统计，
#    用于交接前确认「该在的都在、不该在的都不在」。
# 2. [排除构建产物] bin/ obj/ dist/ 不参与统计，否则数字全是噪音。
# 3. [编码] UTF-8 with BOM。

from __future__ import annotations

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXCLUDE_DIRS = {"bin", "obj", "dist", "dist_sc", ".git", "__pycache__", ".vs"}
CS_EXT = (".cs",)
DOC_EXT = (".md",)


def walk():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDE_DIRS)
        for filename in sorted(filenames):
            yield os.path.join(dirpath, filename)


def count_lines(path):
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as handle:
            return sum(1 for _ in handle)
    except OSError:
        return 0


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    rows = []
    total_cs = total_doc = 0
    for path in walk():
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        lines = count_lines(path)
        rows.append((rel, lines))
        if path.endswith(CS_EXT):
            total_cs += lines
        elif path.endswith(DOC_EXT):
            total_doc += lines

    print("文件清单")
    print("=" * 66)
    for rel, lines in rows:
        print(f"  {lines:6d}  {rel}")

    print()
    print("=" * 66)
    print(f"  代码行数（.cs）      {total_cs}")
    print(f"  文档行数（.md）      {total_doc}")
    print(f"  文件总数             {len(rows)}")

    # 交接前自检：这几个文件缺了就是没准备好
    required = [
        "README.md", "ARCHITECTURE.md", "AGENTS.md", "HANDOVER.md",
        "build.bat", ".gitignore",
        "src-cs/PeripheralBattery.csproj",
        "src-cs/Program.cs",
        "src-cs.Tests/PeripheralBattery.Tests.csproj",
    ]
    missing = [r for r in required if not os.path.exists(os.path.join(ROOT, r))]
    print()
    if missing:
        print("缺失的交接必需文件：")
        for name in missing:
            print("  × " + name)
        return 1
    print("交接必需文件齐全")
    return 0


if __name__ == "__main__":
    sys.exit(main())
