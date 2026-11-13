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

        // ============================================================ 迈从键盘

        [Test]
        public static void MchoseKeyboardBuildsChecksumFrame()
        {
            var frame = MchoseKeyboardProtocol.BuildQuery();
            Assert.Equal(MchoseKeyboardProtocol.ReportLength, frame.Length, "65 字节");
            Assert.Equal(MchoseKeyboardProtocol.ReportId, frame[0], "[0] report id 0x13");
            Assert.Equal(MchoseKeyboardProtocol.CmdWirelessBattery, frame[1], "[1] 命令 0x4A");

            int sum = 0;
            for (int i = 0; i <= 18; i++) sum += frame[i];
            Assert.Equal((byte)(sum & 0xFF), frame[19], "[19] 累加和（前 19 字节）");
        }

        [Test]
        public static void MchoseKeyboardShortFrameIs20Bytes()
        {
            // G75 PRO 端点只收 20 字节，发 65 会被忽略
            var frame = MchoseKeyboardProtocol.BuildShortQuery();
            Assert.Equal(20, frame.Length, "短帧必须恰好 20 字节");

            int sum = 0;
            for (int i = 0; i < 19; i++) sum += frame[i];
            Assert.Equal((byte)(sum & 0xFF), frame[19], "校验覆盖前 19 字节");
        }

        [Test]
        public static void MchoseKeyboardParsesBattery()
        {
            var reply = new byte[20];
            reply[0] = MchoseKeyboardProtocol.ReportId;
            reply[1] = MchoseKeyboardProtocol.CmdWirelessBattery;
            reply[MchoseKeyboardProtocol.BatteryOffset] = 66;
            reply[MchoseKeyboardProtocol.StateOffset] = MchoseKeyboardProtocol.StateCharging;

            int sum = 0;
            for (int i = 0; i < 19; i++) sum += reply[i];
            reply[19] = (byte)(sum & 0xFF);

            Assert.True(MchoseKeyboardProtocol.TryParse(reply, 20,
                out int percent, out bool charging), "应解析成功");
            Assert.Equal(66, percent, "电量在 [5]");
            Assert.True(charging, "状态 0x10 = 充电");
        }

        [Test]
        public static void MchoseKeyboardRejectsBadChecksum()
        {
            var reply = new byte[20];
            reply[0] = MchoseKeyboardProtocol.ReportId;
            reply[1] = MchoseKeyboardProtocol.CmdWirelessBattery;
            reply[MchoseKeyboardProtocol.BatteryOffset] = 66;
            reply[MchoseKeyboardProtocol.StateOffset] = MchoseKeyboardProtocol.StateDischarging;
            reply[19] = 0x00;   // 故意写错

            Assert.False(MchoseKeyboardProtocol.TryParse(reply, 20, out _, out _),
                "校验和错必须拒绝");
        }

        [Test]
        public static void MchoseKeyboardMasksBit7OfCommand()
        {
            // 应答命令号的 bit7 是标志位，比对时必须屏蔽
            var reply = new byte[20];
            reply[0] = MchoseKeyboardProtocol.ReportId;
            reply[1] = (byte)(MchoseKeyboardProtocol.CmdWirelessBattery | 0x80);
            reply[MchoseKeyboardProtocol.BatteryOffset] = 50;
            reply[MchoseKeyboardProtocol.StateOffset] = MchoseKeyboardProtocol.StateDischarging;

            int sum = 0;
            for (int i = 0; i < 19; i++) sum += reply[i];
            reply[19] = (byte)(sum & 0xFF);

            Assert.True(MchoseKeyboardProtocol.TryParse(reply, 20,
                out int percent, out _), "bit7 置位也要能解析");
            Assert.Equal(50, percent, "电量");
        }

        // ============================================================ NuPhy

        [Test]
        public static void NuphyBuildQueryLayout()
        {
            var frame = NuphyProtocol.BuildQuery(NuphyProtocol.CmdGetBattery, 96, 1);
            Assert.Equal(NuphyProtocol.ReportLength, frame.Length, "65 字节");
            Assert.Equal((byte)0x00, frame[0], "[0] 0x00");
            Assert.Equal((byte)0x55, frame[1], "[1] 0x55");
            Assert.Equal(NuphyProtocol.CmdGetBattery, frame[2], "[2] 命令");
            Assert.Equal((byte)1, frame[5], "[5] 长度");
            Assert.Equal((byte)96, frame[6], "[6] 偏移低字节");
            Assert.Equal((byte)0, frame[7], "[7] 偏移高字节");
        }

        [Test]
        public static void NuphyOffsetDependsOnMode()
        {
            // 偏移 = 模式 × 64 + 32，跳过读模式这一步就不知道偏移
            Assert.Equal((ushort)32, NuphyProtocol.BatteryOffsetForMode(0), "模式 0");
            Assert.Equal((ushort)96, NuphyProtocol.BatteryOffsetForMode(1), "模式 1");
            Assert.Equal((ushort)160, NuphyProtocol.BatteryOffsetForMode(2), "模式 2");
            Assert.Equal((ushort)224, NuphyProtocol.BatteryOffsetForMode(3), "模式 3");
        }

        [Test]
        public static void NuphyParsesMode()
        {
            var reply = new byte[65];
            reply[9] = 2;
            Assert.Equal(2, NuphyProtocol.ParseMode(reply), "模式从 [9] 取");

            reply[9] = 9;   // 越界
            Assert.Equal(-1, NuphyProtocol.ParseMode(reply), "越界返回 -1");
        }

        [Test]
        public static void NuphyParsesBatteryMaskingBit7()
        {
            var reply = new byte[65];
            reply[1] = 0xAA;
            reply[2] = NuphyProtocol.CmdGetBattery;
            // bit7 是标志位，0xC8 = 200 → &0x7F = 72
            reply[NuphyProtocol.BatteryFieldOffset] = 0xC8;

            Assert.True(NuphyProtocol.TryParseBattery(reply, out int percent),
                "应解析成功");
            Assert.Equal(72, percent, "必须屏蔽 bit7");
        }

        [Test]
        public static void NuphyRejectsWrongEcho()
        {
            var reply = new byte[65];
            reply[1] = 0xAA;
            reply[2] = NuphyProtocol.CmdGetMode;   // 不是电量命令
            reply[NuphyProtocol.BatteryFieldOffset] = 72;

            Assert.False(NuphyProtocol.TryParseBattery(reply, out _),
                "命令回显不符要拒绝");
        }

        // ============================================================ 宏碁
        //
        // 33 字节，命令字节位置要扫描（带/不带 report id）

        private static byte[] MakeAcerReply(int commandIndex, int percent, bool charging)
        {
            var reply = new byte[AcerProtocol.ReportLength];
            reply[commandIndex] = AcerProtocol.CommandDeviceInfo;
            reply[commandIndex + 1] = 0x00;
            reply[commandIndex + 3] = 13;             // 载荷长度 ≥ 11
            reply[commandIndex + 4] = (byte)'M';      // 设备 ID 必须是可打印 ASCII
            reply[commandIndex + 5] = (byte)'2';
            reply[commandIndex + 6] = (byte)'2';
            reply[commandIndex + 7] = (byte)'0';
            reply[commandIndex + AcerProtocol.ChargingOffset] = charging ? (byte)1 : (byte)0;
            reply[commandIndex + AcerProtocol.BatteryOffset] = (byte)percent;
            return reply;
        }

        [Test]
        public static void AcerParsesBatteryWithoutReportId()
        {
            var reply = MakeAcerReply(0, 58, false);
            Assert.True(AcerProtocol.TryParse(reply, out int percent, out bool charging),
                "命令字节在 [0] 时应解析成功");
            Assert.Equal(58, percent, "电量在 [13]");
            Assert.False(charging, "未充电");
        }

        [Test]
        public static void AcerParsesBatteryWithReportId()
        {
            // 带 report id 时命令字节后移一位。写死下标 0 的实现在这里必错。
            var reply = MakeAcerReply(1, 72, true);
            Assert.True(AcerProtocol.TryParse(reply, out int percent, out bool charging),
                "命令字节在 [1] 时也要能解析");
            Assert.Equal(72, percent, "电量");
            Assert.True(charging, "充电中");
        }

        [Test]
        public static void AcerRejectsNonAsciiDeviceId()
        {
            var reply = MakeAcerReply(0, 58, false);
            reply[4] = 0x01;   // 不可打印
            Assert.False(AcerProtocol.TryParse(reply, out _, out _),
                "设备 ID 非 ASCII 要拒绝");
        }

        [Test]
        public static void AcerChargingSentinelFallsBackToLastReal()
        {
            // 充电时设备把电量填成 100 当哨兵，必须退回上次真实读数
            Assert.Equal(58, AcerProtocol.ResolveDisplayed(100, true, 58),
                "充电读到 100 → 退回上次的 58");
            Assert.Equal(45, AcerProtocol.ResolveDisplayed(45, true, 58),
                "充电读到 45（不是哨兵）→ 原样用");
            Assert.Equal(100, AcerProtocol.ResolveDisplayed(100, true, -1),
                "没有历史值时原样返回，不编数字");
            Assert.Equal(30, AcerProtocol.ResolveDisplayed(30, false, 58),
                "未充电时总是原样返回");
        }

        // ============================================================ HyperX
        //
        // report id 0x66，电量命令 0x89 → 响应 0x0D

        [Test]
        public static void HyperXBuildQueryLayout()
        {
            var frame = HyperXProtocol.BuildQuery(HyperXProtocol.CmdGetBattery);
            Assert.Equal(HyperXProtocol.PacketLength, frame.Length, "62 字节");
            Assert.Equal(HyperXProtocol.ReportId, frame[0], "[0] report id 0x66");
            Assert.Equal(HyperXProtocol.CmdGetBattery, frame[1], "[1] 命令码");
        }

        [Test]
        public static void HyperXParsesBattery()
        {
            var reply = new byte[HyperXProtocol.PacketLength - 1];
            reply[0] = HyperXProtocol.ReportId;
            reply[1] = HyperXProtocol.RspBattery;
            reply[HyperXProtocol.OffsetLevel] = 42;

            Assert.True(HyperXProtocol.TryParseBattery(reply, out int percent),
                "应解析成功");
            Assert.Equal(42, percent, "电量在 [4]");
        }

        [Test]
        public static void HyperXParsesCharging()
        {
            var reply = new byte[HyperXProtocol.PacketLength - 1];
            reply[0] = HyperXProtocol.ReportId;
            reply[1] = HyperXProtocol.RspCharging;
            reply[HyperXProtocol.OffsetLevel] = 1;

            Assert.True(HyperXProtocol.TryParseCharging(reply, out bool charging),
                "应解析成功");
            Assert.True(charging, "充电中");
        }

        [Test]
        public static void HyperXRejectsWrongResponseCode()
        {
            // 用充电应答去当电量解析 → 必须失败。
            // 两条命令的响应码不同（0x0C vs 0x0D），不校验就会串。
            var reply = new byte[HyperXProtocol.PacketLength - 1];
            reply[0] = HyperXProtocol.ReportId;
            reply[1] = HyperXProtocol.RspCharging;
            reply[HyperXProtocol.OffsetLevel] = 42;

            Assert.False(HyperXProtocol.TryParseBattery(reply, out _),
                "响应码不符要拒绝");
        }

        [Test]
        public static void HyperXRejectsOutOfRange()
        {
            var reply = new byte[HyperXProtocol.PacketLength - 1];
            reply[0] = HyperXProtocol.ReportId;
            reply[1] = HyperXProtocol.RspBattery;
            reply[HyperXProtocol.OffsetLevel] = 150;

            Assert.False(HyperXProtocol.TryParseBattery(reply, out _),
                "电量 > 100 要拒绝");
        }

        // ============================================================ 盖世小鸡
        //
        // 65 字节，usagePage 0xFF7A。注意 255 是「充电中但无百分比」哨兵。

        [Test]
        public static void GameSirParsesBattery()
        {
            var reply = new byte[GameSirProtocol.ReportLength];
            reply[0] = 0x00;
            reply[1] = 0x01;
            reply[2] = 0xAB;   // 设备标识（非全零）
            reply[GameSirProtocol.BatteryOffset] = 63;

            Assert.True(GameSirProtocol.TryParse(reply, out int percent,
                out bool charging, out bool chargingOnly), "应解析成功");
            Assert.Equal(63, percent, "电量在 [19]");
            Assert.False(charging, "未充电");
            Assert.False(chargingOnly, "不是纯充电帧");
        }

        [Test]
        public static void GameSirChargingSentinelIsNotAPercent()
        {
            // 255 是「正在充电，固件不给百分比」。
            // 当成 100% 会误导（以为充满），当成 0% 会误报低电量。
            var reply = new byte[GameSirProtocol.ReportLength];
            reply[0] = 0x00;
            reply[1] = 0x01;
            reply[2] = 0xAB;
            reply[GameSirProtocol.BatteryOffset] = GameSirProtocol.ChargingSentinel;

            Assert.True(GameSirProtocol.TryParse(reply, out int percent,
                out bool charging, out bool chargingOnly), "哨兵帧是合法状态，不是错误");
            Assert.Equal(-1, percent, "不能给出百分比");
            Assert.True(charging, "要标为充电中");
            Assert.True(chargingOnly, "要标为「只有充电状态」");
        }

        [Test]
        public static void GameSirRejectsEmptyIdentity()
        {
            // 设备标识全零 = 空帧
            var reply = new byte[GameSirProtocol.ReportLength];
            reply[0] = 0x00;
            reply[1] = 0x01;
            reply[GameSirProtocol.BatteryOffset] = 50;

            Assert.False(GameSirProtocol.TryParse(reply, out _, out _, out _),
                "标识全零要拒绝");
        }

        [Test]
        public static void GameSirRejectsWrongFrameHeader()
        {
            var reply = new byte[GameSirProtocol.ReportLength];
            reply[0] = 0xFF;   // 不是 0x00
            reply[1] = 0x01;
            reply[2] = 0xAB;

            Assert.False(GameSirProtocol.TryParse(reply, out _, out _, out _),
                "帧头不对要拒绝");
        }

        // ============================================================ 华硕 ROG
        //
        // CC 12 帧头，电量 key 7（[6]）、充电 key 8（[5]）

        [Test]
        public static void AsusParsesBattery()
        {
            var reply = new byte[61];
            reply[0] = AsusProtocol.Magic0;
            reply[1] = AsusProtocol.Magic1;
            reply[2] = AsusProtocol.KeyBattery;
            reply[3] = 0x00;
            reply[4] = 0x00;
            reply[6] = 88;

            Assert.True(AsusProtocol.TryParseBattery(reply, out int percent),
                "应解析成功");
            Assert.Equal(88, percent, "电量在 [6]");
        }

        [Test]
        public static void AsusParsesCharging()
        {
            var reply = new byte[61];
            reply[0] = AsusProtocol.Magic0;
            reply[1] = AsusProtocol.Magic1;
            reply[2] = AsusProtocol.KeyCharging;
            reply[3] = 0x00;
            reply[4] = 0x00;
            reply[5] = 0x01;

            Assert.True(AsusProtocol.TryParseCharging(reply, out bool charging),
                "应解析成功");
            Assert.True(charging, "充电中");
        }

        [Test]
        public static void AsusRejectsPlaceholderFrame()
        {
            // [5]=FF [6]=AA 是设备的「未就绪占位帧」。
            // 字段位置恰好合法，不排除就会读出垃圾电量。
            var reply = new byte[61];
            reply[0] = AsusProtocol.Magic0;
            reply[1] = AsusProtocol.Magic1;
            reply[2] = AsusProtocol.KeyBattery;
            reply[5] = 0xFF;
            reply[6] = 0xAA;

            Assert.False(AsusProtocol.TryParseBattery(reply, out _),
                "占位帧必须被排除");
        }

        [Test]
        public static void AsusRejectsWrongKey()
        {
            // 用 key 8 的应答去当电量解析 → 必须失败
            var reply = new byte[61];
            reply[0] = AsusProtocol.Magic0;
            reply[1] = AsusProtocol.Magic1;
            reply[2] = AsusProtocol.KeyCharging;
            reply[6] = 88;

            Assert.False(AsusProtocol.TryParseBattery(reply, out _),
                "key 不符要拒绝");
        }

        [Test]
        public static void AsusRejectsOutOfRange()
        {
            var reply = new byte[61];
            reply[0] = AsusProtocol.Magic0;
            reply[1] = AsusProtocol.Magic1;
            reply[2] = AsusProtocol.KeyBattery;
            reply[6] = 200;

            Assert.False(AsusProtocol.TryParseBattery(reply, out _),
                "电量 > 100 要拒绝");

            var charge = new byte[61];
            charge[0] = AsusProtocol.Magic0;
            charge[1] = AsusProtocol.Magic1;
            charge[2] = AsusProtocol.KeyCharging;
            charge[5] = 5;   // 只能是 0/1

            Assert.False(AsusProtocol.TryParseCharging(charge, out _),
                "充电标志只能是 0/1");
        }

        // ============================================================ 迈从
        //
        // 唯一走 Feature Report 的协议：数据整体异或 0xFF

        [Test]
        public static void MchoseRequestIsFullyInverted()
        {
            var frame = MchoseMouseProtocol.BuildIdentityRequest();
            Assert.Equal(MchoseMouseProtocol.ShortPayload + 1, frame.Length,
                "21 字节（1B report id + 20B 载荷）");
            Assert.Equal(MchoseMouseProtocol.ReportShort, frame[0], "[0] report id 不取反");
            Assert.Equal((byte)(MchoseMouseProtocol.CmdIdentity ^ 0xFF), frame[1],
                "[1] 命令号取反");

            // 空白填充字节必须是 0xFF（= 0x00 取反），不是 0
            for (int i = 2; i < frame.Length; i++)
            {
                Assert.Equal((byte)0xFF, frame[i], "填充字节应为 0xFF");
            }
        }

        [Test]
        public static void MchoseParsesIdentity()
        {
            var response = new byte[MchoseMouseProtocol.ShortPayload + 1];
            response[0] = MchoseMouseProtocol.ReportShort;
            response[1] = (byte)(MchoseMouseProtocol.CmdIdentity ^ 0xFF);

            // 构造解掩码后的载荷：VID 0x3837 / PID 0x1234 / 电量 77 / 充电 1
            var payload = new byte[MchoseMouseProtocol.ShortPayload - 1];
            payload[0] = 0x37; payload[1] = 0x38;
            payload[2] = 0x34; payload[3] = 0x12;
            payload[9] = 77;
            payload[10] = 1;

            for (int i = 0; i < payload.Length; i++)
            {
                response[i + 2] = (byte)(payload[i] ^ 0xFF);
            }

            Assert.True(MchoseMouseProtocol.TryParseIdentity(response,
                out int percent, out bool charging, out int vid, out int pid),
                "应解析成功");
            Assert.Equal(77, percent, "电量在解掩码后的 [9]");
            Assert.True(charging, "充电标志");
            Assert.Equal(0x3837, vid, "VID");
            Assert.Equal(0x1234, pid, "PID");
        }

        [Test]
        public static void MchoseRejectsWrongEcho()
        {
            // 命令号回显不符 → 必须拒绝，否则会把主动状态帧当电量
            var response = new byte[MchoseMouseProtocol.ShortPayload + 1];
            response[0] = MchoseMouseProtocol.ReportShort;
            response[1] = 0x00;
            Assert.False(MchoseMouseProtocol.TryParseIdentity(response,
                out _, out _, out _, out _), "回显不符要拒绝");
        }

        [Test]
        public static void MchoseRejectsUnreadyDevice()
        {
            // VID/PID 全零 = 设备还没准备好，不能当成 0% 电量
            var response = new byte[MchoseMouseProtocol.ShortPayload + 1];
            response[0] = MchoseMouseProtocol.ReportShort;
            response[1] = (byte)(MchoseMouseProtocol.CmdIdentity ^ 0xFF);
            for (int i = 2; i < response.Length; i++) response[i] = 0xFF;  // 全零载荷取反

            Assert.False(MchoseMouseProtocol.TryParseIdentity(response,
                out _, out _, out _, out _), "VID/PID 全零要拒绝");
        }

        [Test]
        public static void MchoseRejectsOutOfRangeBattery()
        {
            var response = new byte[MchoseMouseProtocol.ShortPayload + 1];
            response[0] = MchoseMouseProtocol.ReportShort;
            response[1] = (byte)(MchoseMouseProtocol.CmdIdentity ^ 0xFF);

            var payload = new byte[MchoseMouseProtocol.ShortPayload - 1];
            payload[0] = 0x37; payload[1] = 0x38;   // 有 VID，排除「未就绪」
            payload[9] = 200;                        // 电量越界
            for (int i = 0; i < payload.Length; i++)
            {
                response[i + 2] = (byte)(payload[i] ^ 0xFF);
            }

            Assert.False(MchoseMouseProtocol.TryParseIdentity(response,
                out _, out _, out _, out _), "电量 > 100 要拒绝");
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
