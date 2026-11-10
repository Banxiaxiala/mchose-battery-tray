# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [只做 I/O 编排] 报文构造与解析一律调用 protocols.py 的纯函数。
2. [禁止 SetOutputReport] 雷蛇固件会拒绝 HidD_SetOutputReport（错误码 87），
   必须走 WriteFile 写中断 OUT 端点。改成 Feature/SetOutput 会直接读不到数。
3. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

import time

from ..core.hid import HidDevice, enumerate_all
from ..models import Reading
from . import protocols as P


def _query(device: HidDevice, cmd: int, timeout: float = 0.6):
    """发一条命令并等到匹配的应答；返回 value 字节或 None"""
    try:
        device.write(P.build_razer_frame(cmd), timeout=0.3)
    except Exception:
        return None

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        frame = device.read(P.RAZER_REPORT_LEN, timeout=0.05)
        if not frame:
            continue
        value = P.parse_razer_reply(frame, cmd)
        if value is not None:
            return value
    return None


class RazerReader:
    """雷蛇 2.4G 无线耳机"""

    def read(self):
        """返回 (Reading|None, 错误信息)"""
        interfaces = [
            iface for iface in enumerate_all(P.RAZER_VENDOR)
            if iface.usage_page == P.RAZER_USAGE_PAGE
        ]
        if not interfaces:
            return (None, "没有找到雷蛇 2.4G 接口")

        # 同一 PID 可能暴露多个集合，优先 usage 0x0001（私有通道）
        interfaces.sort(key=lambda i: (i.product_id, i.usage != 0x0001))

        last_error = "雷蛇设备没有应答"
        for iface in interfaces:
            try:
                device = HidDevice(iface.path)
            except Exception as exc:
                last_error = f"打开雷蛇接口失败：{exc}"
                continue
            try:
                battery = _query(device, P.RAZER_CMD_BATTERY)
                if battery is None or not 0 <= battery <= 100:
                    last_error = "雷蛇电量查询无应答（耳机可能已休眠）"
                    continue

                charging = _query(device, P.RAZER_CMD_CHARGING)
                connection = _query(device, P.RAZER_CMD_CONNECTION)

                return (Reading(
                    name=P.razer_model_name(iface.product, iface.product_id),
                    percent=battery,
                    charging=bool(charging),
                    connected=connection != 0,
                    status="充电中" if charging else "放电中",
                    transport="PA-PI",
                ), "")
            finally:
                device.close()
        return (None, last_error)
