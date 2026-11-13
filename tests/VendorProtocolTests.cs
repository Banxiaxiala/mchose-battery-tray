// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [移植代码必须有断言] 这三个协议是照参考实现移植的，没有真机验证。
//    断言是唯一的正确性依据 —— 没有测试的移植等于没移植。
// 2. [失败必须回失败] 每个协议都要测「帧不对时不返回电量」，
//    特别是「不能被主动推送的状态帧骗到」。
// 3. [编码] UTF-8 with BOM。

using System;
using PeripheralBattery.Hardware;

namespace PeripheralBattery.Tests
{
    public static class VendorProtocolTests
    {
        // ============================================================ ATK
        //
        // 帧格式：04 <cmd> <tag=1A> … [8]=电量 [9]=充电
        // 出处：FerrisPulse AtkDeviceHelper

        [Test]
        public static void AtkBuildQueryLayout()
        {
            var frame = AtkVendorProtocol.BuildQuery(AtkVendorProtocol.CmdBattery);
            Assert.Equal(64, frame.Length, "64 字节报告");
            Assert.Equal((byte)0x04, frame[0], "[0] 魔数");
            Assert.Equal((byte)(AtkVendorProtocol.CmdBattery & 0xFF), frame[1], "[1] 命令低字节");
            Assert.Equal((byte)(AtkVendorProtocol.CmdBattery >> 8), frame[2], "[2] 命令高字节");
            Assert.Equal((byte)0x1A, frame[3], "[3] 协议标识");
        }

        [Test]
        public static void AtkParseBattery()
        {
            var reply = new byte[64];
            reply[0] = 0x04;
            reply[1] = (byte)(AtkVendorProtocol.CmdBattery & 0xFF);
            reply[2] = (byte)(AtkVendorProtocol.CmdBattery >> 8);
            reply[3] = 0x1A;
            reply[8] = 73;
            reply[9] = 0;

            Assert.True(AtkVendorProtocol.TryParse(reply, AtkVendorProtocol.CmdBattery,
                out int percent, out bool charging), "应解析成功");
            Assert.Equal(73, percent, "电量在 [8]");
            Assert.False(charging, "未充电");
        }

        [Test]
        public static void AtkParseCharging()
        {
            var reply = new byte[64];
            reply[0] = 0x04;
            reply[1] = (byte)(AtkVendorProtocol.CmdBattery & 0xFF);
            reply[2] = (byte)(AtkVendorProtocol.CmdBattery >> 8);
            reply[3] = 0x1A;
            reply[8] = 100;
            reply[9] = 1;

            Assert.True(AtkVendorProtocol.TryParse(reply, AtkVendorProtocol.CmdBattery,
                out int percent, out bool charging), "应解析成功");
            Assert.Equal(100, percent, "满电");
            Assert.True(charging, "充电中");
        }

        [Test]
        public static void AtkRejectsUnrelatedFrames()
        {
            // 键盘会主动推状态帧，命令号不同 → 必须拒绝，
            // 否则会把推送里的某个字节当成电量
            var pushed = new byte[64];
            pushed[0] = 0x04;
            pushed[3] = 0x1A;
            pushed[8] = 50;
            Assert.False(AtkVendorProtocol.TryParse(pushed,
                AtkVendorProtocol.CmdBattery, out _, out _), "命令号不匹配要拒绝");

            // 魔数不对
            var badMagic = new byte[64];
            badMagic[1] = (byte)(AtkVendorProtocol.CmdBattery & 0xFF);
            badMagic[2] = (byte)(AtkVendorProtocol.CmdBattery >> 8);
            badMagic[3] = 0x1A;
            badMagic[8] = 50;
            Assert.False(AtkVendorProtocol.TryParse(badMagic,
                AtkVendorProtocol.CmdBattery, out _, out _), "魔数不对要拒绝");

            // 电量越界 → 说明这不是电量帧
            var badPercent = new byte[64];
            badPercent[0] = 0x04;
            badPercent[1] = (byte)(AtkVendorProtocol.CmdBattery & 0xFF);
            badPercent[2] = (byte)(AtkVendorProtocol.CmdBattery >> 8);
            badPercent[3] = 0x1A;
            badPercent[8] = 200;
            Assert.False(AtkVendorProtocol.TryParse(badPercent,
                AtkVendorProtocol.CmdBattery, out _, out _), "电量 > 100 要拒绝");
        }

        // ============================================================ 飞智
        //
        // 帧头 5A A5（可带前导 00），电量分档换算

        [Test]
        public static void FlydigiParsesDirectPercent()
        {
            var frame = new byte[32];
            frame[0] = 0x5A;
            frame[1] = 0xA5;
            frame[2] = FlydigiProtocol.InfoCommand;
            int payload = 5;
            frame[payload] = 1;          // 设备类型
            frame[payload + 1] = 2;      // 2.4G
            // ⚠️ raw 只取低 4 位，所以上限是 15。
            // 「> 10 直接当百分比」这一档实际覆盖 11..15 ——
            // 移植时若把这里当成「87 就是 87%」会算错，特此锁定。
            frame[payload + 6] = 13;     // 电量档位 13

            Assert.True(FlydigiProtocol.TryParseInfo(frame, frame.Length,
                out int percent, out bool charging, out int mode), "应解析成功");
            Assert.Equal(13, percent, "raw 11..15 直接当百分比");
            Assert.False(charging, "高 4 位为 0 → 未充电");
            Assert.Equal(2, mode, "连接模式");
        }

        [Test]
        public static void FlydigiParsesCoarseTiers()
        {
            // raw 0..5 → *20（粗档）
            var coarse = new byte[32];
            coarse[0] = 0x5A; coarse[1] = 0xA5;
            coarse[2] = FlydigiProtocol.InfoCommand;
            coarse[5 + 6] = 3;
            Assert.True(FlydigiProtocol.TryParseInfo(coarse, coarse.Length,
                out int p1, out _, out _), "粗档应解析成功");
            Assert.Equal(60, p1, "raw=3 → 3*20 = 60");

            // raw 6..10 → *10（中档）
            var mid = new byte[32];
            mid[0] = 0x5A; mid[1] = 0xA5;
            mid[2] = FlydigiProtocol.InfoCommand;
            mid[5 + 6] = 7;
            Assert.True(FlydigiProtocol.TryParseInfo(mid, mid.Length,
                out int p2, out _, out _), "中档应解析成功");
            Assert.Equal(70, p2, "raw=7 → 7*10 = 70");
        }

        [Test]
        public static void FlydigiParsesChargingFlag()
        {
            var frame = new byte[32];
            frame[0] = 0x5A; frame[1] = 0xA5;
            frame[2] = FlydigiProtocol.InfoCommand;
            // 高 4 位 = 1 → 充电中；低 4 位 = 12 → 12%
            // （低 4 位装不下 50，这也是移植时最容易搞错的点）
            frame[5 + 6] = (byte)((1 << 4) | 12);

            Assert.True(FlydigiProtocol.TryParseInfo(frame, frame.Length,
                out int percent, out bool charging, out _), "应解析成功");
            Assert.Equal(12, percent, "低 4 位电量");
            Assert.True(charging, "高 4 位充电标志");
        }

        [Test]
        public static void FlydigiAcceptsLeadingZero()
        {
            // 带前导 0x00 的形态也要认
            var frame = new byte[32];
            frame[0] = 0x00;
            frame[1] = 0x5A;
            frame[2] = 0xA5;
            frame[3] = FlydigiProtocol.InfoCommand;
            frame[1 + 5 + 6] = 14;

            Assert.True(FlydigiProtocol.TryParseInfo(frame, frame.Length,
                out int percent, out _, out _), "带前导 0 也要解析");
            Assert.Equal(14, percent, "电量档位 14");
        }

        [Test]
        public static void FlydigiRejectsWrongCommand()
        {
            var frame = new byte[32];
            frame[0] = 0x5A; frame[1] = 0xA5;
            frame[2] = 0x02;      // 不是 InfoCommand
            frame[5 + 6] = 10;
            Assert.False(FlydigiProtocol.TryParseInfo(frame, frame.Length,
                out _, out _, out _), "命令号不对要拒绝");
        }

        // ============================================================ 漫步者
        //
        // EC 帧 + XOR 0xA5 + 累加和

        [Test]
        public static void EdifierBuildsFrameWithChecksum()
        {
            var frame = EdifierProtocol.BuildFrame(
                EdifierProtocol.CmdBatteryQuery, Array.Empty<byte>());
            Assert.NotNull(frame, "应能构造");
            Assert.Equal(64, frame!.Length, "64 字节");
            Assert.Equal(EdifierProtocol.TxCommon, frame[0], "[0] 2E");
            Assert.Equal(EdifierProtocol.TxHeader, frame[1], "[1] AA");
            Assert.Equal(EdifierProtocol.DeviceTypeEc, frame[2], "[2] EC");
            Assert.Equal(EdifierProtocol.CmdBatteryQuery, frame[3], "[3] 命令");

            // 空载荷：crcPos = 6
            byte expected = EdifierProtocol.Checksum(frame, 1, 5);
            Assert.Equal(expected, frame[6], "校验和从下标 1 起算");
        }

        [Test]
        public static void EdifierMasksOnlyPayload()
        {
            // 载荷字节参与 XOR，头与长度是明文
            var frame = EdifierProtocol.BuildFrame(0xD0, new byte[] { 0x00, 0xFF });
            Assert.NotNull(frame, "应能构造");
            Assert.Equal((byte)(0x00 ^ 0xA5), frame![6], "载荷 0x00 变成 0xA5");
            Assert.Equal((byte)(0xFF ^ 0xA5), frame[7], "载荷 0xFF 变成 0x5A");
            Assert.Equal(EdifierProtocol.TxCommon, frame[0], "头部不参与掩码");
        }

        [Test]
        public static void EdifierRoundTripParsesBattery()
        {
            // 构造一条合法的应答帧，验证能解出电量
            var reply = new byte[64];
            // 应答头：report[0] 是 report id（0），report[1] = 0x2F
            reply[0] = 0x00;
            reply[1] = EdifierProtocol.RxCommon;              // 2F
            reply[2] = EdifierProtocol.DeviceTypeEc;          // EC
            reply[3] = EdifierProtocol.CmdBatteryQuery;       // 索引
            reply[4] = 0;                                     // lenHi
            reply[5] = 1;                                     // lenLo = 1
            reply[6] = (byte)(95 ^ 0xA5);                     // 掩码后的电量 95
            // 校验覆盖 [baseOffset .. crcPos-1]，baseOffset=1、crcPos=7 → [1..6]。
            // 必须先写好所有参与校验的字节再算，顺序反了就是错的。
            reply[7] = EdifierProtocol.Checksum(reply, 1, 6);

            Assert.True(EdifierProtocol.TryParseReport(reply,
                EdifierProtocol.CmdBatteryQuery, out byte[] payload), "应解析成功");
            Assert.Equal(1, payload.Length, "解掩码后载荷长度");
            Assert.Equal(95, payload[0], "解掩码后是 95");

            Assert.True(EdifierProtocol.TryReadBattery(payload, out int percent),
                "应能读出电量");
            Assert.Equal(95, percent, "电量值");
        }

        [Test]
        public static void EdifierRejectsBadChecksum()
        {
            var reply = new byte[64];
            reply[0] = 0x00;
            reply[1] = EdifierProtocol.RxCommon;
            reply[2] = EdifierProtocol.DeviceTypeEc;
            reply[3] = EdifierProtocol.CmdBatteryQuery;
            reply[4] = 0;
            reply[5] = 1;
            reply[6] = (byte)(95 ^ 0xA5);
            reply[7] = 0x00;   // 故意写错校验

            Assert.False(EdifierProtocol.TryParseReport(reply,
                EdifierProtocol.CmdBatteryQuery, out _), "校验错必须拒绝");
        }

        [Test]
        public static void EdifierRejectsWrongIndex()
        {
            var reply = new byte[64];
            reply[0] = 0x00;
            reply[1] = EdifierProtocol.RxCommon;
            reply[2] = EdifierProtocol.DeviceTypeEc;
            reply[3] = 0x01;   // 不是电量命令
            reply[4] = 0;
            reply[5] = 1;
            reply[6] = (byte)(95 ^ 0xA5);
            reply[7] = EdifierProtocol.Checksum(reply, 1, 6);

            Assert.False(EdifierProtocol.TryParseReport(reply,
                EdifierProtocol.CmdBatteryQuery, out _), "索引不符必须拒绝");
        }

        [Test]
        public static void EdifierRejectsOutOfRangeBattery()
        {
            Assert.False(EdifierProtocol.TryReadBattery(new byte[] { 200 }, out _),
                "电量 > 100 不可信");
            Assert.False(EdifierProtocol.TryReadBattery(System.Array.Empty<byte>(), out _),
                "空载荷");
            Assert.False(EdifierProtocol.TryReadBattery(null, out _), "null 安全");
        }

        [Test]
        public static void EdifierMaskIsSymmetric()
        {
            var original = new byte[] { 1, 2, 3, 250 };
            var masked = EdifierProtocol.MaskPayload(original);
            var back = EdifierProtocol.MaskPayload(masked);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i], back[i], "XOR 两次应还原");
            }
        }
    }
}
