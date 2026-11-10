# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [协议常量集中] 所有 VID/PID/命令码/字节偏移只允许定义在本文件顶部，
   严禁散落在解析函数里。协议是逆向来的，一旦分散就没法一眼核对。
2. [纯解析] parse_* 系列必须是**纯函数**（只吃 bytes，只吐值），不许碰
   句柄或 I/O —— 它们要能被 tests/test_protocols.py 用真实抓包帧断言。
3. [编码] UTF-8 with BOM。
"""

from __future__ import annotations

# ================================================================ 罗技 HID++
LOGI_VENDOR = 0x046D
LOGI_USAGE_PAGE = 0xFF00
LOGI_USAGE_SHORT = 0x0001       # 7 字节，只用来判定「设备不在线」
LOGI_USAGE_LONG = 0x0002        # 20 字节，真正的请求/应答通道

HIDPP_LONG = 0x11
HIDPP_SHORT = 0x10
HIDPP_ERROR_SUBID = 0x8F
# 软件 ID 必须非零：设备主动推送的通知用 0，与功能 0 的应答字节完全相同，
# 只有非零才能区分「这是给我的应答」还是「这是设备自己在广播」。
HIDPP_SOFTWARE_ID = 0x0A

FEAT_ROOT = 0x0000
FEAT_DEVICE_NAME = 0x0005
FEAT_BATTERY_STATUS = 0x1000
FEAT_BATTERY_VOLTAGE = 0x1001
FEAT_UNIFIED_BATTERY = 0x1004

# 槽位探测超时。实测依据（logi-tray 的数据）：
#   在线设备中位应答约 57ms；休眠唤醒时首帧可能慢到约 850ms，
#   且第一次请求可能直接失败；目标不可达时接收器 3~5ms 内就在短集合回绝。
QUICK_TIMEOUT = 0.40
PATIENT_TIMEOUT = 1.30

# 0x1000 BATTERY_STATUS 的电池状态码 -> (文案, 是否充电)
STATUS_1000 = {
    0: ("放电中", False),
    1: ("充电中", True),
    2: ("充电中（接近充满）", True),
    3: ("已充满", True),
    4: ("充电中（慢充）", True),
    5: ("电池异常", False),
    6: ("温度异常", False),
}

# 0x1004 UNIFIED_BATTERY 的充电状态字节
STATUS_1004 = {
    0: ("放电中", False),
    1: ("充电中", True),
    2: ("充电中（慢充）", True),
    3: ("已充满", True),
    4: ("充电异常", False),
}

# 0x1004 档位位掩码 -> (文案, 近似电量)。Solaar BatteryLevelApproximation
LEVEL_BITS = ((0x08, "满", 90), (0x04, "良好", 50),
              (0x02, "偏低", 20), (0x01, "极低", 5))

# Solaar 的放电曲线：电压 mV -> 百分比（分段线性插值）
DISCHARGE_CURVE = ((4186, 100), (4067, 90), (3989, 80), (3922, 70),
                   (3859, 60), (3811, 50), (3778, 40), (3751, 30),
                   (3717, 20), (3671, 10), (3646, 5), (3579, 2),
                   (3500, 0))


# ================================================================ 雷蛇 PA-PI
RAZER_VENDOR = 0x1532
RAZER_USAGE_PAGE = 0xFF00
RAZER_REPORT_LEN = 64
RAZER_REPORT_ID = 0x02

RAZER_CMD_SERIAL = 0x00
RAZER_CMD_CONNECTION = 0x20
RAZER_CMD_BATTERY = 0x21
RAZER_CMD_CHARGING = 0x2A
RAZER_CMD_AUTO_POWER_OFF = 0x2C

# 已知机型（product_string 里通常已带型号，这里只是兜底）
RAZER_MODEL_NAMES = {0x0555: "BlackShark V2 Pro", 0x056C: "Kraken V4"}


# ================================================================ 纯解析函数
def parse_logi_unified_battery(caps: bytes | None, status: bytes | None):
    """解析 0x1004 UNIFIED_BATTERY 应答。

    返回 (percent|None, 状态文案, 是否充电)。返回 None 表示数据不足以判定。

    布局依据 Linux 内核 hidpp20：
      caps[1] BIT(1) = STATE_OF_CHARGE —— 只有置位时 status[0] 才是百分比；
      否则 status[0] 无意义，必须用 status[1] 的档位位掩码（先与 caps[0] 相与）。
    """
    if not status:
        return None

    has_soc = True
    if caps and len(caps) >= 2:
        has_soc = bool(caps[1] & 0x02)

    percent = None
    text, charging = "", False

    if has_soc:
        percent = min(max(status[0], 0), 100)
    else:
        level_bits = status[1] if len(status) > 1 else 0
        if caps:
            level_bits &= caps[0]
        for bit, level_text, approx in LEVEL_BITS:
            if level_bits & bit:
                percent, text = approx, level_text
                break

    if len(status) > 2:
        # 只报档位的设备（has_soc=False）里 status[2] 仍然有充电状态，
        # 但档位文案（满/良好/偏低/极低）比如实有信息量，不能被覆盖。
        charge_text, charging = STATUS_1004.get(status[2], ("", False))
        if has_soc:
            text = charge_text

    if percent is None and not text:
        return None
    return (percent, text, charging)


def parse_logi_battery_status(status: bytes | None):
    """解析 0x1000 BATTERY_STATUS 应答：status[0]=百分比 status[2]=状态码"""
    if not status:
        return None
    percent = min(max(status[0], 0), 100)
    text, charging = ("", False)
    if len(status) > 2:
        text, charging = STATUS_1000.get(status[2], ("", False))
    return (percent, text, charging)


def parse_logi_battery_voltage(data: bytes | None):
    """解析 0x1001 BATTERY_VOLTAGE 应答：be16 电压 + 标志字节"""
    if not data or len(data) < 2:
        return None
    mv = (data[0] << 8) | data[1]
    percent = percent_from_mv(mv)
    if percent is None:
        return None

    flags = data[2] if len(data) > 2 else 0
    if flags & 0x80:
        stage = flags & 0x07
        text, charging = {
            0: ("充电中", True),
            1: ("已充满", True),
            2: ("放电中", False),
        }.get(stage, ("", False))
    else:
        text, charging = ("放电中", False)
    return (percent, text, charging)


def percent_from_mv(mv: int) -> int | None:
    """电压 -> 百分比，按 Solaar 放电曲线分段线性插值"""
    if mv <= 0:
        return None
    curve = DISCHARGE_CURVE
    if mv >= curve[0][0]:
        return curve[0][1]
    if mv <= curve[-1][0]:
        return curve[-1][1]
    for (v_high, p_high), (v_low, p_low) in zip(curve, curve[1:]):
        if v_low <= mv <= v_high:
            ratio = (mv - v_low) / (v_high - v_low)
            return round(p_low + (p_high - p_low) * ratio)
    return None


def build_logi_request(device_index: int, feature_index: int, function: int,
                       params: bytes = b"") -> bytes:
    """构造 20 字节 HID++ 2.0 长请求"""
    function_byte = ((function << 4) & 0xF0) | HIDPP_SOFTWARE_ID
    packet = bytearray(20)
    packet[0] = HIDPP_LONG
    packet[1] = device_index & 0xFF
    packet[2] = feature_index & 0xFF
    packet[3] = function_byte
    for i, byte in enumerate(params[:16]):
        packet[4 + i] = byte
    return bytes(packet)


def match_logi_reply(data: bytes, device_index: int, feature_index: int,
                     function: int) -> bytes | None:
    """严格匹配四字段，返回数据区 data[4:]；不匹配返回 None。

    必须要求 function 字节完全一致：设备主动推送通知的软件 ID 是 0，
    不校验的话会把通知当成应答读出一堆 0。
    """
    if not data or len(data) < 4:
        return None
    function_byte = ((function << 4) & 0xF0) | HIDPP_SOFTWARE_ID
    if (data[0] != HIDPP_LONG or data[1] != (device_index & 0xFF)
            or data[2] != (feature_index & 0xFF) or data[3] != function_byte):
        return None
    return data[4:]


def match_logi_absent(data: bytes, device_index: int, feature_index: int,
                      function: int) -> bool:
    """短集合上的错误帧：说明接收器明确了「这个槽位没有设备」"""
    if not data or len(data) < 7:
        return False
    function_byte = ((function << 4) & 0xF0) | HIDPP_SOFTWARE_ID
    return (data[0] == HIDPP_SHORT and data[1] == (device_index & 0xFF)
            and data[2] == HIDPP_ERROR_SUBID and data[3] == (feature_index & 0xFF)
            and data[4] == function_byte)


def build_razer_frame(cmd: int) -> bytes:
    """构造 64 字节 PA/PI 命令帧

    实测格式：02 80 08 00 00 'P' 'A' 08 00 03 <CMD> 00 ...
    """
    frame = bytearray(RAZER_REPORT_LEN)
    frame[0] = RAZER_REPORT_ID
    frame[1] = 0x80
    frame[2] = 0x08
    frame[5] = 0x50          # 'P'
    frame[6] = 0x41          # 'A'
    frame[7] = 0x08          # 序号
    frame[9] = 0x03
    frame[10] = cmd
    return bytes(frame)


def parse_razer_reply(reply: bytes, cmd: int) -> int | None:
    """解析应答，返回 value 字节；不匹配返回 None

    实测格式：02 <len> 'P' 'I' <seq> <4B 计数> 00 04 00 <CMD> 01 01 <value> ...
    """
    if not reply or len(reply) < 16:
        return None
    if reply[0] != RAZER_REPORT_ID:
        return None
    if reply[2] != 0x50 or reply[3] != 0x49:      # 'P' 'I'
        return None
    if reply[12] != cmd or reply[13] != 0x01:
        return None
    return reply[15]


def razer_model_name(product: str, product_id: int) -> str:
    """从 product_string 里剥出型号；剥不出来就用已知表兜底"""
    name = (product or "").strip()
    for prefix in ("Razer ", "RAZER "):
        if name.startswith(prefix):
            name = name[len(prefix):]
    name = name.replace(" 2.4", "").replace(" 2.4G", "").strip()
    if name and not name.upper().startswith("HID"):
        return name
    return RAZER_MODEL_NAMES.get(product_id, "Razer 无线耳机")
