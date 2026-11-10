# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [产物不进 Git] dist/ build/ release/ 全部在 .gitignore 里
   （AGENT_RULES.md 第 7 条「零二进制提交」）。
2. [Tcl/Tk 数据必须手动打包] Python 3.15 起，官方安装包把 Tcl/Tk 库压成
   tcl/libtcl9.0.4.zip 与 libtk9.0.4.zip，运行时挂在 //zipfs:/lib/tcl 上。
   PyInstaller 的 hook 仍按「找目录」的老逻辑走，会警告
   "Tcl library/data directory '//zipfs:/lib/tcl/tcl_library' does not exist"
   并把 tkinter 打包成坏包 —— 启动直接报
   "Tcl data directory .../_tcl_data not found"。
   所以这里主动把这些 zip 解出来，按 PyInstaller 期望的
   _tcl_data / _tk_data 名字一起塞进去。
3. [编码] UTF-8 with BOM。

用法：
    python tools/build.py            # 打包单文件 exe
    python tools/build.py --no-clean # 跳过 --clean，增量打包更快
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_NAME = "2.4G设备电量"

# PyInstaller 期望的运行时目录名
TCL_TARGET = "_tcl_data"
TK_TARGET = "_tk_data"


def _find_tcl_zips():
    """定位 Python 自带的 Tcl/Tk 库压缩包"""
    import tkinter
    base = os.path.dirname(tkinter.__file__)
    candidates = [
        os.path.join(os.path.dirname(base), "tcl"),
        os.path.join(os.path.dirname(sys.executable), "tcl"),
        os.path.join(os.path.dirname(sys.executable), "lib", "tcl"),
    ]
    for directory in candidates:
        if not os.path.isdir(directory):
            continue
        tcl_zip = tk_zip = None
        for name in os.listdir(directory):
            if name.startswith("libtcl") and name.endswith(".zip"):
                tcl_zip = os.path.join(directory, name)
            elif name.startswith("libtk") and name.endswith(".zip"):
                tk_zip = os.path.join(directory, name)
        if tcl_zip and tk_zip:
            return (tcl_zip, tk_zip)
    return (None, None)


def _unpack_tcl_tk(staging: str):
    """把 tcl/tk 的 zip 解成 _tcl_data / _tk_data，返回 datas 列表"""
    tcl_zip, tk_zip = _find_tcl_zips()
    if not tcl_zip or not tk_zip:
        return []

    if os.path.isdir(staging):
        shutil.rmtree(staging, ignore_errors=True)
    os.makedirs(staging, exist_ok=True)

    datas = []
    for archive, target in ((tcl_zip, TCL_TARGET), (tk_zip, TK_TARGET)):
        with zipfile.ZipFile(archive) as handle:
            handle.extractall(staging)
    # 解出来的顶层目录名是 tcl_library / tk_library，需要改名成
    # PyInstaller 运行时查找的 _tcl_data / _tk_data
    for source, target in (("tcl_library", TCL_TARGET), ("tk_library", TK_TARGET)):
        src = os.path.join(staging, source)
        if os.path.isdir(src):
            dst = os.path.join(staging, target)
            if os.path.isdir(dst):
                shutil.rmtree(dst, ignore_errors=True)
            os.rename(src, dst)
            datas.append((dst, target))
    return datas


def _posix(path: str) -> str:
    """把 Windows 反斜杠路径转成正斜杠。

    必须这么做：生成 .spec 时是把路径当字符串字面量写进去的，而 Windows
    路径里的反斜杠 + v/t/n/b 恰好都是 Python 转义序列 —— 例如
    F:\\LIST\\mchose-battery-tray\\version_info.txt 里的 "\\v" 会被解释成
    垂直制表符，PyInstaller 读 spec 时就报
    OSError: Invalid argument ... '\\x0bersion_info.txt'。
    正斜杠在 Windows 上完全可用，且没有任何转义风险。
    """
    return path.replace("\\", "/")


def _spec_file(datas):
    """生成一份 .spec，把 tcl/tk 数据显式声明进去"""
    entries = "\n".join(f"    ({_posix(d)!r}, {t!r})," for d, t in datas)
    return f"""# -*- mode: python ; coding: utf-8 -*-
# 本文件由 tools/build.py 自动生成，不要手改。
# Tcl/Tk 数据必须显式 datas 进去：Python 3.15 后它们被打包成 zip，
# PyInstaller 的 hook 找不到，会让 tkinter 在运行时崩掉。
a = Analysis(
    ['{_posix(os.path.join(ROOT, "main.py"))}'],
    pathex=['{_posix(ROOT)}'],
    binaries=[],
    datas=[
{entries}
    ],
    hiddenimports=['tkinter', '_tkinter'],
    hookspath=[],
    hooksconfig={{}},
    runtime_hooks=[],
    excludes=['numpy', 'PIL', 'matplotlib', 'PyQt5', 'PyQt6', 'pytest'],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='{APP_NAME}',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    runtime_tmpdir=None,
    console=False,
    icon=['{_posix(os.path.join(ROOT, "app.ico"))}'],
    version='{_posix(os.path.join(ROOT, "version_info.txt"))}',
)
"""


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="打包单文件 exe")
    parser.add_argument("--no-clean", action="store_true", help="跳过清理")
    args = parser.parse_args()

    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("缺少 PyInstaller，请先执行：pip install pyinstaller")
        return 1

    build_dir = os.path.join(ROOT, "build")
    staging = os.path.join(build_dir, "tcl_stage")
    datas = _unpack_tcl_tk(staging)
    if not datas:
        print("警告：没有找到 Tcl/Tk 数据包，打包出来的 exe 可能在起卡片时崩溃")

    spec_path = os.path.join(build_dir, f"{APP_NAME}.spec")
    os.makedirs(build_dir, exist_ok=True)
    with open(spec_path, "w", encoding="utf-8") as handle:
        handle.write(_spec_file(datas))

    command = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--distpath", os.path.join(ROOT, "dist"),
        "--workpath", build_dir,
        spec_path,
    ]
    if not args.no_clean:
        command.insert(3, "--clean")

    print("执行：" + " ".join(command))
    result = subprocess.run(command, cwd=ROOT)
    if result.returncode != 0:
        print("\n打包失败")
        return result.returncode

    exe = os.path.join(ROOT, "dist", f"{APP_NAME}.exe")
    if os.path.exists(exe):
        size = os.path.getsize(exe) / (1024 * 1024)
        print(f"\n产物：{exe}  ({size:.1f} MB)")
        return 0
    print("\n没有找到产物")
    return 1


if __name__ == "__main__":
    sys.exit(main())
