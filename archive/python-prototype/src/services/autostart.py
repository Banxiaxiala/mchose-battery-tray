# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

自启动管理。按 AGENT_RULES.md 第 5 条「自启动三重对齐」实现：

  1. HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run
  2. HKCU\\...\\Explorer\\StartupApproved\\Run —— 清除任务管理器里的「已禁用」标志，
     否则第 1 条写了也会被系统跳过
  3. Shell:Startup 启动文件夹快捷方式，作为冗余保障

三重缺一：只写 Run 会被任务管理器禁用；只写启动文件夹在部分精简系统上不认。
本文件不依赖任何第三方库，全部用 winreg + PowerShell 建快捷方式。
"""

from __future__ import annotations

import os
import subprocess
import sys
import winreg
from pathlib import Path

APP_NAME = "2.4G设备电量"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
APPROVED_KEY = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
LNK_NAME = f"{APP_NAME}.lnk"


def _startup_dir() -> Path:
    return (Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu"
            / "Programs" / "Startup")


def _lnk_path() -> Path:
    return _startup_dir() / LNK_NAME


def _launch_command() -> str:
    """自启动时要执行的命令行"""
    if getattr(sys, "frozen", False):
        # 打包后 sys.executable 就是本程序，直接用
        return f'"{sys.executable}"'
    pyw = Path(sys.executable).with_name("pythonw.exe")
    target = pyw if pyw.exists() else Path(sys.executable)
    entry = Path(sys.argv[0]).resolve()
    return f'"{target}" "{entry}"'


# ---------------------------------------------------------------- 注册表
def run_key_enabled() -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, APP_NAME)
            return bool(value)
    except OSError:
        return False


def _write_run_key(enable: bool) -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0,
                            winreg.KEY_SET_VALUE) as key:
            if enable:
                winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, _launch_command())
            else:
                try:
                    winreg.DeleteValue(key, APP_NAME)
                except OSError:
                    pass
        return True
    except OSError:
        return False


def clear_startup_approved() -> None:
    """删掉 StartupApproved 里的「已禁用」标志，让 Run 项真正生效。

    任务管理器把启动项禁用时，会在这里写入一条二进制值；只删 Run 项不会清它，
    于是重新开启后系统仍然跳过本程序。
    """
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, APPROVED_KEY, 0,
                            winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE) as key:
            try:
                winreg.DeleteValue(key, APP_NAME)
            except OSError:
                pass
    except OSError:
        pass


# ---------------------------------------------------------------- 启动文件夹
def shortcut_enabled() -> bool:
    return _lnk_path().exists()


def _write_shortcut(enable: bool) -> bool:
    lnk = _lnk_path()
    try:
        if enable:
            lnk.parent.mkdir(parents=True, exist_ok=True)
            if getattr(sys, "frozen", False):
                target, arguments = sys.executable, ""
            else:
                pyw = Path(sys.executable).with_name("pythonw.exe")
                target = str(pyw if pyw.exists() else Path(sys.executable))
                arguments = f'"{Path(sys.argv[0]).resolve()}"'
            script = (
                "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnk}');"
                "$s.TargetPath='{target}';$s.Arguments='{args}';"
                "$s.WorkingDirectory='{wd}';$s.Save()"
            ).format(lnk=lnk, target=target, args=arguments,
                     wd=Path(target).parent)
            subprocess.run(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                creationflags=0x08000000, check=False, timeout=20)
        elif lnk.exists():
            lnk.unlink()
        return True
    except Exception:
        return False


# ---------------------------------------------------------------- 对外
def enabled() -> bool:
    """三重通道任一存在即视为已开启"""
    return run_key_enabled() or shortcut_enabled()


def set_enabled(enable: bool) -> bool:
    ok = _write_run_key(enable)
    if enable:
        clear_startup_approved()
    ok = _write_shortcut(enable) and ok
    return ok
