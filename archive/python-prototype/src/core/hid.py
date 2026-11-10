# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [零第三方依赖] 本文件只允许用 src/core/win32.py 里的 ctypes 声明，
   严禁 import hidapi / hid / pywin32。
2. [常驻挂起读] 接收器对「目标槽位不可达」只在短集合上回一条 3~5ms 的错误帧。
   如果每次 poll 都 CancelIoEx 再重建 ReadFile，取消与重建之间的空窗恰好
   会吃掉这条帧，于是空槽位永远判不出来、只能等满超时。
   PendingRead 因此让读请求**一直挂着**，只有取到数据后才重新挂下一轮。
3. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

import ctypes
import struct

from . import win32 as w32


class HidInterface:
    """一个已枚举到的 HID 集合（只描述，不持有句柄）"""

    __slots__ = ("path", "vendor_id", "product_id", "usage_page", "usage",
                 "product", "manufacturer", "serial", "release",
                 "input_len", "output_len", "feature_len")

    def __init__(self, **kw):
        for name in self.__slots__:
            setattr(self, name, kw.get(name))

    def __repr__(self):
        return (f"HidInterface({self.vendor_id:#06x}:{self.product_id:#06x} "
                f"up={self.usage_page:#06x} u={self.usage:#06x} "
                f"io={self.input_len}/{self.output_len} "
                f"feat={self.feature_len} prod={self.product!r})")


def _string(handle, fn, size: int = 256) -> str:
    buf = ctypes.create_unicode_buffer(size)
    try:
        if fn(handle, buf, size):
            return buf.value.strip()
    except Exception:
        pass
    return ""


def _caps(handle):
    pre = ctypes.c_void_p()
    if not w32.hid_dll.HidD_GetPreparsedData(handle, ctypes.byref(pre)):
        return None
    try:
        caps = w32.HIDP_CAPS()
        if w32.hid_dll.HidP_GetCaps(pre, ctypes.byref(caps)) != 0x00110000:
            return None
        return caps
    finally:
        w32.hid_dll.HidD_FreePreparsedData(pre)


def device_path(info, iface) -> str | None:
    """取设备接口路径。

    SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize 字段固定是 4 字节 DWORD，
    路径字符串从紧随其后的第 4 个字节开始。偏移若按 x64 写成 8，会吃掉路径
    前 4 个字符，得到 "?\\hid#..." 这种残缺路径，CreateFile 直接报错误 123。
    """
    required = ctypes.c_ulong(0)
    w32.setupapi.SetupDiGetDeviceInterfaceDetailW(
        info, ctypes.byref(iface), None, 0, ctypes.byref(required), None)
    if required.value == 0:
        return None

    buf = ctypes.create_string_buffer(required.value)
    struct.pack_into("I", buf, 0, 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6)
    if not w32.setupapi.SetupDiGetDeviceInterfaceDetailW(
            info, ctypes.byref(iface), buf, required.value,
            ctypes.byref(required), None):
        return None

    text = buf.raw[4:required.value].decode("utf-16-le", "ignore")
    end = text.find("\x00")
    if end >= 0:
        text = text[:end]
    return text or None


def describe(path: str, vendor_id=None, product_id=None) -> HidInterface | None:
    # 只用 0 权限打开：足够读属性，且不会撞上键盘类的防监听独占锁
    handle = w32.open_device(path, read_write=False)
    if not handle:
        return None
    try:
        attrs = w32.HIDD_ATTRIBUTES()
        attrs.Size = ctypes.sizeof(w32.HIDD_ATTRIBUTES)
        if not w32.hid_dll.HidD_GetAttributes(handle, ctypes.byref(attrs)):
            return None
        if vendor_id is not None and attrs.VendorID != vendor_id:
            return None
        if product_id is not None and attrs.ProductID != product_id:
            return None

        caps = _caps(handle)
        if caps is None:
            return None

        return HidInterface(
            path=path,
            vendor_id=attrs.VendorID,
            product_id=attrs.ProductID,
            usage_page=caps.UsagePage,
            usage=caps.Usage,
            product=_string(handle, w32.hid_dll.HidD_GetProductString),
            manufacturer=_string(handle, w32.hid_dll.HidD_GetManufacturerString),
            serial=_string(handle, w32.hid_dll.HidD_GetSerialNumberString),
            release=attrs.VersionNumber,
            input_len=caps.InputReportByteLength,
            output_len=caps.OutputReportByteLength,
            feature_len=caps.FeatureReportByteLength,
        )
    finally:
        w32.close_handle(handle)


def enumerate_all(vendor_id: int | None = None,
                  product_id: int | None = None) -> list[HidInterface]:
    """枚举 HID 集合，顺带带回 Input/Output/Feature 报告长度。

    用 SetupAPI 而不是第三方库：协议层判断「这个接口能不能用」全靠报告长度
    （罗技短集合 7 / 长集合 20、雷蛇 64/64 且 feature=0）。
    """
    guid = w32.GUID()
    w32.hid_dll.HidD_GetHidGuid(ctypes.byref(guid))

    info = w32.setupapi.SetupDiGetClassDevsW(
        ctypes.byref(guid), None, None,
        w32.DIGCF_PRESENT | w32.DIGCF_DEVICEINTERFACE)
    if not info or info == w32.INVALID_HANDLE_VALUE:
        return []

    results: list[HidInterface] = []
    try:
        iface = w32.SP_DEVICE_INTERFACE_DATA()
        iface.cbSize = ctypes.sizeof(w32.SP_DEVICE_INTERFACE_DATA)
        index = 0
        while w32.setupapi.SetupDiEnumDeviceInterfaces(
                info, None, ctypes.byref(guid), index, ctypes.byref(iface)):
            index += 1
            path = device_path(info, iface)
            if not path:
                continue
            described = describe(path, vendor_id, product_id)
            if described is not None:
                results.append(described)
    finally:
        w32.setupapi.SetupDiDestroyDeviceInfoList(info)
    return results


class HidDevice:
    """打开着的 HID 句柄，支持带超时的重叠读写"""

    def __init__(self, path: str, read_write: bool = True):
        self.path = path
        # 必须以 FILE_FLAG_OVERLAPPED 打开，否则 ReadFile/WriteFile 会退化成
        # 同步阻塞，带超时的重叠 I/O 全部失效（PendingRead 会永久挂死）。
        self._handle = w32.open_device(path, read_write=read_write, overlapped=True)
        if not self._handle:
            raise OSError(f"打开 HID 设备失败（错误 {w32.last_error()}）：{path}")
        self._event = w32.kernel32.CreateEventW(None, True, False, None)
        if not self._event:
            w32.close_handle(self._handle)
            self._handle = None
            raise OSError(f"CreateEvent 失败（错误 {w32.last_error()}）")
        self.closed = False

    def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            w32.kernel32.CancelIoEx(self._handle, None)
        except Exception:
            pass
        w32.close_handle(self._event)
        w32.close_handle(self._handle)
        self._handle = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # ---------------------------------------------------------------- 写
    def write(self, data: bytes, timeout: float = 0.5) -> int:
        """写一条输出报告（中断 OUT）。返回写入字节数，失败返回 -1"""
        if self.closed:
            return -1
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        ov = w32.OVERLAPPED()
        ov.hEvent = self._event
        w32.kernel32.ResetEvent(self._event)
        written = ctypes.c_ulong(0)

        ok = w32.kernel32.WriteFile(self._handle, buf, len(data),
                                    ctypes.byref(written), ctypes.byref(ov))
        if not ok:
            if w32.last_error() != w32.ERROR_IO_PENDING:
                return -1
            if w32.kernel32.WaitForSingleObject(
                    self._event, int(timeout * 1000)) != w32.WAIT_OBJECT_0:
                w32.kernel32.CancelIoEx(self._handle, ctypes.byref(ov))
                w32.kernel32.GetOverlappedResult(
                    self._handle, ctypes.byref(ov), ctypes.byref(written), True)
                return -1
            if not w32.kernel32.GetOverlappedResult(
                    self._handle, ctypes.byref(ov), ctypes.byref(written), False):
                return -1
        return int(written.value)

    # ---------------------------------------------------------------- 读
    def read(self, length: int, timeout: float = 0.2) -> bytes:
        """一次性的同步读；超时返回 b""。轮询请用 PendingRead"""
        if self.closed:
            return b""
        buf = ctypes.create_string_buffer(length)
        ov = w32.OVERLAPPED()
        ov.hEvent = self._event
        w32.kernel32.ResetEvent(self._event)
        read = ctypes.c_ulong(0)

        ok = w32.kernel32.ReadFile(self._handle, buf, length,
                                   ctypes.byref(read), ctypes.byref(ov))
        if not ok:
            if w32.last_error() != w32.ERROR_IO_PENDING:
                return b""
            if w32.kernel32.WaitForSingleObject(
                    self._event, int(timeout * 1000)) != w32.WAIT_OBJECT_0:
                w32.kernel32.CancelIoEx(self._handle, ctypes.byref(ov))
                w32.kernel32.GetOverlappedResult(
                    self._handle, ctypes.byref(ov), ctypes.byref(read), True)
                return b""
            if not w32.kernel32.GetOverlappedResult(
                    self._handle, ctypes.byref(ov), ctypes.byref(read), False):
                return b""
        n = int(read.value)
        return bytes(buf.raw[:n]) if n else b""

    def drain(self, length: int, max_rounds: int = 32):
        for _ in range(max_rounds):
            if not self.read(length, timeout=0.0):
                return


class PendingRead:
    """常驻挂起读。

    读请求一直挂在驱动里，报告一到达就能立刻取走，不存在「取消 → 重建」的
    空窗（那条 3~5ms 的不可达回绝帧正是掉在这里）。取到数据之后才重新挂
    下一轮，因此 poll 语义是「等这一条」，不会一次丢两条。
    """

    def __init__(self, device: HidDevice, length: int):
        self._dev = device
        self._len = length
        self._buf = ctypes.create_string_buffer(length)
        self._ov = w32.OVERLAPPED()
        self._event = w32.kernel32.CreateEventW(None, True, False, None)
        if not self._event:
            raise OSError(f"CreateEvent 失败（错误 {w32.last_error()}）")
        self._ov.hEvent = self._event
        self._read = ctypes.c_ulong(0)
        self._pending = False
        self._broken = False
        self._cancelled = False

    # ---------------------------------------------------------------- 内部
    def _arm(self):
        """挂起一轮读请求"""
        if self._pending or self._broken or self._cancelled or self._dev.closed:
            return
        w32.kernel32.ResetEvent(self._event)
        ok = w32.kernel32.ReadFile(self._dev._handle, self._buf, self._len,
                                   ctypes.byref(self._read), ctypes.byref(self._ov))
        if ok:
            # 同步完成（数据已在 buffer 里），下一次 poll 会立刻取走
            self._pending = True
        elif w32.last_error() == w32.ERROR_IO_PENDING:
            self._pending = True
        else:
            self._broken = True

    def _take(self) -> bytes:
        """取走已完成的那一轮结果"""
        count = ctypes.c_ulong(0)
        ok = w32.kernel32.GetOverlappedResult(
            self._dev._handle, ctypes.byref(self._ov), ctypes.byref(count), False)
        self._pending = False
        if not ok:
            err = w32.last_error()
            # 87 = ERROR_INVALID_PARAMETER：长度与接口不匹配，别再无谓重试
            if err == 87:
                self._broken = True
            return b""
        n = int(count.value)
        data = bytes(self._buf.raw[:n]) if n else b""
        self._arm()
        return data

    # ---------------------------------------------------------------- 对外
    def start(self):
        self._arm()
        return self

    def poll(self, timeout: float = 0.0) -> bytes:
        """在 timeout 内等一条报告；没有就返回 b"" """
        if self._cancelled:
            return b""
        if not self._pending:
            self._arm()
            if not self._pending:
                return b""

        if timeout > 0:
            wait = w32.kernel32.WaitForSingleObject(self._event,
                                                    max(0, int(timeout * 1000)))
            if wait != w32.WAIT_OBJECT_0:
                return b""
        else:
            if w32.kernel32.WaitForSingleObject(self._event, 0) != w32.WAIT_OBJECT_0:
                return b""
        return self._take()

    def cancel(self):
        if self._cancelled:
            return
        self._cancelled = True
        if self._pending and self._ov.hEvent:
            w32.kernel32.CancelIoEx(self._dev._handle, ctypes.byref(self._ov))
            count = ctypes.c_ulong(0)
            w32.kernel32.GetOverlappedResult(
                self._dev._handle, ctypes.byref(self._ov), ctypes.byref(count), True)
            self._pending = False
        w32.close_handle(self._event)
        self._event = None

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.cancel()
