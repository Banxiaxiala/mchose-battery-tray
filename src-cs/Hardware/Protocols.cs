// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [协议常量集中] 所有 VID/PID/命令码/字节偏移只允许定义在本文件顶部，
//    严禁散落在解析函数里。协议是逆向来的，分散后就没法一眼核对。
// 2. [纯解析] Parse* 系列必须是纯函数（只吃字节、只吐值），不许碰句柄或 I/O ——
//    它们是与 Python 版 tests/test_protocols.py 对齐的断言基准。
// 3. [编码] UTF-8 with BOM。
//
// 本机实测帧（改动前先核对）：
//   罗技 PRO X 2 (046D:C54D)
//     请求 0x1004 GetFeature : 11 01 00 0a 10 04 00 ...
//     应答 feature 索引 0x06  : 11 01 00 0a 06 00 05 ...
//     应答 caps               : 0F 0F 02 00 ...   (BIT(1) 置位 = 直报百分比)
//     应答 status             : 35 04 00 00 ...   (0x35=53%, 0x04=放电中)
//   雷蛇 BlackShark V2 Pro 2.4 (1532:0555)
//     发送 02 80 08 00 00 50 41 08 00 03 21 00 ...
//     应答 02 0e 50 49 08 c5 72 89 02 00 04 00 21 01 01 32 ...
//                                                    ↑ 0x32 = 50%

using System;
using System.Collections.Generic;

namespace PeripheralBattery.Hardware
{
    public static class Protocols
    {
        // ============================================================ 罗技 HID++
        public const int LogiVendor = 0x046D;
        public const int LogiUsagePage = 0xFF00;
        public const int LogiUsageShort = 0x0001;   // 7 字节，只用来判定「不在线」
        public const int LogiUsageLong = 0x0002;    // 20 字节，真正的通道

        public const byte HidppLong = 0x11;
        public const byte HidppShort = 0x10;
        public const byte HidppErrorSubId = 0x8F;
        // 软件 ID 必须非零：设备主动推送的通知用 0，与功能 0 的应答字节完全相同。
        public const byte HidppSoftwareId = 0x0A;

        public const int FeatRoot = 0x0000;
        public const int FeatDeviceName = 0x0005;
        public const int FeatBatteryStatus = 0x1000;
        public const int FeatBatteryVoltage = 0x1001;
        public const int FeatUnifiedBattery = 0x1004;

        // 槽位探测超时。实测依据（logi-tray）：在线设备中位应答约 57ms；
        // 休眠唤醒首帧可能慢到 850ms 且第一次可能直接失败；
        // 目标不可达时接收器 3~5ms 内就在短集合回绝。
        public const double QuickTimeout = 0.40;
        public const double PatientTimeout = 1.30;

        /// <summary>0x1000 BATTERY_STATUS 状态码 → (文案, 是否充电)</summary>
        private static readonly Dictionary<int, (string text, bool charging)> Status1000 =
            new()
            {
                [0] = ("放电中", false),
                [1] = ("充电中", true),
                [2] = ("充电中（接近充满）", true),
                [3] = ("已充满", true),
                [4] = ("充电中（慢充）", true),
                [5] = ("电池异常", false),
                [6] = ("温度异常", false),
            };

        /// <summary>0x1004 UNIFIED_BATTERY 充电状态字节</summary>
        private static readonly Dictionary<int, (string text, bool charging)> Status1004 =
            new()
            {
                [0] = ("放电中", false),
                [1] = ("充电中", true),
                [2] = ("充电中（慢充）", true),
                [3] = ("已充满", true),
                [4] = ("充电异常", false),
            };

        /// <summary>Solaar 的放电曲线：电压 mV → 百分比（分段线性插值）</summary>
        private static readonly (int mv, int pct)[] DischargeCurve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60),
            (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10),
            (3646, 5), (3579, 2), (3500, 0),
        };

        // ============================================================ 雷蛇 PA-PI
        public const int RazerVendor = 0x1532;
        public const int RazerUsagePage = 0xFF00;
        public const int RazerReportLen = 64;
        public const byte RazerReportId = 0x02;

        public const byte RazerCmdSerial = 0x00;
        public const byte RazerCmdConnection = 0x20;
        public const byte RazerCmdBattery = 0x21;
        public const byte RazerCmdCharging = 0x2A;
        public const byte RazerCmdAutoPowerOff = 0x2C;

        private static readonly Dictionary<int, string> RazerModelNames = new()
        {
            [0x0555] = "BlackShark V2 Pro",
            [0x056C] = "Kraken V4",
        };

        // ============================================================ 维咖 VIA
        //
        // MAX102（维咖 MAX102 / Weikav MAX102）是 QMK + VIA v12 键盘，
        // 电量走 VIA 私有命令。协议来源：weikav-max102 仓库 docs/08-电量补丁方案.md
        //
        // ⚠️ 两个必须记住的坑（都是实测踩出来的）：
        //   1. cmd 20（0x14）是【补丁】新增的。原厂固件对 cmd>19 一律回 0xFF。
        //      所以见到 0xFF 只能判「不支持」，绝不能当 0% —— 否则没刷补丁的用户
        //      会看到键盘永远 0% 并触发低电量提醒，比不显示更糟。
        //   2. cmd 6 是【恢复出厂默认键位】，原厂功能，任何读取逻辑都不许碰它。
        //      历史上有版本用 cmd 6 读电量，直接把该功能搞失效了。
        public const int ViaVendorMax102 = 0x342D;
        public const int ViaProductMax102 = 0xE537;
        public const int ViaUsagePage = 0xFF60;
        public const int ViaUsage = 0x0061;
        public const int ViaReportLen = 33;   // report id 1B + 32B 载荷

        /// <summary>补丁新增的“读电量”。原厂此号越界。</summary>
        public const byte ViaCmdGetBattery = 0x14;

        /// <summary>连接状态（来自 md_info.state）</summary>
        private static readonly Dictionary<int, string> ViaStates = new()
        {
            [0] = "未初始化",
            [1] = "配对中",
            [2] = "已连接",
            [3] = "已断开",
            [4] = "被拒绝",
        };

        // ============================================================ 罗技解析

        /// <summary>构造 20 字节 HID++ 2.0 长请求</summary>
        public static byte[] BuildLogiRequest(int deviceIndex, int featureIndex,
                                              int function, byte[]? parameters = null)
        {
            var packet = new byte[20];
            packet[0] = HidppLong;
            packet[1] = (byte)(deviceIndex & 0xFF);
            packet[2] = (byte)(featureIndex & 0xFF);
            packet[3] = (byte)(((function << 4) & 0xF0) | HidppSoftwareId);
            if (parameters != null)
            {
                int count = Math.Min(parameters.Length, 16);
                Array.Copy(parameters, 0, packet, 4, count);
            }
            return packet;
        }

        /// <summary>
        /// 严格匹配四字段，返回数据区 data[4..]；不匹配返回 null。
        /// 必须要求 function 字节完全一致：设备主动推送通知的软件 ID 是 0，
        /// 不校验会把通知当成应答读出一堆 0。
        /// </summary>
        public static byte[]? MatchLogiReply(byte[] data, int deviceIndex,
                                             int featureIndex, int function)
        {
            if (data == null || data.Length < 4) return null;
            byte functionByte = (byte)(((function << 4) & 0xF0) | HidppSoftwareId);
            if (data[0] != HidppLong) return null;
            if (data[1] != (byte)(deviceIndex & 0xFF)) return null;
            if (data[2] != (byte)(featureIndex & 0xFF)) return null;
            if (data[3] != functionByte) return null;
            var payload = new byte[data.Length - 4];
            Array.Copy(data, 4, payload, 0, payload.Length);
            return payload;
        }

        /// <summary>短集合上的错误帧：接收器明确说「这个槽位没有设备」</summary>
        public static bool MatchLogiAbsent(byte[] data, int deviceIndex,
                                           int featureIndex, int function)
        {
            if (data == null || data.Length < 7) return false;
            byte functionByte = (byte)(((function << 4) & 0xF0) | HidppSoftwareId);
            return data[0] == HidppShort
                && data[1] == (byte)(deviceIndex & 0xFF)
                && data[2] == HidppErrorSubId
                && data[3] == (byte)(featureIndex & 0xFF)
                && data[4] == functionByte;
        }

        /// <summary>
        /// 解析 0x1004 UNIFIED_BATTERY 应答。
        ///
        /// 布局依据 Linux 内核 hidpp20：
        ///   caps[1] BIT(1) = STATE_OF_CHARGE —— 只有置位时 status[0] 才是百分比；
        ///   否则 status[0] 无意义，必须用 status[1] 的档位位掩码（先与 caps[0] 相与）。
        /// </summary>
        public static (int? percent, string text, bool charging)? ParseLogiUnifiedBattery(
            byte[]? caps, byte[]? status)
        {
            if (status == null || status.Length < 1) return null;

            bool hasSoc = true;
            if (caps != null && caps.Length >= 2)
            {
                hasSoc = (caps[1] & 0x02) != 0;
            }

            int? percent = null;
            string text = "";
            bool charging = false;

            if (hasSoc)
            {
                percent = Math.Min(Math.Max((int)status[0], 0), 100);
            }
            else
            {
                int levelBits = status.Length > 1 ? (int)status[1] : 0;
                if (caps != null) levelBits &= caps[0];
                foreach (var (bit, levelText, approx) in new[]
                {
                    (0x08, "满", 90), (0x04, "良好", 50),
                    (0x02, "偏低", 20), (0x01, "极低", 5),
                })
                {
                    if ((levelBits & bit) != 0)
                    {
                        percent = approx;
                        text = levelText;
                        break;
                    }
                }
            }

            if (status.Length > 2)
            {
                // 只报档位的设备里 status[2] 仍然有充电状态，
                // 但档位文案（满/良好/偏低/极低）比如实有信息量，不能被覆盖。
                if (Status1004.TryGetValue(status[2], out var info))
                {
                    if (hasSoc) text = info.text;
                    charging = info.charging;
                }
            }

            if (!percent.HasValue && string.IsNullOrEmpty(text)) return null;
            return (percent, text, charging);
        }

        /// <summary>解析 0x1000 BATTERY_STATUS：status[0]=百分比 status[2]=状态码</summary>
        public static (int percent, string text, bool charging)? ParseLogiBatteryStatus(
            byte[]? status)
        {
            if (status == null || status.Length < 1) return null;
            int percent = Math.Min(Math.Max((int)status[0], 0), 100);
            string text = "";
            bool charging = false;
            if (status.Length > 2 && Status1000.TryGetValue(status[2], out var info))
            {
                text = info.text;
                charging = info.charging;
            }
            return (percent, text, charging);
        }

        /// <summary>解析 0x1001 BATTERY_VOLTAGE：be16 电压 + 标志字节</summary>
        public static (int percent, string text, bool charging)? ParseLogiBatteryVoltage(
            byte[]? data)
        {
            if (data == null || data.Length < 2) return null;
            int mv = (data[0] << 8) | data[1];
            int? percent = PercentFromMillivolt(mv);
            if (!percent.HasValue) return null;

            int flags = data.Length > 2 ? data[2] : 0;
            string text;
            bool charging;
            if ((flags & 0x80) != 0)
            {
                switch (flags & 0x07)
                {
                    case 0: text = "充电中"; charging = true; break;
                    case 1: text = "已充满"; charging = true; break;
                    case 2: text = "放电中"; charging = false; break;
                    default: text = ""; charging = false; break;
                }
            }
            else
            {
                text = "放电中";
                charging = false;
            }
            return (percent.Value, text, charging);
        }

        /// <summary>电压 → 百分比，按 Solaar 放电曲线分段线性插值</summary>
        public static int? PercentFromMillivolt(int mv)
        {
            if (mv <= 0) return null;
            if (mv >= DischargeCurve[0].mv) return DischargeCurve[0].pct;
            int last = DischargeCurve.Length - 1;
            if (mv <= DischargeCurve[last].mv) return DischargeCurve[last].pct;

            for (int i = 0; i + 1 < DischargeCurve.Length; i++)
            {
                int vHigh = DischargeCurve[i].mv;
                int pHigh = DischargeCurve[i].pct;
                int vLow = DischargeCurve[i + 1].mv;
                int pLow = DischargeCurve[i + 1].pct;
                if (mv <= vHigh && mv >= vLow)
                {
                    double ratio = (double)(mv - vLow) / (vHigh - vLow);
                    return (int)Math.Round(pLow + (pHigh - pLow) * ratio);
                }
            }
            return null;
        }

        // ============================================================ 雷蛇

        /// <summary>构造 64 字节 PA/PI 命令帧：02 80 08 00 00 'P' 'A' 08 00 03 &lt;CMD&gt; 00 …</summary>
        public static byte[] BuildRazerFrame(byte cmd)
        {
            var frame = new byte[RazerReportLen];
            frame[0] = RazerReportId;
            frame[1] = 0x80;
            frame[2] = 0x08;
            frame[5] = 0x50;      // 'P'
            frame[6] = 0x41;      // 'A'
            frame[7] = 0x08;      // 序号
            frame[9] = 0x03;
            frame[10] = cmd;
            return frame;
        }

        /// <summary>
        /// 把「电量、充电、连接状态」三个字节映射成界面要的三个字段：
        /// Connected / Status / Charging。
        ///
        /// 抽成纯函数而不是写在 Reader 里，是因为这个映射本身就有三种边界：
        /// `connection` 可能是 0（真的休眠）、1（正常）、-1（没问到）。
        /// 写错一个分支就会造成「耳机明明在用却显示掉线」，必须能脱机断言。
        ///
        /// 入参取值范围：battery 0–100；charging 0/1/-1；connection 0/1/-1。
        /// -1 一律表示「没读到」，不得当作 0 用。
        /// </summary>
        public static RazerState InterpretRazerState(int charging, int connection)
        {
            // connection == 0：耳机自己报「休眠」。能读到电量说明链路是通的，
            // 所以这不是「读不到」，而是设备主动说的「我睡了」。
            if (connection == 0)
            {
                return new RazerState { Connected = false, Status = "已休眠", Charging = charging > 0 };
            }

            // connection < 0：丢包没问到。电量刚读到，链路必然通，算在线，
            // 但状态要如实说「未确认」，不能假装是「放电中」。
            if (connection < 0)
            {
                return new RazerState
                {
                    Connected = true,
                    Status = "在线（状态未确认）",
                    Charging = charging > 0,
                };
            }

            return new RazerState
            {
                Connected = true,
                Status = charging > 0 ? "充电中" : "放电中",
                Charging = charging > 0,
            };
        }

        /// <summary>
        /// 解析应答，返回 value 字节；不匹配返回 -1。
        /// 实测格式：02 &lt;len&gt; 'P' 'I' &lt;seq&gt; &lt;4B 计数&gt; 00 04 00 &lt;CMD&gt; 01 01 &lt;value&gt;
        /// </summary>
        public static int ParseRazerReply(byte[]? reply, byte cmd)
        {
            if (reply == null || reply.Length < 16) return -1;
            if (reply[0] != RazerReportId) return -1;
            if (reply[2] != 0x50 || reply[3] != 0x49) return -1;   // 'P' 'I'
            if (reply[12] != cmd || reply[13] != 0x01) return -1;
            return reply[15];
        }

        // ============================================================ 维咖解析

        /// <summary>构造 33 字节 VIA 请求：[00, cmd, payload…] 补零</summary>
        public static byte[] BuildViaRequest(byte cmd, byte[]? payload = null)
        {
            var frame = new byte[ViaReportLen];
            frame[0] = 0x00;   // report id
            frame[1] = cmd;
            if (payload != null)
            {
                int count = Math.Min(payload.Length, ViaReportLen - 2);
                Array.Copy(payload, 0, frame, 2, count);
            }
            return frame;
        }

        /// <summary>
        /// 解析 cmd 20 的应答载荷（**32 字节，不含 report id**）。
        ///
        /// ⚠️ 这里的偏移与 HID 报告缓冲不同：Windows 的 ReadFile 会把
        /// report id 放在第 0 字节（共 33 字节），而 hidapi 的 read() 不返回它。
        /// 为了跟文档（08-电量补丁方案 §4）逐行对应，本函数只吃
        /// **已经剥掉 report id 的 32 字节载荷**，剥的动作在 Max102Reader.Query。
        ///
        /// 载荷格式：
        ///   [0]    0x14 命令号回显
        ///   [1..3] 0x00
        ///   [4]    电量 0–100
        ///   [5]    连接状态 0..4
        ///   [6]    无线模块固件版本
        ///   [7]    指示灯位图 bit0=Caps bit1=Num bit2=Scroll
        ///
        /// 判定顺序不能换：
        ///   ① 回显不是 0x14 → 原厂固件（回 0xFF）或接口不对 → unsupported
        ///   ② 状态 0（未初始化）→ USB 有线模式，md_info 全零 → 没有可信电量
        ///   ③ 其余才算 valid
        ///
        /// ② 是踩过坑才加的：USB 有线时补丁生效与不生效的响应恰好都是 0，
        /// 只看字节根本分不出来，必须先按回显分档。
        /// </summary>
        public static ViaBattery ParseViaBattery(byte[]? payload, out string reason)
        {
            reason = "";
            if (payload == null || payload.Length < 8)
            {
                reason = "应答太短";
                return ViaBattery.Unsupported;
            }
            if (payload[0] != ViaCmdGetBattery)
            {
                // 0xFF = 原厂固件（没有电量补丁）；其它值 = 接口选错/协议不对
                reason = payload[0] == 0xFF
                    ? "键盘固件没有电量补丁（发 cmd 20 回 0xFF）"
                    : $"命令回显不是 0x14，而是 0x{payload[0]:X2}";
                return ViaBattery.Unsupported;
            }

            int percent = payload[4];
            int state = payload[5];
            int version = payload[6];
            int indicator = payload[7];

            if (state == 0)
            {
                // USB 有线：无线模块不工作，md_info 全零。这不是“0%”，
                // 而是“当前模式下拿不到电量”。
                reason = "USB 有线模式，无线模块未工作，读不到电量";
                return new ViaBattery
                {
                    Kind = ViaBatteryKind.Wired, State = state,
                    ModuleVersion = version, Indicator = indicator,
                };
            }
            if (percent > 100)
            {
                reason = $"电量字节超出范围：{percent}";
                return ViaBattery.Unsupported;
            }
            return new ViaBattery
            {
                Kind = ViaBatteryKind.Valid,
                Percent = percent,
                State = state,
                ModuleVersion = version,
                Indicator = indicator,
            };
        }

        /// <summary>连接状态码 → 文案；未知码原样回数字</summary>
        public static string ViaStateText(int state)
        {
            return ViaStates.TryGetValue(state, out var text) ? text : "状态" + state;
        }

        /// <summary>连接状态是否算“真的在线”（CONNECTED=2）</summary>
        public static bool ViaStateConnected(int state)
        {
            return state == 2;
        }

        /// <summary>指示灯位图 → 文案（Caps/Num/Scroll 中已亮的）</summary>
        public static string ViaIndicatorText(int bits)
        {
            var parts = new List<string>();
            if ((bits & 0x01) != 0) parts.Add("Caps");
            if ((bits & 0x02) != 0) parts.Add("Num");
            if ((bits & 0x04) != 0) parts.Add("Scroll");
            return string.Join("+", parts);
        }

        /// <summary>
        /// 从 Windows 读到的 HID 输入报告（**含 report id**）里剥出 VIA 载荷。
        ///
        /// 为什么要这一步：hidapi 的 read() 只返回载荷，而 Win32 ReadFile
        /// 会把 report id 放在第 0 字节。0x14 属于“编号报告”，所以
        /// 33 字节缓冲 = 1 字节 report id + 32 字节载荷：
        ///
        ///   实机抓包  00 14 00 00 00 64 02 01 01 00 …
        ///             ↑  ↑           ↑  ↑  ↑  ↑
        ///        report id │          │  │  │  └ 指示灯 0x01
        ///             cmd 回显 0x14  │  │  └ 模块版本 1
        ///                       电量 0x64=100% │
        ///                            状态 0x02=CONNECTED
        ///
        /// 不剥就会拿 report id(0x00) 去比对 cmd，永远不匹配 ——
        /// 这正是“2.4G 下读不到电量、反而回落到蓝牙”的实际成因。
        /// </summary>
        public static byte[]? StripReportId(byte[]? report, byte expectedCmd)
        {
            if (report == null || report.Length == 0) return null;
            if (report[0] == expectedCmd) return report;          // 无 report id 的形态
            if (report.Length > 1 && report[1] == expectedCmd)     // 去掉 report id
            {
                var payload = new byte[report.Length - 1];
                Array.Copy(report, 1, payload, 0, payload.Length);
                return payload;
            }
            return null;
        }

        /// <summary>从 product_string 里剥出型号；剥不出来就用已知表兜底</summary>
        public static string RazerModelName(string? product, int productId)
        {
            string name = (product ?? "").Trim();
            if (name.StartsWith("Razer ")) name = name.Substring(6);
            else if (name.StartsWith("RAZER ")) name = name.Substring(6);
            name = name.Replace(" 2.4", "").Replace(" 2.4G", "").Trim();
            if (name.Length > 0 && !name.ToUpperInvariant().StartsWith("HID"))
            {
                return name;
            }
            return RazerModelNames.TryGetValue(productId, out var fallback)
                ? fallback
                : "Razer 无线耳机";
        }

        /// <summary>
        /// MAX102 型号名。2.4G 下 product_string 是 "MAX102-2.4G"，
        /// USB 下是 "MAX102"，都统一显示成大写的物理型号。
        /// </summary>
        public static string Max102ModelName(string? product)
        {
            string name = (product ?? "").Trim();
            if (name.Length == 0) return "维咖 MAX102";
            if (name.IndexOf("MAX102", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "维咖 MAX102";
            }
            return name;
        }
    }

    /// <summary>雷蛇耳机三字节映射后的界面状态</summary>
    public struct RazerState
    {
        public bool Connected;
        public string Status;
        public bool Charging;
    }

    public enum ViaBatteryKind
    {
        /// <summary>没有可信电量（原厂无补丁 / 应答不对）</summary>
        Unsupported,
        /// <summary>USB 有线，无线模块未工作，拿不到电量</summary>
        Wired,
        /// <summary>拿到了 0–100 的电量</summary>
        Valid,
    }

    /// <summary>cmd 20 的解析结果</summary>
    public struct ViaBattery
    {
        public ViaBatteryKind Kind;
        public int Percent;
        public int State;
        public int ModuleVersion;
        public int Indicator;

        public static ViaBattery Unsupported
        {
            get { return new ViaBattery { Kind = ViaBatteryKind.Unsupported }; }
        }
    }
}
