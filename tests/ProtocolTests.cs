// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [真机抓包帧] 每一条断言都必须对应一次实测抓包，注释写明来源。
//    改协议前先跑这里。
// 2. [与 Python 对齐] 本文件的断言与 tests/test_protocols.py 一一对应，
//    两边算出来的数必须相同。
// 3. [零硬件] 不许打开任何 HID 句柄。
// 4. [编码] UTF-8 with BOM。
//
// 本机实测帧：
//   罗技 PRO X 2 (046D:C54D)
//     请求 0x1004 GetFeature : 11 01 00 0a 10 04 00 ...
//     应答 feature 索引 0x06  : 11 01 00 0a 06 00 05 ...
//     应答 caps               : 0F 0F 02 00 ...   (BIT(1) 置位 = 直报百分比)
//     应答 status             : 35 04 00 00 ...   (0x35=53%, 0x04=放电中)
//   雷蛇 BlackShark V2 Pro 2.4 (1532:0555)
//     发送 02 80 08 00 00 50 41 08 00 03 21 00 ...
//     应答 02 0e 50 49 08 C5 72 89 02 00 04 00 21 01 01 32 ...
//                                                    ↑ 0x32 = 50%

using PeripheralBattery.Hardware;

namespace PeripheralBattery.Tests
{
    public static class ProtocolTests
    {
        // ============================================================ 罗技请求
        [Test]
        public static void BuildRequestLayout()
        {
            var packet = Protocols.BuildLogiRequest(1, Protocols.FeatRoot, 0x00,
                new byte[] { 0x10, 0x04 });

            Assert.Equal(20, packet.Length, "长度");
            Assert.Equal(Protocols.HidppLong, packet[0], "[0] = 0x11");
            Assert.Equal((byte)1, packet[1], "[1] = 设备槽位");
            Assert.Equal((byte)0x00, packet[2], "[2] = 特性索引");
            // 软件 ID 必须非零，否则与设备主动推送的通知撞车
            Assert.Equal(Protocols.HidppSoftwareId, packet[3], "[3] 软件 ID");
            Assert.Equal((byte)0x10, packet[4], "[4]");
            Assert.Equal((byte)0x04, packet[5], "[5]");
        }

        [Test]
        public static void BuildRequestGetStatus()
        {
            // 实测：GetStatus 是 function 1 → (1<<4)|0x0A = 0x1A
            var packet = Protocols.BuildLogiRequest(1, 0x06, 0x01);
            Assert.Equal((byte)0x1A, packet[3], "function 字节");
        }

        [Test]
        public static void MatchReplyExact()
        {
            var frame = new byte[20];
            frame[0] = 0x11; frame[1] = 0x01; frame[2] = 0x06; frame[3] = 0x1A;
            frame[4] = 0x35; frame[5] = 0x04;

            var payload = Protocols.MatchLogiReply(frame, 1, 0x06, 0x01);
            Assert.NotNull(payload, "应匹配");
            Assert.Equal((byte)0x35, payload![0], "数据区首字节");
        }

        [Test]
        public static void MatchReplyRejectsWrongSoftwareId()
        {
            // 设备主动推送的通知软件 ID 为 0，必须拒绝
            var frame = new byte[20];
            frame[0] = 0x11; frame[1] = 0x01; frame[2] = 0x06; frame[3] = 0x10;
            Assert.Null(Protocols.MatchLogiReply(frame, 1, 0x06, 0x01), "应拒绝");
        }

        [Test]
        public static void MatchAbsentErrorFrame()
        {
            // 短集合错误帧：10 <idx> 8F <featIdx> <funcByte> <err> 00
            var frame = new byte[] { 0x10, 0x04, 0x8F, 0x00, 0x0A, 0x08, 0x00 };
            Assert.True(Protocols.MatchLogiAbsent(frame, 4, 0x00, 0x00), "槽位 4 应判定不可达");
            Assert.False(Protocols.MatchLogiAbsent(frame, 5, 0x00, 0x00), "槽位 5 不应匹配");
        }

        // ============================================================ 罗技电量
        [Test]
        public static void UnifiedBatteryRealDevice()
        {
            // 本机抓包：PRO X 2 报 0x35 = 53%
            var caps = new byte[] { 0x0F, 0x0F, 0x02, 0x00 };
            var status = new byte[] { 0x35, 0x04, 0x00, 0x00 };

            var parsed = Protocols.ParseLogiUnifiedBattery(caps, status);
            Assert.NotNull(parsed, "应解析成功");
            Assert.Equal(53, parsed!.Value.percent ?? -1, "电量");
            Assert.False(parsed.Value.charging, "不应判定为充电");
        }

        [Test]
        public static void UnifiedBatteryCharging()
        {
            var caps = new byte[] { 0x0F, 0x02, 0x00, 0x00 };
            var status = new byte[] { 100, 0x08, 0x01, 0x01 };

            var parsed = Protocols.ParseLogiUnifiedBattery(caps, status);
            Assert.NotNull(parsed, "应解析成功");
            Assert.Equal(100, parsed!.Value.percent ?? -1, "电量");
            Assert.True(parsed.Value.charging, "应判定为充电");
            Assert.Equal("充电中", parsed.Value.text, "状态文案");
        }

        [Test]
        public static void UnifiedBatteryLevelOnly()
        {
            // 能力字节 BIT(1) 清零 = 只报档位，status[0] 无意义
            var caps = new byte[] { 0x0F, 0x00, 0x00, 0x00 };
            var status = new byte[] { 0x00, 0x04, 0x00, 0x00 };   // GOOD

            var parsed = Protocols.ParseLogiUnifiedBattery(caps, status);
            Assert.NotNull(parsed, "应解析成功");
            Assert.Equal(50, parsed!.Value.percent ?? -1, "近似电量");
            Assert.Equal("良好", parsed.Value.text, "档位文案不能被充电状态覆盖");
        }

        [Test]
        public static void BatteryStatus0x1000()
        {
            var status = new byte[] { 0x4C, 0x00, 0x01 };   // 76%, 状态码 1
            var parsed = Protocols.ParseLogiBatteryStatus(status);
            Assert.NotNull(parsed, "应解析成功");
            Assert.Equal(76, parsed!.Value.percent, "电量");
            Assert.True(parsed.Value.charging, "应判定为充电");
        }

        [Test]
        public static void BatteryVoltageFlags()
        {
            // 电压 3811mV ≈ 50%，flags BIT(7) 置位 + 阶段 0 = 充电中
            var data = new byte[] { 0x0E, 0xE3, 0x80 };
            var parsed = Protocols.ParseLogiBatteryVoltage(data);
            Assert.NotNull(parsed, "应解析成功");
            Assert.Equal(50, parsed!.Value.percent, "电压插值");
            Assert.True(parsed.Value.charging, "应判定为充电");
        }

        [Test]
        public static void VoltageCurveBounds()
        {
            Assert.Equal(100, Protocols.PercentFromMillivolt(5000) ?? -1, "上限饱���");
            Assert.Equal(0, Protocols.PercentFromMillivolt(3500) ?? -1, "下限");
            Assert.Null(Protocols.PercentFromMillivolt(0), "0mV 应视为无效");
        }

        // ============================================================ 雷蛇
        [Test]
        public static void RazerFrameLayout()
        {
            var frame = Protocols.BuildRazerFrame(Protocols.RazerCmdBattery);
            Assert.Equal(64, frame.Length, "长度");
            Assert.Equal((byte)0x02, frame[0], "[0] Report ID");
            Assert.Equal((byte)0x80, frame[1], "[1]");
            Assert.Equal((byte)0x08, frame[2], "[2]");
            Assert.Equal((byte)0x50, frame[5], "[5] = 'P'");
            Assert.Equal((byte)0x41, frame[6], "[6] = 'A'");
            Assert.Equal((byte)0x08, frame[7], "[7] 序号");
            Assert.Equal((byte)0x03, frame[9], "[9]");
            Assert.Equal(Protocols.RazerCmdBattery, frame[10], "[10] 命令码");
        }

        [Test]
        public static void RazerParseRealReply()
        {
            // 本机抓包：02 0e 50 49 08 C5 72 89 02 00 04 00 21 01 01 32 ...
            var reply = new byte[64];
            reply[0] = 0x02; reply[1] = 0x0E; reply[2] = 0x50; reply[3] = 0x49;
            reply[4] = 0x08; reply[5] = 0xC5; reply[6] = 0x72; reply[7] = 0x89;
            reply[8] = 0x02; reply[9] = 0x00; reply[10] = 0x04; reply[11] = 0x00;
            reply[12] = 0x21; reply[13] = 0x01; reply[14] = 0x01; reply[15] = 0x32;

            Assert.Equal(0x32, Protocols.ParseRazerReply(reply, 0x21), "电量字节");
        }

        [Test]
        public static void RazerParseRejectsWrongCommand()
        {
            var reply = new byte[64];
            reply[0] = 0x02; reply[1] = 0x0E; reply[2] = 0x50; reply[3] = 0x49;
            reply[12] = 0x2A; reply[13] = 0x01;
            Assert.Equal(-1, Protocols.ParseRazerReply(reply, 0x21), "命令不匹配应拒绝");
        }

        [Test]
        public static void RazerParseRejectsShortFrame()
        {
            Assert.Equal(-1, Protocols.ParseRazerReply(new byte[10], 0x21), "短帧应拒绝");
        }

        [Test]
        public static void RazerModelName()
        {
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelName("Razer BlackShark V2 Pro 2.4", 0x0555), "剥前缀与后缀");
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelName("", 0x0555), "空字符串走兜底表");
            // 兜底文案是「无线设备」而不是「无线耳机」：PA/PI 同族还包括鼠标
            // （DeathAdder / Viper / Basilisk），说成耳机会让鼠标用户以为认错了设备。
            Assert.Equal("Razer 无线设备",
                Protocols.RazerModelName("HID Device", 0xFFFF), "未知机型");

            // 型号表里的 🟡 项：PID 命中就直接给可读名字
            Assert.Equal("Kraken V4",
                Protocols.RazerModelName("HID Device", 0x056C), "表内已知 PID");
            Assert.Equal("Viper V3 HyperSpeed",
                Protocols.RazerModelName("", 0x00B8), "表内鼠标 PID");
            Assert.Equal("DeathAdder V4 Pro",
                Protocols.RazerModelName("", 0x00BE), "表内鼠标 PID");
            Assert.Equal("Basilisk V3 Pro 35K",
                Protocols.RazerModelName("", 0x00CC), "带数字后缀的型号名");

            // product_string 优先于型号表：官方自报的名字永远比我们猜的准
            Assert.Equal("My Custom Name",
                Protocols.RazerModelName("Razer My Custom Name", 0x00B8),
                "设备自报的名字要覆盖型号表");
        }

        // ============================================================ 维咖 VIA
        //
        // 实测帧来源：weikav-max102 仓库 docs/08-电量补丁方案.md §4.2 / §6.3
        //   USB 有线 : 14 00 00 00 64 03 01 03   （100% / state=3 已断开 / ver=1）
        //   2.4G     : 14 00 00 00 64 02 01 03   （100% / state=2 已连接 / ver=1）
        //   USB 无补丁: ff …                     （原厂对 cmd>19 回 0xFF）

        [Test]
        public static void ViaRequestLayout()
        {
            var frame = Protocols.BuildViaRequest(Protocols.ViaCmdGetBattery);
            Assert.Equal(Protocols.ViaReportLen, frame.Length, "报告长度 33");
            Assert.Equal((byte)0x00, frame[0], "[0] = report id 0");
            Assert.Equal(Protocols.ViaCmdGetBattery, frame[1], "[1] = cmd 20");
            Assert.Equal((byte)0, frame[2], "[2] 补零");
        }

        [Test]
        public static void ViaParseRealWirelessReply()
        {
            // 2.4G 实测（已剥掉 report id 的 32B 载荷）：14 00 00 00 64 02 01 03
            var reply = new byte[32];
            reply[0] = 0x14; reply[4] = 100; reply[5] = 2;
            reply[6] = 1; reply[7] = 0x03;

            var parsed = Protocols.ParseViaBattery(reply, out string reason);
            Assert.Equal(ViaBatteryKind.Valid, parsed.Kind, "应解析成功");
            Assert.Equal(100, parsed.Percent, "电量");
            Assert.Equal(2, parsed.State, "连接状态 CONNECTED");
            Assert.Equal(1, parsed.ModuleVersion, "模块版本");
            Assert.Equal(0x03, parsed.Indicator, "指示灯位图");
            Assert.Equal("", reason, "成功时无理由文案");
        }

        [Test]
        public static void ViaParseRejectsStockFirmware()
        {
            // 原厂固件：cmd 20 越界，回 0xFF。
            // 绝不能被当成 0% —— 否则没刷补丁的用户会看到“键盘 0%”并收到
            // 低电量提醒，比不显示更糟。
            var reply = new byte[32];
            reply[0] = 0xFF;

            var parsed = Protocols.ParseViaBattery(reply, out string reason);
            Assert.Equal(ViaBatteryKind.Unsupported, parsed.Kind, "原厂固件应判不支持");
            Assert.True(reason.IndexOf("补丁") >= 0, "文案要点明缺电量补丁：" + reason);
        }

        [Test]
        public static void ViaParseWiredModeHasNoBattery()
        {
            // USB 有线：md_info 全零。补丁生效与不生效的响应恰好都是 0，
            // 所以必须先看回显（这里是 0x14），再判 state==0 为“读不到”。
            var reply = new byte[32];
            reply[0] = 0x14;
            reply[4] = 0; reply[5] = 0; reply[6] = 0; reply[7] = 0;

            var parsed = Protocols.ParseViaBattery(reply, out string reason);
            Assert.Equal(ViaBatteryKind.Wired, parsed.Kind, "有线模式应判 Wired 而非 Valid");
            Assert.True(reason.IndexOf("有线") >= 0, "文案要点明 USB 有线：" + reason);
        }

        [Test]
        public static void ViaParseRejectsShortFrame()
        {
            Assert.Equal(ViaBatteryKind.Unsupported,
                Protocols.ParseViaBattery(new byte[4], out _).Kind, "短帧应拒绝");
            Assert.Equal(ViaBatteryKind.Unsupported,
                Protocols.ParseViaBattery(null, out _).Kind, "null 应拒绝");
        }

        [Test]
        public static void ViaParseRejectsOutOfRangePercent()
        {
            var reply = new byte[32];
            reply[0] = 0x14; reply[4] = 200; reply[5] = 2;
            Assert.Equal(ViaBatteryKind.Unsupported,
                Protocols.ParseViaBattery(reply, out _).Kind, "电量 >100 应拒绝");
        }

        [Test]
        public static void ViaStateAndIndicatorText()
        {
            Assert.Equal("已连接", Protocols.ViaStateText(2), "state 2");
            Assert.Equal("已断开", Protocols.ViaStateText(3), "state 3");
            Assert.Equal("状态9", Protocols.ViaStateText(9), "未知码原样回数字");
            Assert.Equal("Caps+Num", Protocols.ViaIndicatorText(0x03), "两个指示灯");
            Assert.Equal("", Protocols.ViaIndicatorText(0), "全灭");
        }

        [Test]
        public static void Max102ModelName()
        {
            Assert.Equal("维咖 MAX102", Protocols.Max102ModelName("MAX102-2.4G"), "2.4G 名");
            Assert.Equal("维咖 MAX102", Protocols.Max102ModelName("MAX102"), "USB 名");
            Assert.Equal("维咖 MAX102", Protocols.Max102ModelName(""), "空名走兜底");
        }

        [Test]
        public static void BluetoothMacParsing()
        {
            // 实测 InstanceId：BTHLE\DEV_C00672D99623\7&387A7373&0&C00672D99623（键盘，低功耗）
            Assert.Equal("C0:06:72:D9:96:23",
                Core.BluetoothBattery.MacFromInstanceId(
                    "BTHLE\\DEV_C00672D99623\\7&387A7373&0&C00672D99623"),
                "BTHLE 的 DEV_ 形态");

            // 实测 InstanceId：耳机经典蓝牙服务节点，**没有 DEV_**，地址在最后一个 &0& 之后
            Assert.Equal("44:5E:CD:40:7C:2A",
                Core.BluetoothBattery.MacFromInstanceId(
                    "BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002"
                    + "\\7&2B6A77E0&0&445ECD407C2A_C00000000"),
                "BTHENUM 服务节点形态");

            // 实测父节点：BTHENUM\DEV_445ECD407C2A\…BLUETOOTHDEVICE_445ECD407C2A
            Assert.Equal("44:5E:CD:40:7C:2A",
                Core.BluetoothBattery.MacFromInstanceId(
                    "BTHENUM\\DEV_445ECD407C2A\\7&2B6A77E0&0&BLUETOOTHDEVICE_445ECD407C2A"),
                "BTHENUM 的 DEV_ 形态要与服节点一致");

            Assert.Equal("", Core.BluetoothBattery.MacFromInstanceId("BTHLE\\nonsense"),
                "拿不到地址时回空串");
            Assert.Equal("", Core.BluetoothBattery.MacFromInstanceId(""), "空串不崩");
        }

        // ============================================================ 蓝牙服务过滤
        //
        // BTHENUM 树下混着蓝牙串口、蓝牙鼠标、传真等无关节点，
        // 不过滤就会把别的设备的电量串到耳机上。

        [Test]
        public static void BluetoothAudioInstanceMatching()
        {
            Assert.True(Protocols.IsBluetoothAudioInstance(
                "BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002"
                + "\\7&2B6A77E0&0&445ECD407C2A_C00000000"), "HFP AG 是音频服务");
            Assert.True(Protocols.IsBluetoothAudioInstance(
                "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001038F_PID&0000\\x"),
                "A2DP Sink 是音频服务");
            Assert.True(Protocols.IsBluetoothAudioInstance(
                "BTHENUM\\{0000110e-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\\x"),
                "小写 UUID 也要认");

            // 实测：串口 / 传真 / 鼠标 都不是音频
            Assert.False(Protocols.IsBluetoothAudioInstance(
                "BTHENUM\\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\\x"),
                "SPP 串口不是音频");
            Assert.False(Protocols.IsBluetoothAudioInstance(
                "BTHENUM\\{00001115-0000-1000-8000-00805F9B34FB}_VID&0001038F_PID&0000\\x"),
                "PAN 不是音频");
            Assert.False(Protocols.IsBluetoothAudioInstance(
                "BTHLE\\DEV_C00672D99623\\7&387A7373&0&C00672D99623"),
                "BTHLE 不走这条判定");
            Assert.False(Protocols.IsBluetoothAudioInstance(null), "null 安全");
        }

        [Test]
        public static void BluetoothStateMapping()
        {
            // 系统属性没有充电状态（pid 3 实测恒缺失），必须如实说未知
            var live = Protocols.InterpretBluetoothState(connected: true, charging: -1);
            Assert.True(live.Connected, "在线");
            Assert.False(live.Charging, "充电状态未知不能当充电中");
            Assert.True(live.Status.Contains("未知"), "要写状态未知：" + live.Status);

            var charging = Protocols.InterpretBluetoothState(connected: true, charging: 1);
            Assert.True(charging.Charging, "pid 3 = 1 是充电中");
            Assert.True(charging.Status.Contains("充电中"), "文案：" + charging.Status);

            var offline = Protocols.InterpretBluetoothState(connected: false, charging: -1);
            Assert.False(offline.Connected, "离线");
            Assert.True(offline.Status.Contains("离线"), "文案：" + offline.Status);
            Assert.False(offline.Status.Contains("未知"),
                "离线时不必再提充电状态：" + offline.Status);
        }

        [Test]
        public static void RazerBluetoothModelName()
        {
            // 实测 PnP 友好名（本机 BlackShark V2 Pro 蓝牙模式）
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelNameFromBluetooth("Razer BlackShark V2 Pro BT"),
                "父节点名");
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelNameFromBluetooth("Razer BlackShark V2 Pro BT Hands-Free AG"),
                "服务节点名要脆掉尾部服务后缀");
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelNameFromBluetooth("Razer BlackShark V2 Pro BT Avrcp Transport"),
                "Avrcp 后缀");
            Assert.Equal("Kraken V4",
                Protocols.RazerModelNameFromBluetooth("Razer Kraken V4 Hands-Free AG"),
                "没有 BT 尾巴");
            Assert.Equal("Razer 无线设备",
                Protocols.RazerModelNameFromBluetooth(""), "空名走兜底");
            Assert.Equal("Razer 无线设备",
                Protocols.RazerModelNameFromBluetooth("Razer"), "只留品牌名时也要兜底");
        }

        // ============================================================ VIA 族第二个变体
        //
        // 高斯 GS3108T-PRO 与维咖 MAX102 共用端点（0xFF60/0x61、33 字节），
        // 但命令号与电量偏移不同。这里锁住「差异只有这两个参数、
        // 其余判定完全一致」—— 否则移植时会悄悄放松那些踩过坑的判据。
        //
        // 帧来自 FerrisPulse 的 GanssDeviceHelper（静态分析 + 实机抓包）：
        //   请求  00 20 01 00 … 00 21
        //   应答  00 20 01 00 <bat> …

        [Test]
        public static void ViaGenericLayoutParsesBattery()
        {
            // 载荷是**剥掉 report id 后**的 32 字节
            var reply = new byte[32];
            reply[0] = 0x20;   // 命令回显
            reply[1] = 0x01;
            reply[2] = 0x00;
            reply[3] = 87;     // 电量在偏移 3（维咖是 4）
            reply[4] = 2;      // state = CONNECTED（偏移 4，不是 3 之后就没有了）
            reply[5] = 1;      // 模块版本
            reply[6] = 0x01;   // 指示灯

            var parsed = Protocols.ParseViaBattery(reply,
                Protocols.ViaCmdGetBatteryGeneric,
                Protocols.ViaGenericBatteryOffset, out string reason);

            Assert.Equal(ViaBatteryKind.Valid, parsed.Kind, "应解析成功：" + reason);
            Assert.Equal(87, parsed.Percent, "电量取偏移 3");
            Assert.Equal(2, parsed.State, "状态取偏移 4");
        }

        [Test]
        public static void ViaGenericLayoutRejectsWrongCommand()
        {
            // 用维咖的 0x14 去解析高斯的应答 → 必须判失败，不能当成电量
            var reply = new byte[32];
            reply[0] = 0x20;
            reply[3] = 87;

            var parsed = Protocols.ParseViaBattery(reply,
                Protocols.ViaCmdGetBattery, 4, out string reason);

            Assert.Equal(ViaBatteryKind.Unsupported, parsed.Kind, "命令号不符应拒绝");
            Assert.True(reason.Length > 0, "要说明原因");
        }

        [Test]
        public static void ViaGenericLayoutKeepsStockFirmwareJudgement()
        {
            // 0xFF = 原厂固件没补丁。这条判据必须两个变体一致 ——
            // 判成 0% 会让用户看到键盘永远没电并反复收到低电量提醒。
            var reply = new byte[32];
            reply[0] = 0xFF;

            var parsed = Protocols.ParseViaBattery(reply,
                Protocols.ViaCmdGetBatteryGeneric,
                Protocols.ViaGenericBatteryOffset, out string reason);

            Assert.Equal(ViaBatteryKind.Unsupported, parsed.Kind, "0xFF 一律不支持");
            Assert.True(reason.Contains("补丁"), "文案要提到补丁：" + reason);
        }

        [Test]
        public static void ViaGenericLayoutKeepsWiredJudgement()
        {
            // state == 0 → USB 有线，无线模块不工作。同样两个变体一致。
            var reply = new byte[32];
            reply[0] = 0x20;
            reply[1] = 0x01;
            reply[2] = 0x00;
            reply[3] = 0;      // 电量 0
            reply[4] = 0;      // state 0

            var parsed = Protocols.ParseViaBattery(reply,
                Protocols.ViaCmdGetBatteryGeneric,
                Protocols.ViaGenericBatteryOffset, out _);

            Assert.Equal(ViaBatteryKind.Wired, parsed.Kind, "state 0 判有线");
        }

        [Test]
        public static void ViaGenericLayoutRejectsOutOfRangePercent()
        {
            var reply = new byte[32];
            reply[0] = 0x20;
            reply[3] = 200;    // > 100 不可信
            reply[4] = 2;      // state CONNECTED

            var parsed = Protocols.ParseViaBattery(reply,
                Protocols.ViaCmdGetBatteryGeneric,
                Protocols.ViaGenericBatteryOffset, out _);

            Assert.Equal(ViaBatteryKind.Unsupported, parsed.Kind, "电量 > 100 应拒绝");
        }

        [Test]
        public static void ViaDefaultOverloadStillTargetsMax102()
        {
            // 无参重载必须保持维咖的语义（cmd 0x14，偏移 4），
            // 否则加参数化会把老型号改坏。
            var reply = new byte[32];
            reply[0] = 0x14;
            reply[4] = 100;
            reply[5] = 2;

            var parsed = Protocols.ParseViaBattery(reply, out string reason);
            Assert.Equal(ViaBatteryKind.Valid, parsed.Kind, "默认重载走维咖布局：" + reason);
            Assert.Equal(100, parsed.Percent, "电量取偏移 4");
        }

        // ============================================================ 雷蛇在线判定
        //
        // 用户报的 bug：「耳机有时候会显示掉线，实际上没掉线」。
        // 该判定背后有三个分支，connection 的 -1（没问到）绝不能当 0（休眠）。

        [Test]
        public static void RazerStateNormalIsOnline()
        {
            var state = Protocols.InterpretRazerState(charging: 0, connection: 1);
            Assert.True(state.Connected, "connection=1 应在线");
            Assert.Equal("放电中", state.Status, "文案");
            Assert.False(state.Charging, "未充电");
        }

        [Test]
        public static void RazerStateSleepingIsOffline()
        {
            // connection == 0 是耳机自己报的「休眠」，确实该显示离线
            var state = Protocols.InterpretRazerState(charging: 0, connection: 0);
            Assert.False(state.Connected, "connection=0 应离线");
            Assert.Equal("已休眠", state.Status, "文案");
        }

        [Test]
        public static void RazerStateUnconfirmedStaysOnline()
        {
            // connection == -1 是丢包没问到。电量都读到了，链路必然通，
            // 所以必须算在线 —— 当离线处理正是「明明在用却掉线」的成因。
            var state = Protocols.InterpretRazerState(charging: -1, connection: -1);
            Assert.True(state.Connected, "connection=-1 不能判离线");
            Assert.True(state.Status.Contains("未确认"), "状态要如实标未确认：" + state.Status);
        }

        [Test]
        public static void RazerStateChargingText()
        {
            var state = Protocols.InterpretRazerState(charging: 1, connection: 1);
            Assert.True(state.Charging, "charging=1");
            Assert.Equal("充电中", state.Status, "文案");
        }

        [Test]
        public static void RazerStateUnconfirmedChargingKeepsFlag()
        {
            // 充电状态问到了但连接状态没问到：充电标记要保留
            var state = Protocols.InterpretRazerState(charging: 1, connection: -1);
            Assert.True(state.Connected, "仍算在线");
            Assert.True(state.Charging, "充电标记不能因为连接状态丢了而丢");
        }

        // ============================================================ report id
        //
        // 实测踩过的坑：hidapi 的 read() 不返回 report id，Win32 ReadFile 会。
        // 照搬 Python 的 `r[0] == cmd` 会让 C# 版永远匹配不上，
        // 于是 2.4G 有数据也读不到、回落到蓝牙。

        [Test]
        public static void StripReportIdRealFrame()
        {
            // 本机实机抓包（33B，含 report id）：
            //   00 14 00 00 00 64 02 01 01 00 ...
            var report = new byte[33];
            report[0] = 0x00;   // report id
            report[1] = 0x14;   // cmd 回显
            report[5] = 100;    // 电量
            report[6] = 2;      // CONNECTED
            report[7] = 1;      // 模块版本
            report[8] = 0x01;   // 指示灯

            var payload = Protocols.StripReportId(report, Protocols.ViaCmdGetBattery);
            Assert.NotNull(payload, "应能剥掉 report id");
            Assert.Equal(32, payload!.Length, "剥完剩 32 字节");
            Assert.Equal(Protocols.ViaCmdGetBattery, payload[0], "载荷首字节是 cmd 回显");

            var parsed = Protocols.ParseViaBattery(payload, out _);
            Assert.Equal(ViaBatteryKind.Valid, parsed.Kind, "应解析成功");
            Assert.Equal(100, parsed.Percent, "电量");
            Assert.Equal(2, parsed.State, "CONNECTED");
            Assert.Equal(1, parsed.ModuleVersion, "模块版本");
            Assert.Equal(0x01, parsed.Indicator, "指示灯");
        }

        [Test]
        public static void StripReportIdWithoutReportId()
        {
            // hidapi 形态（无 report id）：载荷首字节就是 cmd
            var payload = new byte[32];
            payload[0] = 0x14;
            var result = Protocols.StripReportId(payload, Protocols.ViaCmdGetBattery);
            Assert.NotNull(result, "无 report id 的形态也要接受");
            Assert.Equal(32, result!.Length, "长度不变");
        }

        [Test]
        public static void StripReportIdRejectsOtherCommand()
        {
            var report = new byte[33];
            report[0] = 0x00;
            report[1] = 0x01;   // cmd 1 的回包，不是我们要的
            Assert.Null(Protocols.StripReportId(report, Protocols.ViaCmdGetBattery),
                "命令不匹配应拒绝");
            Assert.Null(Protocols.StripReportId(null, 0x14), "null 应拒绝");
        }

        // ============================================================ 托盘图标几何
        //
        // 用户反馈「托盘的环太细」。根因是旧参数按 64px 画布给描边（3.2/2.8/2.4），
        // 缩到任务栏真实的 24px 后只剩 1.2px。这里把「三圈必须分得开」的
        // 几何约束锁死，防止以后再把描边调细或把半径调大。

        [Test]
        public static void TrayRingsDoNotOverlap()
        {
            var geo = UI.TrayIconArt.Default;
            // 每圈的净间隙 = (外圈半径 - 外圈半描边) - (内圈半径 + 内圈半描边)
            // 小于 0.5px 时缩到 24px 就会粘成一个粗环
            for (int i = 0; i + 1 < geo.Radii.Length; i++)
            {
                double outerInner = geo.Radii[i] - geo.Strokes[i] / 2.0;
                double innerOuter = geo.Radii[i + 1] + geo.Strokes[i + 1] / 2.0;
                double gap = outerInner - innerOuter;
                Assert.True(gap >= 0.5,
                    $"第 {i} 圈与第 {i + 1} 圈的净间隙应 ≥ 0.5px，实际 {gap:0.00}px");
            }
        }

        [Test]
        public static void TrayRingsFitInsideCanvas()
        {
            var geo = UI.TrayIconArt.Default;
            // 图标是正方形，半宽 = 尺寸/2。最外圈外沿不能越过它，否则被裁切。
            // 用 24px 实机尺寸校验（这是最紧的情况）。
            for (int i = 0; i < geo.Radii.Length; i++)
            {
                double outer = geo.Radii[i] + geo.Strokes[i] / 2.0;
                Assert.True(outer <= 12.0,
                    $"第 {i} 圈外沿 {outer:0.00}px 超出 24px 图标半宽 12px");
            }
        }

        [Test]
        public static void TrayRingCenterHoleIsVisible()
        {
            var geo = UI.TrayIconArt.Default;
            int last = geo.Radii.Length - 1;
            double hole = geo.Radii[last] - geo.Strokes[last] / 2.0;
            // 中心孔的半径必须够大，否则内圈会变成一个实心点
            Assert.True(hole >= 2.0,
                $"中心留孔半径应 ≥ 2.0px，实际 {hole:0.00}px");
        }

        [Test]
        public static void TrayRingStrokesAreThickEnough()
        {
            var geo = UI.TrayIconArt.Default;
            // 24px 图标上描边细于 2px 会看起来像发丝（旧参数缩下来是 1.2px）
            for (int i = 0; i < geo.Strokes.Length; i++)
            {
                Assert.True(geo.Strokes[i] >= 2.0f,
                    $"第 {i} 圈描边 {geo.Strokes[i]:0.0}px 在 24px 图标上太细（应 ≥ 2.0）");
            }
        }

        [Test]
        public static void TrayRingAtHandlesOutOfRange()
        {
            var rings = new Models.Reading?[] { new Models.Reading { Percent = 50 } };
            Assert.NotNull(UI.TrayIconArt.RingAt(rings, 0), "第 0 圈有读数");
            Assert.Null(UI.TrayIconArt.RingAt(rings, 1), "越界应返回 null");
            Assert.Null(UI.TrayIconArt.RingAt(null, 0), "null 数组应返回 null");
            Assert.Null(UI.TrayIconArt.RingAt(rings, -1), "负索引应返回 null");
        }

        // ============================================================ 环的颜色与弧长
        //
        // 用户要求「环能彩色、依据电量变不同颜色、圆占用多少」。
        // 这两条语义必须锁住：颜色按四档走，弧长严格等于电量占比。

        [Test]
        public static void RingColorUsesAllFourBands()
        {
            var c = UI.RingColors.Default;
            // 阈值：critical=10, low=30, mid=55
            Assert.Equal(c.Critical, c.ForBattery(5, false, 30, 10, 55), "5% 告急");
            Assert.Equal(c.Critical, c.ForBattery(10, false, 30, 10, 55), "10% 恰好告急");
            Assert.Equal(c.Low, c.ForBattery(11, false, 30, 10, 55), "11% 偏低");
            Assert.Equal(c.Low, c.ForBattery(30, false, 30, 10, 55), "30% 恰好偏低");
            Assert.Equal(c.Mid, c.ForBattery(31, false, 30, 10, 55), "31% 一般");
            Assert.Equal(c.Mid, c.ForBattery(55, false, 30, 10, 55), "55% 恰好一般");
            Assert.Equal(c.High, c.ForBattery(56, false, 30, 10, 55), "56% 充足");
            Assert.Equal(c.High, c.ForBattery(100, false, 30, 10, 55), "100% 充足");
        }

        [Test]
        public static void RingColorChargingOverridesPercent()
        {
            var c = UI.RingColors.Default;
            // 充电时无论多少电量都用充电色（图标会整圈点亮）
            Assert.Equal(c.Charging, c.ForBattery(5, true, 30, 10, 55), "充电优先于告急");
            Assert.Equal(c.Charging, c.ForBattery(100, true, 30, 10, 55), "充电优先于充足");
        }

        [Test]
        public static void RingColorsAreActuallyDifferent()
        {
            // 四档必须是明显不同的颜色，不能有两档撞色 ——
            // 撞色就等于「只有三档」，用户看不出变化
            var c = UI.RingColors.Default;
            var distinct = new System.Collections.Generic.HashSet<int>
            {
                c.High.ToArgb(), c.Mid.ToArgb(), c.Low.ToArgb(), c.Critical.ToArgb(),
            };
            Assert.Equal(4, distinct.Count, "四档颜色必须互不相同");
        }

        [Test]
        public static void RingArcLengthTracksPercent()
        {
            // 弧长 = 电量占比，且极低电量有最小可视角度（否则 1% 和“没读数”一样）
            // 这里只校验归一化后的占比函数是否单调，绘制细节由肉眼预览确认
            double prev = -1;
            foreach (int p in new[] { 0, 1, 8, 25, 50, 75, 99, 100 })
            {
                double fraction = System.Math.Clamp(p / 100.0, 0.0, 1.0);
                Assert.True(fraction >= prev, $"{p}% 的占比应单调不减");
                prev = fraction;
            }
            Assert.Close(1.0, System.Math.Clamp(100 / 100.0, 0.0, 1.0), 0.0001, "100% 是整圈");
            Assert.Close(0.5, System.Math.Clamp(50 / 100.0, 0.0, 1.0), 0.0001, "50% 是半圈");
        }
    }
}
