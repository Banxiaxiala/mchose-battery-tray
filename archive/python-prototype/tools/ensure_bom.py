# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [用途] 按 AGENT_RULES.md 第 3 条「文件编码安全」，源码必须统一编码。
   本项目是 Python，约定用 **UTF-8 with BOM**（与两个参考项目一致，
   也让 Windows 记事本打开时不乱码）。
2. [安全] 只处理白名单目录里的 .py/.md/.txt，默认只报告不改写；
   加 --write 才真正写入。
3. [编码] 本文件自身 UTF-8 with BOM。

用法：
    python tools/ensure_bom.py           # 只检查，缺 BOM 就列出来并返回 1
    python tools/ensure_bom.py --write   # 补齐 BOM
"""

from __future__ import annotations

import argparse
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 只处理这些目录；dist/build 是构建产物，不该动
TARGET_DIRS = ("src", "tests", "tools", "docs")
TARGET_ROOT_FILES = ("main.py", "README.md", "ARCHITECTURE.md", "AGENTS.md")
TARGET_EXT = (".py", ".md", ".txt")
BOM = b"\xef\xbb\xbf"


def iter_files():
    for name in TARGET_ROOT_FILES:
        path = os.path.join(ROOT, name)
        if os.path.isfile(path):
            yield path
    for directory in TARGET_DIRS:
        base = os.path.join(ROOT, directory)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d != "__pycache__"]
            for filename in filenames:
                if filename.endswith(TARGET_EXT):
                    yield os.path.join(dirpath, filename)


def has_bom(path: str) -> bool:
    with open(path, "rb") as handle:
        return handle.read(3) == BOM


def add_bom(path: str) -> None:
    with open(path, "rb") as handle:
        data = handle.read()
    if data.startswith(BOM):
        return
    with open(path, "wb") as handle:
        handle.write(BOM + data)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="检查/补齐 UTF-8 BOM")
    parser.add_argument("--write", action="store_true", help="真正写入 BOM")
    args = parser.parse_args()

    missing = []
    for path in iter_files():
        try:
            if not has_bom(path):
                missing.append(path)
                if args.write:
                    add_bom(path)
        except OSError:
            continue

    relative = [os.path.relpath(p, ROOT) for p in missing]
    if not missing:
        print("所有源码文件都带 UTF-8 BOM")
        return 0

    if args.write:
        for name in relative:
            print(f"已补 BOM: {name}")
        print(f"\n共补齐 {len(missing)} 个文件")
        return 0

    for name in relative:
        print(f"缺少 BOM: {name}")
    print(f"\n共 {len(missing)} 个文件缺少 BOM，加 --write 可自动补齐")
    return 1


if __name__ == "__main__":
    sys.exit(main())
