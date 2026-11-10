# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [只做 I/O 编排] 本文件负责「把字节发出去、把应答收回来」，
   所有字节布局必须调用 src/hardware/protocols.py 里的纯函数，严禁在此
   硬编码偏移量。
2. [两遍探测] 槽位探测必须保留「轻探 + 耐心等」两遍：第一遍靠短集合的
   错误帧毫秒级排除空槽位，第二遍只对真正超时的槽位给足唤醒窗口。
   改成单遍会让唤醒中的设备永远等不到应答。
3. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

import time

from ..core.hid import HidDevice, PendingRead, enumerate_all
from ..models import Reading
from . import protocols as P


class LogitechReceiver:
    """一个 LIGHTSPEED 接收器：短集合 + 长集合"""

    def __init__(self, short_iface, long_iface):
        self.key = (long_iface.vendor_id, long_iface.product_id, long_iface.serial)
        self.short = HidDevice(short_iface.path)
        self.long = HidDevice(long_iface.path)
        # 常驻挂起读：读请求一直挂在驱动里，3~5ms 的回绝帧才不会漏
        self._short_pending = PendingRead(self.short, short_iface.input_len or 7)
        self._long_pending = PendingRead(self.long, long_iface.input_len or 20)
        self._short_pending.start()
        self._long_pending.start()

    def close(self):
        for obj in (self._short_pending, self._long_pending):
            try:
                obj.cancel()
            except Exception:
                pass
        for dev in (self.short, self.long):
            try:
                dev.close()
            except Exception:
                pass

    # ------------------------------------------------------------ 底层
    def _drain(self):
        for pending in (self._long_pending, self._short_pending):
            for _ in range(16):
                if not pending.poll(0.0):
                    break

    def _xfer(self, device_index: int, feature_index: int, function: int,
              params: bytes = b"", timeout: float = P.QUICK_TIMEOUT):
        """返回 (状态, 数据区)。状态：ok / absent / timeout"""
        self._drain()
        self.long.write(P.build_logi_request(device_index, feature_index,
                                             function, params))

        deadline = time.monotonic() + timeout
        while True:
            left = deadline - time.monotonic()
            if left <= 0:
                return ("timeout", b"")

            # 先看短集合：不可达时接收器 3~5ms 就在这里回绝
            frame = self._short_pending.poll(0.0)
            if frame and P.match_logi_absent(frame, device_index, feature_index, function):
                return ("absent", b"")

            frame = self._long_pending.poll(min(left, 0.02))
            if frame:
                payload = P.match_logi_reply(frame, device_index, feature_index, function)
                if payload is not None:
                    return ("ok", payload)

    def _call(self, device_index: int, feature_index: int, function: int,
              params: bytes = b"", timeout: float = P.QUICK_TIMEOUT):
        state, payload = self._xfer(device_index, feature_index, function,
                                    params, timeout)
        return payload if state == "ok" else None

    # ------------------------------------------------------------ 特性
    def feature_index(self, device_index: int, feature_id: int,
                      timeout: float = P.QUICK_TIMEOUT):
        """查特性索引；0 表示该设备不支持。设备不在该槽位时抛 LookupError"""
        state, payload = self._xfer(device_index, P.FEAT_ROOT, 0x00,
                                    bytes([feature_id >> 8, feature_id & 0xFF]),
                                    timeout)
        if state == "absent":
            raise LookupError("device absent")
        if state != "ok" or not payload:
            return None
        return payload[0] or None

    def device_name(self, device_index: int, timeout: float = P.QUICK_TIMEOUT) -> str:
        index = self.feature_index(device_index, P.FEAT_DEVICE_NAME, timeout)
        if not index:
            return ""
        head = self._call(device_index, index, 0x00, timeout=timeout)
        if not head or not head[0]:
            return ""
        length = min(head[0], 64)
        name = ""
        for offset in range(0, length, 16):
            chunk = self._call(device_index, index, 0x01, bytes([offset]), timeout)
            if not chunk:
                break
            name += bytes(chunk).split(b"\x00")[0].decode("utf-8", "replace")
            if len(name) >= length:
                break
        return name.strip()

    # ------------------------------------------------------------ 电量
    def _read_battery(self, device_index: int, timeout: float):
        """依次尝试三条电量特性，返回 (Reading|None, 协议标识)"""
        attempts = (
            (P.FEAT_UNIFIED_BATTERY, P.parse_logi_unified_battery, "HID++"),
            (P.FEAT_BATTERY_STATUS, P.parse_logi_battery_status, "0x1000"),
            (P.FEAT_BATTERY_VOLTAGE, P.parse_logi_battery_voltage, "0x1001"),
        )
        for feature_id, parser, label in attempts:
            index = self.feature_index(device_index, feature_id, timeout)
            if not index:
                continue

            caps = None
            if feature_id == P.FEAT_UNIFIED_BATTERY:
                caps = self._call(device_index, index, 0x00, timeout=timeout)
                status = self._call(device_index, index, 0x01, timeout=timeout)
                parsed = parser(caps, status)
            else:
                status = self._call(device_index, index, 0x00, timeout=timeout)
                parsed = parser(status)

            if not parsed:
                continue
            percent, text, charging = parsed
            return Reading(percent=percent, status=text,
                           charging=charging, transport=label), label
        return None, ""

    def probe(self, device_index: int, timeout: float = P.QUICK_TIMEOUT):
        """读一个槽位。返回 (Reading|None, 槽位状态 ok/absent/timeout)"""
        try:
            reading, _label = self._read_battery(device_index, timeout)
        except LookupError:
            return (None, "absent")
        except Exception:
            return (None, "timeout")
        if reading is None:
            return (None, "timeout")

        try:
            reading.name = self.device_name(device_index, timeout)
        except Exception:
            reading.name = ""
        reading.connected = True
        return (reading, "ok")


def group_receivers(interfaces):
    """把枚举结果配成 [(短集合, 长集合)]；同一接收器按 PID + 序列号配对"""
    grouped: dict[tuple, dict[int, object]] = {}
    for iface in interfaces:
        if iface.usage_page != P.LOGI_USAGE_PAGE:
            continue
        key = (iface.vendor_id, iface.product_id, iface.serial or "")
        grouped.setdefault(key, {})[iface.usage] = iface

    pairs = []
    for key, by_usage in sorted(grouped.items()):
        short = by_usage.get(P.LOGI_USAGE_SHORT)
        long_ = by_usage.get(P.LOGI_USAGE_LONG)
        if long_ is not None:
            pairs.append((short, long_))
    return pairs


class LogitechReader:
    """对外入口：枚举 -> 探测槽位 -> 返回一台设备的读数"""

    SLOTS = tuple(range(1, 8))
    # 记住上次成功的槽位，避免每轮都从 1 号盲扫
    _slot_cache: dict[tuple, int] = {}

    def read(self):
        """返回 (Reading|None, 错误信息)"""
        interfaces = enumerate_all(P.LOGI_VENDOR)
        pairs = group_receivers(interfaces)
        if not pairs:
            return (None, "没有找到罗技接收器")

        last_error = "接收器上没有应答的设备"
        for short_iface, long_iface in pairs:
            try:
                receiver = LogitechReceiver(short_iface or long_iface, long_iface)
            except Exception as exc:
                last_error = f"打开接收器失败：{exc}"
                continue
            try:
                order = list(self.SLOTS)
                cached = self._slot_cache.get(receiver.key)
                if cached in order:
                    order.remove(cached)
                    order.insert(0, cached)

                # 第一遍：轻探。空槽位靠短集合错误帧毫秒级排除
                pending = []
                for slot in order:
                    reading, state = receiver.probe(slot, P.QUICK_TIMEOUT)
                    if reading is not None:
                        self._slot_cache[receiver.key] = slot
                        return (reading, "")
                    if state == "timeout":
                        pending.append(slot)

                # 第二遍：只有「什么都没回」的槽位值得再等 —— 设备可能正在唤醒
                for slot in pending:
                    reading, _state = receiver.probe(slot, P.PATIENT_TIMEOUT)
                    if reading is not None:
                        self._slot_cache[receiver.key] = slot
                        return (reading, "")
                if not pending:
                    last_error = "罗技设备已离线（接收器上没有设备应答）"
            finally:
                receiver.close()
        return (None, last_error)
