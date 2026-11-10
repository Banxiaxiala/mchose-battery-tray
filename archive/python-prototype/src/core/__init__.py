# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

Win32 底层封装层。约定：
  * 所有 ctypes 声明集中在 win32.py，其它文件不许再写 WinDLL。
  * hid.py 负责 HID 句柄与常驻挂起读。
  * 本层不依赖 hardware / services / ui。
"""
