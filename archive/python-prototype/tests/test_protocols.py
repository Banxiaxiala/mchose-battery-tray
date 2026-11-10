# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

1. [真机抓包帧] test_protocols 里每一条断言都必须对应一次实测抓包，
   注释里要写出帧的来源。改协议前先跑这里。
2. [零硬件依赖] 这些测试全部脱机运行，不许打开任何 HID 句柄。
3. [编码] UTF-8 with BOM。

本机实测帧（用于核对）：
  罗技 PRO X 2 (046D:C54D)
    请求 0x1004 GetFeature : 11 01 00 0a 10 04 00 ...
    应答 feature 索引 0x06  : 11 01 00 0a 06 00 05 ...
    请求 GetCapabilities    : 11 01 06 0a 00 00 ...
    应答 caps               : 0F 0F 02 00 ...   (BIT(1) 置位 = 直报百分比)
    请求 GetStatus          : 11 01 06 1a 00 00 ...
    应答 status             : 35 04 00 00 ...   (0x35=53%, 0x04=放电慢充)
    设备名 PRO X 2
  雷蛇 BlackShark V2 Pro 2.4 (1532:0555)
    发送 02 80 08 00 00 50 41 08 00 03 21 00 ...
    应答 02 0e 50 49 08 C5 72 89 02 00 04 00 21 01 01 32 ...
                                                ↑ 0x32 = 50%
"""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from src.hardware import protocols as P


class LogitechRequestTests(unittest.TestCase):
    def test_build_request_layout(self):
        pkt = P.build_logi_request(1, P.FEAT_ROOT, 0x00,
                                   bytes([0x10, 0x04]))
        self.assertEqual(len(pkt), 20)
        self.assertEqual(pkt[0], P.HIDPP_LONG)
        self.assertEqual(pkt[1], 1)
        self.assertEqual(pkt[2], 0x00)
        # 软件 ID 必须非零，否则与设备主动推送的通知撞车
        self.assertEqual(pkt[3], P.HIDPP_SOFTWARE_ID)
        self.assertEqual(pkt[4], 0x10)
        self.assertEqual(pkt[5], 0x04)
        self.assertEqual(pkt[6:], bytes(14))

    def test_build_request_get_status(self):
        # 实测：GetStatus 是 function 1
        pkt = P.build_logi_request(1, 0x06, 0x01)
        self.assertEqual(pkt[3], 0x1A)

    def test_match_reply_exact(self):
        frame = bytes([0x11, 0x01, 0x06, 0x1A, 0x35, 0x04, 0x00, 0x00]
                      + [0] * 12)
        payload = P.match_logi_reply(frame, 1, 0x06, 0x01)
        self.assertIsNotNone(payload)
        self.assertEqual(payload[0], 0x35)

    def test_match_reply_rejects_wrong_software_id(self):
        # 设备主动推送的通知软件 ID 为 0，必须拒绝
        frame = bytes([0x11, 0x01, 0x06, 0x10, 0x00, 0x00] + [0] * 14)
        self.assertIsNone(P.match_logi_reply(frame, 1, 0x06, 0x01))

    def test_match_absent_error_frame(self):
        # 短集合错误帧：10 <idx> 8F <featIdx> <funcByte> <err> 00
        frame = bytes([0x10, 0x04, 0x8F, 0x00, 0x0A, 0x08, 0x00])
        self.assertTrue(P.match_logi_absent(frame, 4, 0x00, 0x00))
        self.assertFalse(P.match_logi_absent(frame, 5, 0x00, 0x00))


class LogitechBatteryParseTests(unittest.TestCase):
    def test_unified_battery_real_device(self):
        # 本机抓包：PRO X 2 报 0x35 = 53%
        caps = bytes([0x0F, 0x0F, 0x02, 0x00] + [0] * 12)
        status = bytes([0x35, 0x04, 0x00, 0x00] + [0] * 12)
        percent, text, charging = P.parse_logi_unified_battery(caps, status)
        self.assertEqual(percent, 53)
        self.assertFalse(charging)

    def test_unified_battery_charging(self):
        caps = bytes([0x0F, 0x02, 0x00, 0x00])
        status = bytes([100, 0x08, 0x01, 0x01])
        percent, text, charging = P.parse_logi_unified_battery(caps, status)
        self.assertEqual(percent, 100)
        self.assertTrue(charging)
        self.assertEqual(text, "充电中")

    def test_unified_battery_level_only(self):
        # 能力字节 BIT(1) 清零 = 只报档位，status[0] 无意义
        caps = bytes([0x0F, 0x00, 0x00, 0x00])
        status = bytes([0x00, 0x04, 0x00, 0x00])       # GOOD
        percent, text, charging = P.parse_logi_unified_battery(caps, status)
        self.assertEqual(percent, 50)
        self.assertEqual(text, "良好")

    def test_battery_status_0x1000(self):
        status = bytes([0x4C, 0x00, 0x01])       # 76%, 状态码 1 = 充电中
        percent, text, charging = P.parse_logi_battery_status(status)
        self.assertEqual(percent, 76)
        self.assertTrue(charging)

    def test_battery_voltage_flags(self):
        # 电压 3811mV ≈ 50%，flags BIT(7) 置位 + 阶段 0 = 充电中
        data = bytes([0x0E, 0xE3, 0x80])
        percent, text, charging = P.parse_logi_battery_voltage(data)
        self.assertEqual(percent, 50)
        self.assertTrue(charging)

    def test_voltage_curve_bounds(self):
        self.assertEqual(P.percent_from_mv(5000), 100)
        self.assertEqual(P.percent_from_mv(3500), 0)
        self.assertIsNone(P.percent_from_mv(0))


class RazerTests(unittest.TestCase):
    def test_build_frame_real(self):
        frame = P.build_razer_frame(P.RAZER_CMD_BATTERY)
        self.assertEqual(len(frame), 64)
        self.assertEqual(frame[0], 0x02)
        self.assertEqual(frame[1], 0x80)
        self.assertEqual(frame[2], 0x08)
        self.assertEqual(frame[5], 0x50)      # 'P'
        self.assertEqual(frame[6], 0x41)      # 'A'
        self.assertEqual(frame[7], 0x08)
        self.assertEqual(frame[9], 0x03)
        self.assertEqual(frame[10], P.RAZER_CMD_BATTERY)

    def test_parse_real_reply(self):
        # 本机抓包：02 0e 50 49 08 C5 72 89 02 00 04 00 21 01 01 32 ...
        reply = bytes([0x02, 0x0E, 0x50, 0x49, 0x08, 0xC5, 0x72, 0x89,
                       0x02, 0x00, 0x04, 0x00, 0x21, 0x01, 0x01, 0x32]
                      + [0] * 48)
        self.assertEqual(P.parse_razer_reply(reply, 0x21), 0x32)

    def test_parse_rejects_wrong_command(self):
        reply = bytes([0x02, 0x0E, 0x50, 0x49, 0x08, 0, 0, 0,
                       0x02, 0x00, 0x04, 0x00, 0x2A, 0x01, 0x01, 0x00]
                      + [0] * 48)
        self.assertIsNone(P.parse_razer_reply(reply, 0x21))

    def test_parse_rejects_short_frame(self):
        self.assertIsNone(P.parse_razer_reply(bytes(10), 0x21))

    def test_model_name(self):
        self.assertEqual(
            P.razer_model_name("Razer BlackShark V2 Pro 2.4", 0x0555),
            "BlackShark V2 Pro")
        self.assertEqual(P.razer_model_name("", 0x0555), "BlackShark V2 Pro")
        self.assertEqual(P.razer_model_name("HID Device", 0xFFFF), "Razer 无线耳机")


if __name__ == "__main__":
    unittest.main(verbosity=2)
