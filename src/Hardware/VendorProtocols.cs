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
    /// 迈从（MCHOSE）键盘：G75 PRO / G87 V2 / Z75S。
    ///
    /// 与迈从**鼠标**是两套完全不同的协议（鼠标走 Feature Report），
    /// 这里走中断端点 + 累加和校验：
    ///
    ///   请求  [0]=0x13(report id) [1]=0x4A(读电量) … [19]=前 19 字节和的低 8 位
    ///   应答  [0]=0x13 [1]=0x4A(bit7 是标志位，比对时要 &0x7F)
    ///         [5]=电量 0..100  [6]=状态（0x01 放电 / 0x10 充电）
    ///         [19]=校验和
    ///
    /// ⚠️ G75 PRO 的端点要求**恰好 20 字节**，不是 65 —— 同一个协议两套长度。
    /// 发 65 字节过去它会直接忽略。
    ///
    /// 出处：FerrisPulse MchoseDeviceHelper。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class MchoseKeyboardProtocol
    {
        public const byte ReportId = 0x13;
        /// <summary>2.4G 读电量命令</summary>
        public const byte CmdWirelessBattery = 0x4A;

        /// <summary>标准报告长度</summary>
        public const int ReportLength = 65;
        /// <summary>G75 PRO 专用长度（短帧）</summary>
        public const int ShortReportLength = 20;

        public const int BatteryOffset = 5;
        public const int StateOffset = 6;

        /// <summary>放电</summary>
        public const byte StateDischarging = 0x01;
        /// <summary>充电</summary>
        public const byte StateCharging = 0x10;

        /// <summary>构造标准 65 字节请求</summary>
        public static byte[] BuildQuery()
        {
            var frame = new byte[ReportLength];
            frame[0] = ReportId;
            frame[1] = CmdWirelessBattery;
            frame[2] = 0x01;
            int sum = 0;
            for (int i = 0; i <= 18; i++) sum += frame[i];
            frame[19] = (byte)(sum & 0xFF);
            return frame;
        }

        /// <summary>构造 G75 PRO 的 20 字节请求</summary>
        public static byte[] BuildShortQuery()
        {
            var frame = new byte[ShortReportLength];
            frame[0] = ReportId;
            frame[1] = CmdWirelessBattery;
            int sum = 0;
            for (int i = 0; i < ShortReportLength - 1; i++) sum += frame[i];
            frame[ShortReportLength - 1] = (byte)(sum & 0xFF);
            return frame;
        }

        /// <summary>
        /// 解析应答。<paramref name="frameLength"/> 决定用哪套校验范围
        /// （20 或 65），两种都要支持。
        /// </summary>
        public static bool TryParse(byte[]? report, int frameLength,
            out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (report == null) return false;
            if (frameLength != ReportLength && frameLength != ShortReportLength) return false;
            if (report.Length < frameLength) return false;
            if (report[0] != ReportId) return false;

            // bit7 是标志位，命令号比对必须屏蔽掉
            if ((report[1] & 0x7F) != CmdWirelessBattery) return false;

            int sum = 0;
            for (int i = 0; i < frameLength - 1; i++) sum += report[i];
            if ((byte)(sum & 0xFF) != report[frameLength - 1]) return false;

            int battery = report[BatteryOffset];
            byte state = report[StateOffset];
            if (battery < 0 || battery > 100) return false;
            if (state != StateDischarging && state != StateCharging) return false;

            percent = battery;
            charging = state == StateCharging;
            return true;
        }
    }

    /// <summary>
    /// NuPhy（努斐）三模键盘：WH80 / Air75 V2 / Halo75 V2 / Air60 V2 等。
    ///
    /// 65 字节，帧头 `0x00 0x55`，读电量要**两步**：
    ///   1. cmd `0x04` 读当前连接模式 → 应答 [9] 是模式号（0..3）
    ///   2. cmd `0x05` 读电量，偏移 = `模式 × 64 + 32`
    ///      应答 [1] 必须是 0xAA 或 0x55，[2] 必须是 cmd 回显
    ///      [9] 的低 7 位才是电量（bit7 是标志位）
    ///
    /// ⚠️ 两步不能省：跳过第 1 步就不知道偏移，直接读会拿到别的字段。
    /// ⚠️ 电量必须 `& 0x7F`：bit7 是标志位，不屏蔽会读出 128+ 的假值。
    ///
    /// 出处：FerrisPulse NuphyDeviceHelper。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class NuphyProtocol
    {
        public const int ReportLength = 65;

        public const byte Magic0 = 0x00;
        public const byte Magic1 = 0x55;

        /// <summary>读连接模式</summary>
        public const byte CmdGetMode = 0x04;
        /// <summary>读电量</summary>
        public const byte CmdGetBattery = 0x05;

        /// <summary>电量字段在应答里的偏移</summary>
        public const int BatteryFieldOffset = 9;

        public static byte[] BuildQuery(byte command, ushort offset, byte length)
        {
            var frame = new byte[ReportLength];
            frame[0] = Magic0;
            frame[1] = Magic1;
            frame[2] = command;
            frame[3] = 0x00;
            frame[5] = length;
            frame[6] = (byte)(offset & 0xFF);
            frame[7] = (byte)((offset >> 8) & 0xFF);
            frame[8] = 0x00;
            return frame;
        }

        /// <summary>从模式应答里取模式号（0..3）；越界返回 -1</summary>
        public static int ParseMode(byte[]? report)
        {
            if (report == null || report.Length < 10) return -1;
            int mode = report[9];
            return mode >= 0 && mode <= 3 ? mode : -1;
        }

        /// <summary>模式 → 电量字段偏移</summary>
        public static ushort BatteryOffsetForMode(int mode)
        {
            return (ushort)(mode * 64 + 32);
        }

        /// <summary>
        /// 解析电量应答。
        /// [1] 必须是 0xAA/0x55，[2] 必须是 0x05 回显，
        /// [9] 低 7 位是电量。
        /// </summary>
        public static bool TryParseBattery(byte[]? report, out int percent)
        {
            percent = -1;
            if (report == null || report.Length < BatteryFieldOffset + 1) return false;
            if (report[1] != 0xAA && report[1] != 0x55) return false;
            if (report[2] != CmdGetBattery) return false;

            int value = report[BatteryFieldOffset] & 0x7F;
            if (value > 100) return false;
            percent = value;
            return true;
        }
    }

    /// <summary>
    /// 宏碁（Acer）VK M3 系列无线鼠标。
    ///
    /// 33 字节（report id 0x00 + 32B 载荷），`usagePage 0xFF01 / usage 0x10`：
    ///   请求  [0]=00 [1]=10(设备信息) [2]=00
    ///   应答  [i]=10 [i+1]=00，其中 i ∈ {0,1,2}（兼容带/不带 report id）
    ///         [i+3]  载荷长度（必须 ≥ 11）
    ///         [i+4..7] 设备 ID（4 个可打印 ASCII 字符）
    ///         [i+8..9] 固件版本
    ///         [i+12] 充电标志
    ///         [i+13] 电量 0..100
    ///
    /// ⚠️ **充电时电量字段会被填成 100 当哨兵值**（实测：拔线 58%，
    /// 插线瞬间变 100%，物理上不可能）。所以充电时若读到 100，
    /// 应当退回最近一次非充电读数，而不是显示「充电中 100%」。
    ///
    /// ⚠️ 命令字节的位置要在前 3 字节里**扫描**而不是写死下标 0 ——
    /// 带 report id 与不带的缓冲区相差一个字节，写死必然有一边读错。
    ///
    /// 出处：FerrisPulse AcerDeviceHelper（VID 249A / 248A，PID 5C2F）。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class AcerProtocol
    {
        /// <summary>2.4G 接收器</summary>
        public const int VendorIdWireless = 0x249A;
        /// <summary>有线变体</summary>
        public const int VendorIdWired = 0x248A;

        public const int UsagePage = 0xFF01;
        public const int Usage = 0x0010;

        public const int ReportLength = 33;

        /// <summary>设备信息查询命令</summary>
        public const byte CommandDeviceInfo = 0x10;

        /// <summary>电量相对命令字节的偏移</summary>
        public const int BatteryOffset = 13;
        /// <summary>充电标志相对命令字节的偏移</summary>
        public const int ChargingOffset = 12;

        /// <summary>充电时设备用这个值充当「电量」哨兵</summary>
        public const int ChargingSentinelPercent = 100;

        public static byte[] BuildInfoQuery(int length = ReportLength)
        {
            var frame = new byte[length];
            frame[0] = 0x00;
            frame[1] = CommandDeviceInfo;
            frame[2] = 0x00;
            return frame;
        }

        /// <summary>
        /// 解析设备信息应答。
        ///
        /// 命令字节在 [0]、[1] 或 [2]（取决于缓冲区是否带 report id），
        /// 所以前 3 个位置都要试，取第一个匹配的。
        /// </summary>
        public static bool TryParse(byte[]? report, out int percent,
            out bool charging)
        {
            percent = -1;
            charging = false;

            if (report == null || report.Length < ReportLength) return false;

            int commandIndex = -1;
            for (int i = 0; i < 3 && i + ChargingOffset < report.Length; i++)
            {
                if (report[i] == CommandDeviceInfo && report[i + 1] == 0x00)
                {
                    commandIndex = i;
                    break;
                }
            }
            if (commandIndex < 0) return false;

            int payloadLength = report[commandIndex + 3];
            if (payloadLength < 11) return false;

            // 设备 ID 必须是 4 个可打印 ASCII —— 否则这不是设备信息帧
            for (int i = 0; i < 4; i++)
            {
                byte c = report[commandIndex + 4 + i];
                if (c < 0x20 || c > 0x7E) return false;
            }

            int battery = report[commandIndex + BatteryOffset];
            if (battery < 0 || battery > 100) return false;

            percent = battery;
            charging = report[commandIndex + ChargingOffset] != 0;
            return true;
        }

        /// <summary>
        /// 把「充电时读到的原始电量」换算成可展示值。
        ///
        /// 充电时设备会填 100 当哨兵。此时退回最近一次非充电读数；
        /// 没有历史值就原样返回（宁可显示 100%，也不编一个假数字）。
        /// </summary>
        public static int ResolveDisplayed(int rawPercent, bool charging,
            int lastRealPercent)
        {
            if (!charging) return rawPercent;
            if (rawPercent >= ChargingSentinelPercent
                && lastRealPercent >= 0
                && lastRealPercent < ChargingSentinelPercent)
            {
                return lastRealPercent;
            }
            return rawPercent;
        }
    }

    /// <summary>
    /// HyperX（现属 HP）飓风3 无线耳机。
    ///
    /// 62 字节报文，report id 固定 `0x66`：
    ///   请求  [0]=66 [1]=命令码，其余 0
    ///   应答  [0]=66 [1]=响应码 [2][3]=状态位 [4]=电量
    ///
    /// 三条独立命令（各发一次）：
    ///   `0x82` → `0x0B` 连接状态
    ///   `0x89` → `0x0D` 电量（[4]）
    ///   `0x8A` → `0x0C` 充电状态
    ///
    /// ⚠️ 必须排除 `0x06BE`（飓风3S）—— 它的报文协议不同，
    /// 套这套解析会读出垃圾。实测排除项写在这里，不要「顺手」加回来。
    ///
    /// 出处：FerrisPulse HyperXDeviceHelper。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class HyperXProtocol
    {
        /// <summary>HP Inc.（HyperX 现属 HP）</summary>
        public const int VendorId = 0x03F0;

        /// <summary>飓风3 无线接收器</summary>
        public const int PidCloud3 = 0x05B7;
        /// <summary>飓风3 无线接收器（批次变体）</summary>
        public const int PidCloud3Alt = 0x0C9D;

        public const byte ReportId = 0x66;
        public const int PacketLength = 62;

        public const byte CmdGetConnected = 0x82;
        public const byte CmdGetBattery = 0x89;
        public const byte CmdGetCharging = 0x8A;

        public const byte RspConnected = 0x0B;
        public const byte RspBattery = 0x0D;
        public const byte RspCharging = 0x0C;

        /// <summary>电量字段偏移</summary>
        public const int OffsetLevel = 4;

        /// <summary>构造查询帧</summary>
        public static byte[] BuildQuery(byte command)
        {
            var frame = new byte[PacketLength];
            frame[0] = ReportId;
            frame[1] = command;
            return frame;
        }

        /// <summary>应答的响应码是否匹配预期</summary>
        public static bool IsReply(byte[]? report, byte expectedResponse)
        {
            if (report == null || report.Length < PacketLength - 1) return false;
            if (report[0] != ReportId) return false;
            return report[1] == expectedResponse;
        }

        /// <summary>解析电量（响应码 0x0D，电量在 [4]）</summary>
        public static bool TryParseBattery(byte[]? report, out int percent)
        {
            percent = -1;
            if (!IsReply(report, RspBattery)) return false;
            int value = report![OffsetLevel];
            if (value > 100) return false;
            percent = value;
            return true;
        }

        /// <summary>解析充电状态（响应码 0x0C）</summary>
        public static bool TryParseCharging(byte[]? report, out bool charging)
        {
            charging = false;
            if (!IsReply(report, RspCharging)) return false;
            int value = report![OffsetLevel];
            if (value > 1) return false;
            charging = value == 1;
            return true;
        }
    }

    /// <summary>
    /// 盖世小鸡（GameSir）启明星2 手柄。
    ///
    /// 65 字节报告，`usagePage 0xFF7A / usage 1`：
    ///   请求  [0]=00 [1]=01 [2]=01，其余 0
    ///   应答  [0]=00 [1]=01 [2..7]=设备标识（非全零才算有效）
    ///         [19]=电量 0..100，或 **255 表示"正在充电但固件不给百分比"**
    ///
    /// ⚠️ 255 这个哨兵值必须特殊处理：它不是电量，是「充电中但读不到数」。
    /// 当成 100% 会误导用户（以为充满），当成 0% 会误报低电量。
    /// 正确做法是：保持上一次的百分比，状态标「正在充电」。
    ///
    /// 出处：FerrisPulse GameSirDeviceHelper（PID 0x1098 / 0x100F 实测）。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class GameSirProtocol
    {
        public const int VendorId = 0x3537;

        /// <summary>2.4G 接收器</summary>
        public const int PidWireless = 0x1098;
        /// <summary>USB 有线</summary>
        public const int PidWired = 0x100F;

        public const int UsagePage = 0xFF7A;
        public const int Usage = 0x0001;
        public const int ReportLength = 65;

        /// <summary>电量字段偏移</summary>
        public const int BatteryOffset = 19;

        /// <summary>「正在充电，固件不提供百分比」的哨兵值</summary>
        public const byte ChargingSentinel = 255;

        public static byte[] BuildInfoQuery()
        {
            var frame = new byte[ReportLength];
            frame[1] = 0x01;
            frame[2] = 0x01;
            return frame;
        }

        /// <summary>
        /// 解析设备信息应答。
        ///
        /// chargingOnly 为 true 表示「设备在线且在充电，但拿不到百分比」——
        /// 调用方应当保留上一次读数，而不是显示 0% 或 100%。
        /// </summary>
        public static bool TryParse(byte[]? report, out int percent, out bool charging,
            out bool chargingOnly)
        {
            percent = -1;
            charging = false;
            chargingOnly = false;

            if (report == null || report.Length != ReportLength) return false;
            if (report[0] != 0x00 || report[1] != 0x01) return false;

            // 设备标识：2..7 必须至少有一个非零字节，否则是空帧
            bool hasIdentity = false;
            for (int i = 2; i < 8; i++)
            {
                if (report[i] != 0) { hasIdentity = true; break; }
            }
            if (!hasIdentity) return false;

            int raw = report[BatteryOffset];

            if (raw == ChargingSentinel)
            {
                // 充电中但没有百分比 —— 这是合法的状态，不是错误
                charging = true;
                chargingOnly = true;
                return true;
            }

            if (raw > 100) return false;

            percent = raw;
            return true;
        }
    }

    /// <summary>
    /// 华硕 ROG PELTA 耳机 / Scope II RX 键盘。
    ///
    /// 64 字节报告，`usagePage 0xFF00 / usage 1`，帧头 `CC 12`：
    ///   请求  [0]=CC [1]=12 [2]=key，其余 0
    ///   应答  [0]=CC [1]=12 [2]=key [3]=00 [4]=00 …
    ///         key 7 → [6] = 电量 0..100
    ///         key 8 → [5] = 充电 0/1
    ///
    /// ⚠️ 有一个必须排除的伪应答：`[5]==0xFF && [6]==0xAA` 是设备的
    /// 「未就绪占位帧」，不排除会读出一个看似合法但其实是垃圾的电量。
    ///
    /// 电量与充电是两个**独立命令**（key 7 / key 8），要发两次。
    ///
    /// 出处：FerrisPulse AsusDeviceHelper（PR #39，PID 0x1B84 / 0x1AB5）。
    /// 本仓库未真机验证，🟡。
    /// </summary>
    public static class AsusProtocol
    {
        public const int VendorId = 0x0B05;

        /// <summary>ROG PELTA 无线耳机接收器</summary>
        public const int PidPelta = 0x1B84;
        /// <summary>ROG Scope II RX 键盘</summary>
        public const int PidScopeII = 0x1AB5;

        public const int UsagePage = 0xFF00;
        public const int Usage = 0x0001;

        /// <summary>PELTA 用 64 字节，Scope II 用 65 字节</summary>
        public const int ReportLengthPelta = 64;
        public const int ReportLengthScope = 65;

        public const byte Magic0 = 0xCC;
        public const byte Magic1 = 0x12;

        /// <summary>读电量</summary>
        public const byte KeyBattery = 7;
        /// <summary>读充电状态</summary>
        public const byte KeyCharging = 8;

        /// <summary>构造 64 字节请求</summary>
        public static byte[] BuildQuery(byte key, int length = ReportLengthPelta)
        {
            var frame = new byte[length];
            frame[0] = Magic0;
            frame[1] = Magic1;
            frame[2] = key;
            return frame;
        }

        /// <summary>
        /// 判断是不是对指定 key 的合法应答。
        ///
        /// 必须排除 `[5]=FF [6]=AA` 的占位帧 —— 那是设备还没准备好时
        /// 回的东西，字段位置恰好符合格式，不排除就会读出垃圾值。
        /// </summary>
        public static bool IsReply(byte[]? report, byte key)
        {
            if (report == null || report.Length < 8) return false;
            if (report[0] != Magic0 || report[1] != Magic1) return false;
            if (report[2] != key) return false;
            if (report[3] != 0x00 || report[4] != 0x00) return false;
            if (report[5] == 0xFF && report[6] == 0xAA) return false;
            return true;
        }

        /// <summary>解析电量应答（key 7）</summary>
        public static bool TryParseBattery(byte[]? report, out int percent)
        {
            percent = -1;
            if (!IsReply(report, KeyBattery)) return false;
            if (report![6] > 100) return false;
            percent = report[6];
            return true;
        }

        /// <summary>解析充电应答（key 8）；0/1 之外的值不可信</summary>
        public static bool TryParseCharging(byte[]? report, out bool charging)
        {
            charging = false;
            if (!IsReply(report, KeyCharging)) return false;
            if (report![5] > 1) return false;
            charging = report[5] == 1;
            return true;
        }
    }

    /// <summary>
    /// 迈从（MCHOSE）鼠标的控制通道：**Feature Report + 数据异或 0xFF**。
    ///
    /// 与前面几家最大的不同：它不用中断端点，而是
    ///   `HidD_SetFeature` 写命令 → 等 ~95ms → `HidD_GetFeature` 读结果。
    ///
    /// 帧格式（20 字节，report id 0x11）：
    ///   请求  [0]=0x11  [1..]=payload ^ 0xFF，其余补 0xFF
    ///   应答  [0]=0x11  [1]=命令号 ^ 0xFF  [2..]=payload ^ 0xFF
    ///
    /// 三个必须照做的细节：
    ///   1. **整帧异或 0xFF**，包括空白填充字节（所以空白是 0x00 的取反）
    ///   2. 回显校验用 `response[1] == body[0] ^ 0xFF`，不校验会把
    ///      设备主动更新的状态帧当成应答
    ///   3. **写完必须等**（实测 95ms 起步，重试时递增）。
    ///      控制通道是同步的，立刻读拿到的是上一次的数据
    ///
    /// 出处：FerrisPulse MchoseMouseHelper。
    /// 本仓库未真机验证，等级 🟡。
    /// </summary>
    public static class MchoseMouseProtocol
    {
        /// <summary>厂商号；ALT 是部分批次的另一个 VID</summary>
        public const int VendorId = 0x3837;
        public const int VendorIdAlt = 0x5253;

        /// <summary>短报告：20 字节 payload + 1 字节 report id</summary>
        public const byte ReportShort = 0x11;
        /// <summary>长报告：64 字节 payload + 1 字节 report id</summary>
        public const byte ReportLong = 0x12;

        /// <summary>读设备信息（含电量）</summary>
        public const byte CmdIdentity = 0x06;

        /// <summary>短报告载荷长度</summary>
        public const int ShortPayload = 20;

        /// <summary>
        /// 构造 21 字节请求（1B report id + 20B 载荷）。
        /// 空字节补 0xFF，载荷整体异或 0xFF。
        /// </summary>
        public static byte[] BuildIdentityRequest()
        {
            var frame = new byte[ShortPayload + 1];
            for (int i = 0; i < frame.Length; i++) frame[i] = 0xFF;
            frame[0] = ReportShort;
            frame[1] = (byte)(CmdIdentity ^ 0xFF);
            return frame;
        }

        /// <summary>
        /// 解析 identity 应答。
        ///
        /// 解掩码后的载荷布局：
        ///   [0..1] VID   [2..3] PID   [4..7] 固件版本
        ///   [8]    状态标志  [9] 电量 0..100  [10] 充电 0/1
        ///
        /// 校验要点：VID/PID 全零说明设备还没准备好；电量越界说明这不是
        /// 电量帧 —— 两种情况都必须返回失败，绝不能当 0% 用。
        /// </summary>
        public static bool TryParseIdentity(byte[]? response, out int percent,
            out bool charging, out int vendorId, out int productId)
        {
            percent = -1;
            charging = false;
            vendorId = 0;
            productId = 0;

            if (response == null || response.Length < ShortPayload + 1) return false;
            if (response[0] != ReportShort) return false;
            if (response[1] != (byte)(CmdIdentity ^ 0xFF)) return false;

            // 解掩码：payload 从 response[2] 开始，共 19 字节
            var payload = new byte[ShortPayload - 1];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(response[i + 2] ^ 0xFF);
            }
            if (payload.Length < 11) return false;

            vendorId = payload[0] | (payload[1] << 8);
            productId = payload[2] | (payload[3] << 8);
            int battery = payload[9];
            int charge = payload[10];

            if (vendorId == 0 && productId == 0) return false;
            if (battery < 0 || battery > 100) return false;

            percent = battery;
            charging = charge == 1;
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
