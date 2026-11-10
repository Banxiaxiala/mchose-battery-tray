# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [唯一 Win32 入口] 全项目所有 ctypes 声明都必须集中在本文件。
   别的文件不许再写 ctypes.WinDLL(...) 或自定义 Structure —— 一旦分散，
   argtypes/restype 漏写就会造成 64 位指针被截断这类难查的崩溃。
2. [argtypes 必须齐全] 每个函数都必须声明 argtypes 与 restype。
   Windows 上 HANDLE/HWND 是 64 位指针，不声明 restype 会被截成 int32。
3. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes as wt

# ---------------------------------------------------------------- 基础常量
INVALID_HANDLE_VALUE = wt.HANDLE(-1).value

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
FILE_ATTRIBUTE_NORMAL = 0x00000080

ERROR_IO_PENDING = 997
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258

INFINITE = 0xFFFFFFFF

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
hid_dll = ctypes.WinDLL("hid", use_last_error=True)


# ---------------------------------------------------------------- kernel32
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                                 wt.DWORD, wt.DWORD, wt.HANDLE]
kernel32.CreateFileW.restype = wt.HANDLE

kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.CloseHandle.restype = wt.BOOL

kernel32.CreateEventW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.BOOL, wt.LPCWSTR]
kernel32.CreateEventW.restype = wt.HANDLE

kernel32.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                              ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
kernel32.ReadFile.restype = wt.BOOL

kernel32.WriteFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                               ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
kernel32.WriteFile.restype = wt.BOOL

kernel32.GetOverlappedResult.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                         ctypes.POINTER(wt.DWORD), wt.BOOL]
kernel32.GetOverlappedResult.restype = wt.BOOL

kernel32.CancelIoEx.argtypes = [wt.HANDLE, ctypes.c_void_p]
kernel32.CancelIoEx.restype = wt.BOOL

kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.WaitForSingleObject.restype = wt.DWORD

kernel32.GetLastError.restype = wt.DWORD

kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
kernel32.GetModuleHandleW.restype = wt.HMODULE

kernel32.GetCurrentProcess.restype = wt.HANDLE


# ---------------------------------------------------------------- user32
user32.RegisterClassExW.argtypes = [ctypes.c_void_p]
user32.RegisterClassExW.restype = wt.ATOM

user32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
                                   wt.INT, wt.INT, wt.INT, wt.INT,
                                   wt.HWND, wt.HMENU, wt.HINSTANCE, ctypes.c_void_p]
user32.CreateWindowExW.restype = wt.HWND

user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.DefWindowProcW.restype = wt.LPARAM

user32.DestroyWindow.argtypes = [wt.HWND]
user32.DestroyWindow.restype = wt.BOOL

user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostMessageW.restype = wt.BOOL

user32.GetMessageW.argtypes = [ctypes.c_void_p, wt.HWND, wt.UINT, wt.UINT]
user32.GetMessageW.restype = wt.BOOL

user32.TranslateMessage.argtypes = [ctypes.c_void_p]
user32.TranslateMessage.restype = wt.BOOL

user32.DispatchMessageW.argtypes = [ctypes.c_void_p]
user32.DispatchMessageW.restype = wt.LPARAM

user32.PeekMessageW.argtypes = [ctypes.c_void_p, wt.HWND, wt.UINT, wt.UINT, wt.UINT]
user32.PeekMessageW.restype = wt.BOOL

user32.RegisterWindowMessageW.argtypes = [wt.LPCWSTR]
user32.RegisterWindowMessageW.restype = wt.UINT

user32.GetCursorPos.argtypes = [ctypes.c_void_p]
user32.GetCursorPos.restype = wt.BOOL

user32.GetSystemMetrics.argtypes = [wt.INT]
user32.GetSystemMetrics.restype = wt.INT

user32.GetWindowRect.argtypes = [wt.HWND, ctypes.c_void_p]
user32.GetWindowRect.restype = wt.BOOL

user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.FindWindowW.restype = wt.HWND

user32.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]
user32.FindWindowExW.restype = wt.HWND

user32.GetDpiForWindow.argtypes = [wt.HWND]
user32.GetDpiForWindow.restype = wt.UINT

user32.SetForegroundWindow.argtypes = [wt.HWND]
user32.SetForegroundWindow.restype = wt.BOOL

user32.CreatePopupMenu.argtypes = []
user32.CreatePopupMenu.restype = wt.HMENU
user32.AppendMenuW.argtypes = [wt.HMENU, wt.UINT, wt.WPARAM, wt.LPCWSTR]
user32.AppendMenuW.restype = wt.BOOL
user32.DestroyMenu.argtypes = [wt.HMENU]
user32.DestroyMenu.restype = wt.BOOL
user32.TrackPopupMenuEx.argtypes = [wt.HMENU, wt.UINT, wt.INT, wt.INT,
                                    wt.HWND, ctypes.c_void_p]
user32.TrackPopupMenuEx.restype = wt.UINT
user32.SetMenuDefaultItem.argtypes = [wt.HMENU, wt.UINT, wt.UINT]
user32.SetMenuDefaultItem.restype = wt.BOOL

user32.LoadImageW.argtypes = [wt.HINSTANCE, wt.LPCWSTR, wt.UINT, wt.INT, wt.INT, wt.UINT]
user32.LoadImageW.restype = wt.HANDLE
user32.DestroyIcon.argtypes = [wt.HANDLE]
user32.DestroyIcon.restype = wt.BOOL

user32.CreateIconIndirect.argtypes = [ctypes.c_void_p]
user32.CreateIconIndirect.restype = wt.HANDLE


# ---------------------------------------------------------------- gdi32
gdi32.CreateBitmap.argtypes = [wt.INT, wt.INT, wt.UINT, wt.UINT, ctypes.c_void_p]
gdi32.CreateBitmap.restype = wt.HBITMAP
gdi32.CreateDIBSection.argtypes = [wt.HDC, ctypes.c_void_p, wt.UINT,
                                   ctypes.c_void_p, wt.HANDLE, wt.DWORD]
gdi32.CreateDIBSection.restype = wt.HBITMAP
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteObject.restype = wt.BOOL
gdi32.CreateSolidBrush.argtypes = [wt.DWORD]
gdi32.CreateSolidBrush.restype = wt.HBRUSH


# ---------------------------------------------------------------- shell32
class NOTIFYICONDATAW(ctypes.Structure):
    """Shell_NotifyIcon 的载荷。

    结构体大小随系统版本变化：Windows 2000 起是 952 字节（含 guidItem / balloon）。
    这里按 Unicode 版本、cbSize 用 32 位边界对齐声明，与 SDK 一致。
    """

    _fields_ = [
        ("cbSize", wt.DWORD),
        ("hWnd", wt.HWND),
        ("uID", wt.UINT),
        ("uFlags", wt.UINT),
        ("uCallbackMessage", wt.UINT),
        ("hIcon", wt.HANDLE),
        ("szTip", wt.WCHAR * 128),
        ("dwState", wt.DWORD),
        ("dwStateMask", wt.DWORD),
        ("szInfo", wt.WCHAR * 256),
        ("uVersion", wt.UINT),
        ("szInfoTitle", wt.WCHAR * 64),
        ("dwInfoFlags", wt.DWORD),
        ("guidItem", ctypes.c_byte * 16),
        ("hBalloonIcon", wt.HANDLE),
    ]


class NOTIFYICONIDENTIFIER(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD),
        ("hWnd", wt.HWND),
        ("uID", wt.UINT),
        ("guidItem", ctypes.c_byte * 16),
    ]


shell32.Shell_NotifyIconW.argtypes = [wt.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]
shell32.Shell_NotifyIconW.restype = wt.BOOL

shell32.Shell_NotifyIconGetRect.argtypes = [ctypes.POINTER(NOTIFYICONIDENTIFIER),
                                            ctypes.POINTER(wt.RECT)]
shell32.Shell_NotifyIconGetRect.restype = ctypes.c_long


# ---------------------------------------------------------------- hid.dll
class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wt.ULONG),
                ("VendorID", wt.USHORT),
                ("ProductID", wt.USHORT),
                ("VersionNumber", wt.USHORT)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wt.USHORT),
        ("UsagePage", wt.USHORT),
        ("InputReportByteLength", wt.USHORT),
        ("OutputReportByteLength", wt.USHORT),
        ("FeatureReportByteLength", wt.USHORT),
        ("Reserved", wt.USHORT * 17),
        ("NumberLinkCollectionNodes", wt.USHORT),
        ("NumberInputButtonCaps", wt.USHORT),
        ("NumberInputValueCaps", wt.USHORT),
        ("NumberInputDataIndices", wt.USHORT),
        ("NumberOutputButtonCaps", wt.USHORT),
        ("NumberOutputValueCaps", wt.USHORT),
        ("NumberOutputDataIndices", wt.USHORT),
        ("NumberFeatureButtonCaps", wt.USHORT),
        ("NumberFeatureValueCaps", wt.USHORT),
        ("NumberFeatureDataIndices", wt.USHORT),
    ]


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_byte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD),
                ("InterfaceClassGuid", GUID),
                ("Flags", wt.DWORD),
                ("Reserved", ctypes.c_void_p)]


hid_dll.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
hid_dll.HidD_GetHidGuid.restype = None

hid_dll.HidD_GetAttributes.argtypes = [wt.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid_dll.HidD_GetAttributes.restype = wt.BOOL

hid_dll.HidD_GetPreparsedData.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid_dll.HidD_GetPreparsedData.restype = wt.BOOL

hid_dll.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid_dll.HidD_FreePreparsedData.restype = wt.BOOL

hid_dll.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
hid_dll.HidP_GetCaps.restype = ctypes.c_long

hid_dll.HidD_GetProductString.argtypes = [wt.HANDLE, wt.LPWSTR, wt.ULONG]
hid_dll.HidD_GetProductString.restype = wt.BOOL

hid_dll.HidD_GetManufacturerString.argtypes = [wt.HANDLE, wt.LPWSTR, wt.ULONG]
hid_dll.HidD_GetManufacturerString.restype = wt.BOOL

hid_dll.HidD_GetSerialNumberString.argtypes = [wt.HANDLE, wt.LPWSTR, wt.ULONG]
hid_dll.HidD_GetSerialNumberString.restype = wt.BOOL

hid_dll.HidD_GetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid_dll.HidD_GetFeature.restype = wt.BOOL

hid_dll.HidD_SetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid_dll.HidD_SetFeature.restype = wt.BOOL

hid_dll.HidD_GetInputReport.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid_dll.HidD_GetInputReport.restype = wt.BOOL


# ---------------------------------------------------------------- setupapi
setupapi = ctypes.WinDLL("setupapi", use_last_error=True)

setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wt.LPCWSTR,
                                          wt.HWND, wt.DWORD]
setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE

setupapi.SetupDiEnumDeviceInterfaces.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                                 ctypes.POINTER(GUID), wt.DWORD,
                                                 ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiEnumDeviceInterfaces.restype = wt.BOOL

setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA), ctypes.c_void_p,
    wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wt.BOOL

setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]
setupapi.SetupDiDestroyDeviceInfoList.restype = wt.BOOL

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10


# ---------------------------------------------------------------- 便捷封装
class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p),
                ("InternalHigh", ctypes.c_void_p),
                ("Offset", wt.DWORD),
                ("OffsetHigh", wt.DWORD),
                ("hEvent", wt.HANDLE)]


class MSG(ctypes.Structure):
    _fields_ = [("hwnd", wt.HWND), ("message", wt.UINT),
                ("wParam", wt.WPARAM), ("lParam", wt.LPARAM),
                ("time", wt.DWORD), ("pt", wt.POINT),
                ("lPrivate", wt.DWORD)]


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("style", wt.UINT),
                ("lpfnWndProc", ctypes.c_void_p), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE),
                ("hIcon", wt.HICON), ("hCursor", wt.HANDLE),
                ("hbrBackground", wt.HBRUSH), ("lpszMenuName", wt.LPCWSTR),
                ("lpszClassName", wt.LPCWSTR), ("hIconSm", wt.HICON)]


def last_error() -> int:
    return ctypes.get_last_error()


def open_device(path: str, read_write: bool = True,
                overlapped: bool = False) -> int | None:
    """打开 HID 设备句柄；失败返回 None。

    read_write=False 时用 0 权限（FILE_READ_ATTRIBUTES），这是打开键盘类
    集合的唯一方式 —— Windows 内核对键盘有防监听独占锁，要 GENERIC_READ
    会被直接 ERROR_ACCESS_DENIED。

    overlapped=True 时加 FILE_FLAG_OVERLAPPED。**打开句柄时不加这个标志，
    后续即使传了 OVERLAPPED 结构，ReadFile/WriteFile 也会退化为同步阻塞**，
    带超时的读写全部失效，调用会永久挂住（本项目的 PendingRead 就因此卡死过）。
    """
    access = (GENERIC_READ | GENERIC_WRITE) if read_write else 0
    flags = FILE_FLAG_OVERLAPPED if overlapped else 0
    handle = kernel32.CreateFileW(path, access,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  None, OPEN_EXISTING, flags, None)
    if handle == INVALID_HANDLE_VALUE or not handle:
        return None
    return handle


def close_handle(handle) -> None:
    if handle:
        try:
            kernel32.CloseHandle(handle)
        except Exception:
            pass

class ICONINFO(ctypes.Structure):
    _fields_ = [("fIcon", wt.BOOL), ("xHotspot", wt.DWORD),
                ("yHotspot", wt.DWORD), ("hbmMask", wt.HBITMAP),
                ("hbmColor", wt.HBITMAP)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG),
                ("biHeight", wt.LONG), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


BI_RGB = 0


def create_icon_from_bgra(width: int, height: int, bgra_bottom_up: bytes):
    """把 BGRA（自底向上）像素做成 HICON。

    走 CreateDIBSection 而不是 CreateBitmap：32bpp 的 DIB 天然带 alpha 通道，
    Windows 会按 per-pixel alpha 合成，托盘图标边缘才不会有黑边。
    CreateBitmap + 单独的掩码位图在 Win10/11 上会丢掉半透明，边缘出现锯齿黑框。
    """
    header = BITMAPINFOHEADER()
    header.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    header.biWidth = width
    header.biHeight = height          # 正数 = 自底向上，与 to_bgra_bottom_up 一致
    header.biPlanes = 1
    header.biBitCount = 32
    header.biCompression = BI_RGB
    header.biSizeImage = 0

    bits = ctypes.c_void_p()
    color = gdi32.CreateDIBSection(None, ctypes.byref(header), 0,
                                   ctypes.byref(bits), None, 0)
    if not color:
        return None
    try:
        size = width * height * 4
        ctypes.memmove(bits, bgra_bottom_up, size)

        # 掩码位图是 1bpp 单色，全 0 表示「全部由 alpha 通道决定」
        mask = gdi32.CreateBitmap(width, height, 1, 1, None)
        if not mask:
            return None
        try:
            info = ICONINFO()
            info.fIcon = True
            info.hbmMask = mask
            info.hbmColor = color
            hicon = user32.CreateIconIndirect(ctypes.byref(info))
        finally:
            gdi32.DeleteObject(mask)
        return hicon
    finally:
        gdi32.DeleteObject(color)
