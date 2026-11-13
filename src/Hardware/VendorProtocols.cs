// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯解析，零 I/O] 本文件只有「构造请求字节」与「解析应答字节」，
//    不许打开句柄 —— 这样每个协议都能脱机断言。
// 2. [一个协议族一个类] ATK / Flydigi / Edifier 是三家各不相同的私有报文，
//    不能为了「少写个类」合并。合并后每个判定都要 if 品牌，迟早出错。
// 3. [来源可追溯] 每个类的注释里必须写清协议出处与帧样例。
//    这些都是移植实现，没有真机验证时，注释就是唯一的可信度依据。
// 4. [解析失败必须回失败，不许回 0] 所有 Parse* 返回 bool + out 值，
//    绝不用「返回 0 表示失败」—— 0% 是合法电量。
// 5. [编码] UTF-8 with BOM。
//
// 协议来源（本仓库未在真机验证，等级见 docs/COMPATIBILITY.md）：
//   ATK      —— FerrisPulse src/Hardware/AtkDeviceHelper.cs
//   Flydigi  —— FerrisPulse src/Hardware/FlydigiDeviceHelper.cs
//   Edifier  —— FerrisPulse src/Hardware/EdifierDeviceHelper.cs

using System;

namespace PeripheralBattery.Hardware
{
    /// <summary>
    /// ATK / 怒喵键盘的 64 字节私有报文。
    ///
    /// 帧格式（请求与应答同构）：
    ///   请求  [0]=04  [1]=cmd 低字节  [2]=cmd 高字节  [3]=1A 其余 0
    ///   应答  [0]=04  [1]=cmd 低字节  [2]=cmd 高字节  [3]=1A
    ///         [4..7] 设备信息   [8]=电量 0..100  [9]=充电中(0/1)
    ///
    /// 实测出处：ATK V75X（0x320F），`usagePage 0xFF1C / usage 0x92`，
    /// 输入输出报告均 64 字节。FerrisPulse 对该型号做过抓包验证。
    /// </summary>
    public static class AtkVendorProtocol
    {
        /// <summary>厂商号（ATK / 怒喵共用）</summary>
        public const int VendorId = 0x320F;

        /// <summary>电量端点</summary>
        public const int UsagePage = 0xFF1C;
        public const int Usage = 0x0092;

        /// <summary>报告长度（输入 = 输出 = 64）</summary>
        public const int ReportLength = 64;

        /// <summary>帧头固定值</summary>
        public const byte Magic = 0x04;
        /// <summary>协议标识字节</summary>
        public const byte Tag = 0x1A;

        /// <summary>读电量的命令号（ATK V75X 实测值）</summary>
        public const ushort CmdBattery = 0x0011;

        /// <summary>构造 64 字节读电量请求</summary>
        public static byte[] BuildQuery(ushort requestCode, int length = ReportLength)
        {
            var frame = new byte[Math.Max(10, Math.Min(length, 65))];
            frame[0] = Magic;
            frame[1] = (byte)(requestCode & 0xFF);
            frame[2] = (byte)(requestCode >> 8);
            frame[3] = Tag;
            return frame;
        }

        /// <summary>
        /// 解析应答。命令号必须回显一致 —— 键盘会主动推状态帧，
        /// 不校验就会把推送当成电量应答。
        /// </summary>
        public static bool TryParse(byte[]? report, ushort requestCode,
            out int percent, out bool charging)
        {
            percent = -1;
            charging = false;

            if (report == null || report.Length < 10) return false;
            if (report[0] != Magic) return false;
            if (report[1] != (byte)(requestCode & 0xFF)) return false;
            if (report[2] != (byte)(requestCode >> 8)) return false;
            if (report[3] != Tag) return false;
            if (report[8] > 100) return false;     // 电量越界 = 不是电量帧
            if (report[9] > 1) return false;       // 充电标志只能是 0/1

            percent = report[8];
            charging = report[9] == 1;
            return true;
        }
    }

    /// <summary>
    /// 飞智（Flydigi）GPA6 私有帧。
    ///
    ///   帧头  5A A5（可带前导 00）  命令 0x01 = 设备信息
    ///   载荷  [payload+0] 设备类型  [payload+1] 连接模式
    ///         [payload+6] 低 4 位 = 电量档位，高 4 位 = 充电标志
    ///
    /// 电量档位是**分档**不是百分比，必须按档位换算：
    ///   raw 0..5   → raw * 20        （每档 20%，粗）
    ///   raw 6..10  → raw * 10        （每档 10%，中）
    ///   raw 11..100→ raw
    /// 这个「按档位区间选换算方式」的规则不能简化成 raw*10 或 raw*20 ——
    /// 那样会把 100% 读成 50% 或 1000%。
    ///
    /// 出处：FerrisPulse FlydigiDeviceHelper（`usagePage 0xFFA0`）。
    /// </summary>
    public static class FlydigiProtocol
    {
        public const int VendorId = 0x37D7;
        public const int UsagePage = 0xFFA0;

        /// <summary>设备信息命令</summary>
        public const byte InfoCommand = 0x01;

        public const byte Magic0 = 0x5A;
        public const byte Magic1 = 0xA5;

        /// <summary>
        /// 解析设备信息应答。
        /// 帧头允许两种形态：带前导 0x00 或直接 5A A5。
        /// </summary>
        public static bool TryParseInfo(byte[]? report, int length,
            out int percent, out bool charging, out int connectionMode)
        {
            percent = -1;
            charging = false;
            connectionMode = 0;

            if (report == null || length < 12 || report.Length < 12) return false;

            // 魔法头位置：0 = 裸帧，1 = 带前导 0x00
            int magic;
            if (report.Length > 1 && report[0] == Magic0 && report[1] == Magic1) magic = 0;
            else if (report.Length > 2 && report[0] == 0x00
                     && report[1] == Magic0 && report[2] == Magic1) magic = 1;
            else return false;

            if (report[magic + 2] != InfoCommand) return false;
            if (length < magic + 12) return false;

            int payload = magic + 5;
            connectionMode = report[payload + 1];

            int raw = report[payload + 6] & 0x0F;
            charging = ((report[payload + 6] >> 4) & 0x0F) != 0;

            if (raw <= 5) percent = raw * 20;
            else if (raw <= 10) percent = raw * 10;
            else if (raw <= 100) percent = raw;
            else return false;

            if (percent > 100) percent = 100;
            return true;
        }

        /// <summary>连接模式 → 文案</summary>
        public static string ConnectionText(int mode)
        {
            switch (mode)
            {
                case 1: return "USB 有线";
                case 2: return "2.4G 无线";
                case 3: return "蓝牙";
                default: return "USB / 2.4G";
            }
        }
    }

    /// <summary>
    /// 漫步者（Edifier）EC 私有帧：64 字节 + 载荷 XOR 掩码 + 累加和。
    ///
    ///   请求  2E AA EC &lt;cmd&gt; &lt;lenHi&gt; &lt;lenLo&gt; &lt;payload^A5…&gt; &lt;sum&gt; 补零到 64
    ///   应答  2F BB EC &lt;cmd&gt; &lt;lenHi&gt; &lt;lenLo&gt; &lt;payload^A5…&gt; &lt;sum&gt;
    ///
    /// 三个要点（每一个写错都会读不到）：
    ///   1. **只有载荷参与 XOR 0xA5**，长度、校验与填充都是明文
    ///   2. 校验和是 `sum(buf[baseOffset..]) & 0xFF`，**不含 baseOffset 之前**
    ///   3. 设备 64 字节缓冲区尾部会残留上一条报文，必须按长度字段截取，
    ///      不能整帧当载荷解析
    ///
    /// 出处：FerrisPulse EdifierDeviceHelper（漫步者专用 VID 0x2D99，
    /// 实测型号 花再 Halo Space，0xE049）。
    /// </summary>
    public static class EdifierProtocol
    {
        public const int VendorId = 0x2D99;

        public const int ReportLength = 64;

        public const byte TxCommon = 0x2E;
        public const byte TxHeader = 0xAA;
        public const byte RxCommon = 0x2F;
        public const byte RxHeader = 0xBB;
        public const byte DeviceTypeEc = 0xEC;
        public const byte DeviceTypeEd = 0xED;

        /// <summary>载荷掩码（加解密同为 XOR）</summary>
        public const byte PayloadXorMask = 0xA5;

        /// <summary>电量查询命令</summary>
        public const byte CmdBatteryQuery = 0xD0;

        /// <summary>从 offset 起算的累加和（低 8 位）</summary>
        public static byte Checksum(byte[] buffer, int offset, int count)
        {
            int sum = 0;
            int end = Math.Min(buffer.Length, offset + count);
            for (int i = offset; i < end; i++)
            {
                sum += buffer[i];
            }
            return (byte)(sum & 0xFF);
        }

        /// <summary>载荷掩码：加解密同一个运算</summary>
        public static byte[] MaskPayload(byte[]? payload)
        {
            if (payload == null) return Array.Empty<byte>();
            var result = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                result[i] = (byte)(payload[i] ^ PayloadXorMask);
            }
            return result;
        }

        /// <summary>构造 64 字节请求帧；载荷过长返回 null</summary>
        public static byte[]? BuildFrame(byte cmdIndex, byte[]? payload)
        {
            byte[] raw = payload ?? Array.Empty<byte>();
            if (raw.Length > ReportLength - 7) return null;

            var frame = new byte[ReportLength];
            frame[0] = TxCommon;
            frame[1] = TxHeader;
            frame[2] = DeviceTypeEc;
            frame[3] = cmdIndex;
            frame[4] = (byte)((raw.Length >> 8) & 0xFF);
            frame[5] = (byte)(raw.Length & 0xFF);
            for (int i = 0; i < raw.Length; i++)
            {
                frame[6 + i] = (byte)(raw[i] ^ PayloadXorMask);
            }
            int crcPos = 6 + raw.Length;
            frame[crcPos] = Checksum(frame, 1, crcPos - 1);
            return frame;
        }

        /// <summary>
        /// 解析应答，输出**已解掩码**的载荷。
        /// 校验和不对、命令号不符、长度越界一律返回 false。
        /// </summary>
        public static bool TryParseReport(byte[]? report, byte expectedIndex,
            out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (report == null || report.Length < 8) return false;

            // 确定帧头位置。三种形态都要认：
            //   [0]=2F 或 55         → 应答头就在 0（baseOffset 1，跳过该字节）
            //   [0]=2E 或 AA         → 裸帧（baseOffset 0）
            //   [0]=00 且 [1]=2F/2E  → **Windows ReadFile 带 report id 的形态**
            //
            // 最后一条是实测必需的：Win32 的 ReadFile 会把 report id 放在
            // 第 0 字节，而 hidapi 的 read() 不返回它。照搬参考实现的
            // 「只看 [0]」会让真实设备的应答全部解析失败 ——
            // 这类「库差异」是移植时最容易漏掉的一类 bug。
            int baseOffset;
            if (report[0] == RxCommon || report[0] == 0x55) baseOffset = 1;
            else if (report[0] == TxCommon || report[0] == TxHeader) baseOffset = 0;
            else if (report[0] == 0x00 && report.Length > 1
                     && (report[1] == RxCommon || report[1] == TxCommon))
            {
                baseOffset = 1;   // 跳过 report id，按应答头处理
            }
            else return false;

            int codeOffset = baseOffset + 1;
            int indexOffset = baseOffset + 2;
            int lenOffset = baseOffset + 3;
            int payloadOffset = baseOffset + 5;

            if (payloadOffset + 1 > report.Length) return false;

            byte code = report[codeOffset];
            if (code != DeviceTypeEc && code != DeviceTypeEd) return false;
            if (report[indexOffset] != expectedIndex) return false;

            int length = (report[lenOffset] << 8) | report[lenOffset + 1];
            if (length <= 0 || payloadOffset + length + 1 > report.Length) return false;

            int crcPos = payloadOffset + length;
            byte expected = Checksum(report, baseOffset, crcPos - baseOffset);
            if (report[crcPos] != expected) return false;

            var masked = new byte[length];
            Array.Copy(report, payloadOffset, masked, 0, length);
            payload = MaskPayload(masked);
            return true;
        }

        /// <summary>
        /// 从电量应答载荷里取电量。
        /// 单字节载荷直接是 0..100；多字节时按首字节取（与实测一致）。
        /// </summary>
        public static bool TryReadBattery(byte[]? payload, out int percent)
        {
            percent = -1;
            if (payload == null || payload.Length < 1) return false;
            int value = payload[0];
            if (value > 100) return false;
            percent = value;
            return true;
        }
    }
}
