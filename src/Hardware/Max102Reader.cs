// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [双通道，2.4G 优先] 键盘有 2.4G / 蓝牙 / USB 有线三种形态：
//      * 2.4G（dongle 在，usage 0xFF60/0x61）→ VIA cmd 20 直读，带连接状态
//      * 蓝牙（BTHLE 节点）           → 读 Windows 维护的电量属性
//      * USB 有线                     → cmd 20 回全零，拿不到电量
//    2.4G 优先是因为它同时能拿到“连接状态”和“指示灯”，信息量更大；
//    而且 dongle 在的时候蓝牙通道通常是不活跃的。
// 2. [cmd 20 不是原厂命令] 只有刷过电量补丁的固件才应答。回 0xFF 时必须
//    如实说明「固件没有电量补丁」，不许伪造 0% —— 见 Protocols 顶部的说明。
// 3. [充电状态未知] 主控固件里没有充电检测（X14076 的 CHRG/STDBY 直驱 LED），
//    任何通道都拿不到充没充电。显示成“充电中”是错的。
// 4. [编码] UTF-8 with BOM。
//
// 协议来源：weikav-max102 仓库
//   docs/08-电量补丁方案.md §4（cmd 20 的字节布局与实测样本）
//   docs/18-已知限制.md   §9b（蓝牙只能走系统属性，无 VIA 通路）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PeripheralBattery.Core;
using PeripheralBattery.Models;

namespace PeripheralBattery.Hardware
{
    /// <summary>
    /// VIA 族键盘（维咖 MAX102 / 高斯 GS3108T-PRO 等）：
    /// 2.4G VIA 优先，否则回落蓝牙系统属性。
    ///
    /// 「一族一个 Reader + 参数化」而不是「一个型号一个 Reader」：
    /// 这些键盘共用 0xFF60/0x61、33 字节报文，差别只有命令号与电量偏移。
    /// </summary>
    public sealed class Max102Reader
    {
        /// <summary>cmd 20 的超时。实测应答约 5–30ms，给 400ms 足够覆盖唤醒。</summary>
        private const double QueryTimeout = 0.4;

        // ---------------------------------------------------------- 可配置项
        //
        // 为什么做成可配置：高斯 GS3108T-PRO 等键盘与维咖 MAX102 **同一个端点**
        // （0xFF60 / 0x61，33 字节），差别只有命令号与电量偏移。
        // 把它们做成两条独立的 Reader 会重复 90% 代码，还容易改一处漏一处。

        /// <summary>HID 厂商号；0 = 不限制（由协议链上层按通道过滤）</summary>
        public int VendorId { get; }

        /// <summary>HID 产品号；0 = 不限制</summary>
        public int ProductId { get; }

        /// <summary>读电量的命令号</summary>
        public byte BatteryCommand { get; }

        /// <summary>电量字段在 32 字节载荷里的偏移</summary>
        public int BatteryOffset { get; }

        /// <summary>蓝牙通道的设备名前缀；空串表示不支持蓝牙通道</summary>
        public string BluetoothNamePrefix { get; }

        /// <summary>卡片上显示的型号名</summary>
        public string ModelName { get; }

        /// <summary>维咖 MAX102 的默认配置（cmd 0x14，电量在偏移 4）</summary>
        public Max102Reader()
            : this(Protocols.ViaVendorMax102, Protocols.ViaProductMax102,
                   Protocols.ViaCmdGetBattery, 4, "MAX102", "维咖 MAX102")
        {
        }

        public Max102Reader(int vendorId, int productId, byte batteryCommand,
            int batteryOffset, string bluetoothNamePrefix, string modelName)
        {
            VendorId = vendorId;
            ProductId = productId;
            BatteryCommand = batteryCommand;
            BatteryOffset = batteryOffset;
            BluetoothNamePrefix = bluetoothNamePrefix;
            ModelName = modelName;
        }

        public (Reading? reading, string error) Read()
        {
            var (viaReading, viaError) = ReadWireless();
            if (viaReading != null) return (viaReading, "");

            var (btReading, btError) = ReadBluetooth();
            if (btReading != null) return (btReading, "");

            // 两条都不通：优先报更有信息量的那个
            return (null, ChooseError(viaError, btError));
        }

        // ---------------------------------------------------------- 单通道入口
        //
        // 协议链的编排归 DeviceBinder（见 Hardware/DeviceBinder.cs），
        // 由 DeviceCatalog 的声明决定走哪条。这里只暴露单条通道，
        // 避免「只配了蓝牙」的键盘仍先做一轮 VIA 枚举（那要先打开 HID 句柄）。

        /// <summary>只走 2.4G / USB 的 VIA 通道</summary>
        public (Reading? reading, string error) ReadViaOnly()
        {
            return ReadWireless();
        }

        /// <summary>只走蓝牙系统属性通道</summary>
        public (Reading? reading, string error) ReadBluetoothOnly()
        {
            return ReadBluetooth();
        }

        /// <summary>
        /// 两条链路都失败时的文案。
        ///
        /// 「固件没有电量补丁」是用户能自己解决的信息（去刷补丁），
        /// 比「2.4G 也没插、蓝牙也没连」更该优先展示。
        /// </summary>
        private static string ChooseError(string viaError, string btError)
        {
            if (!string.IsNullOrEmpty(viaError) &&
                viaError.IndexOf("补丁", StringComparison.Ordinal) >= 0)
            {
                return viaError;
            }
            if (!string.IsNullOrEmpty(viaError) && viaError.IndexOf("有线", StringComparison.Ordinal) >= 0)
            {
                return viaError;
            }
            if (!string.IsNullOrEmpty(btError)) return btError;
            return string.IsNullOrEmpty(viaError) ? "键盘未连接" : viaError;
        }

        // ======================================================== 2.4G / USB
        private (Reading? reading, string error) ReadWireless()
        {
            List<HidInterface> interfaces;
            try
            {
                interfaces = VendorId != 0
                    ? (ProductId != 0
                        ? HidEnumerator.Enumerate(VendorId, ProductId)
                        : HidEnumerator.Enumerate(VendorId))
                    : HidEnumerator.Enumerate();
            }
            catch (Exception exc)
            {
                return (null, "枚举 HID 接口失败：" + exc.Message);
            }

            Tracer.Section(ModelName + " HID 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            // 只认 VIA 的厂商集合 0xFF60/0x61：键盘还有键盘/鼠标/多媒体等集合，
            // 那些要么不可写，要么不通 VIA。
            var via = interfaces
                .Where(i => i.UsagePage == Protocols.ViaUsagePage
                         && i.Usage == Protocols.ViaUsage)
                .ToList();

            if (via.Count == 0)
            {
                return (null, "没有找到 " + ModelName + " 的 VIA 接口（未插线/未连 2.4G）");
            }

            // 2.4G 的 product_string 带 “2.4G”，有线是 “MAX102”；
            // 同型号可能同时存在（插着线又连着 dongle），无线优先。
            via.Sort((a, b) =>
            {
                bool aWireless = IsWireless(a.Product);
                bool bWireless = IsWireless(b.Product);
                if (aWireless != bWireless) return aWireless ? -1 : 1;
                return a.InputLength.CompareTo(b.InputLength);
            });

            string lastError = "键盘没有应答 cmd 20";
            foreach (var iface in via)
            {
                HidDevice device;
                try
                {
                    // 键盘集合有防监听独占锁，只能 GENERIC_READ|WRITE + OVERLAPPED，
                    // 这里要真正发命令，所以 readWrite: true。
                    device = new HidDevice(iface.Path);
                }
                catch (Exception exc)
                {
                    lastError = "打开 " + ModelName + " 的 VIA 接口失败：" + exc.Message;
                    continue;
                }

                try
                {
                    byte[]? reply = Query(device, BatteryCommand);
                    if (reply == null)
                    {
                        lastError = ModelName + " 没有应答读电量命令（可能正在休眠）";
                        continue;
                    }

                    var parsed = Protocols.ParseViaBattery(reply, BatteryCommand,
                        BatteryOffset, out string reason);
                    Tracer.Note($"cmd 20 → kind={parsed.Kind} bat={parsed.Percent} "
                              + $"state={parsed.State} ver={parsed.ModuleVersion} {reason}");

                    if (parsed.Kind == ViaBatteryKind.Unsupported)
                    {
                        lastError = reason;
                        continue;
                    }
                    if (parsed.Kind == ViaBatteryKind.Wired)
                    {
                        // USB 有线下无线模块不工作，没有电量可读。
                        // 但设备是“在线”的，名字要照实显示，别让用户以为没连上。
                        lastError = reason;
                        continue;
                    }

                    // 用户配置的型号名优先：product_string 在不同批次的固件里
                    // 可能是 "MAX102-2.4G"、"GS3108T-PRO" 甚至空串
                    string name = ModelName.Length > 0
                        ? ModelName
                        : Protocols.Max102ModelName(iface.Product);
                    return (new Reading
                    {
                        Name = name,
                        Percent = parsed.Percent,
                        // 固件里没有充电检测，任何通道都拿不到
                        Charging = false,
                        // 能应答就说明设备可达（不可达时 dongle 根本不回包，
                        // 会在上面超时返回）。md_info.state 描述的是
                        // “键盘 ↔ 无线模块”的连接状态，只当文案用。
                        Connected = true,
                        Status = Protocols.ViaStateText(parsed.State),
                        Transport = "VIA",
                    }, "");
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        private static bool IsWireless(string? product)
        {
            return product != null
                && product.IndexOf("2.4", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 发一条 VIA 命令并等匹配的应答。
        ///
        /// 返回**剥掉 report id 后的 32 字节载荷**；null 表示超时/写失败。
        /// 这里必须校验命令号，因为键盘会主动推电量变化包（未订阅时也可能
        /// 混进来），把推送当应答会读到错误的数值。
        /// </summary>
        private byte[]? Query(HidDevice device, byte cmd)
        {
            byte[] request = Protocols.BuildViaRequest(cmd);
            Tracer.Frame($"[{ModelName} cmd=0x{cmd:X2}] 发 →", request);
            try
            {
                if (device.Write(request, 0.3) < 0)
                {
                    Tracer.Note("[" + ModelName + "] 写入失败");
                    return null;
                }
            }
            catch (Exception exc)
            {
                Tracer.Note("[" + ModelName + "] 写入异常：" + exc.Message);
                return null;
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < QueryTimeout)
            {
                var frame = device.Read(Protocols.ViaReportLen, 0.05);
                if (frame.Length == 0) continue;

                // ⚠️ Win32 的 ReadFile 返回的缓冲里第 0 字节是 report id，
                // 命令回显在 [1]。hidapi 的 read() 不返回 report id，
                // 直接照搬 Python 代码（r[0] == cmd）会让这里永远不匹配。
                var payload = Protocols.StripReportId(frame, cmd);
                if (payload != null)
                {
                    Tracer.Frame("收 ←", frame);
                    return payload;
                }
                Tracer.Frame("收 ← （命令号不匹配，丢弃）", frame);
            }
            Tracer.Note($"[{ModelName} cmd=0x{cmd:X2}] 超时");
            return null;
        }

        // ======================================================== 蓝牙
        /// <summary>
        /// 蓝牙通道：读 Windows 自己维护的电量属性。
        ///
        /// 键盘在蓝牙模式下没有 VIA 通路（7 个 HID 集合里没有 0xFF60/0x61，
        /// 厂商集合 0xFF00 也不回包），所以只能借系统这条数据。
        /// </summary>
        private (Reading? reading, string error) ReadBluetooth()
        {
            if (BluetoothNamePrefix.Length == 0)
            {
                return (null, ModelName + " 未配置蓝牙通道");
            }
            BluetoothBatteryInfo? info;
            try
            {
                info = BluetoothBattery.QueryOne(BluetoothNamePrefix);
            }
            catch (Exception exc)
            {
                return (null, "读蓝牙电量失败：" + exc.Message);
            }

            if (info == null)
            {
                return (null, "没有找到蓝牙通道的 " + ModelName + "（系统未记录其电量）");
            }
            if (!info.Percent.HasValue)
            {
                return (null, "蓝牙通道有设备节点但系统还没读到电量");
            }

            Tracer.Note($"蓝牙 " + info);

            // 系统属性里的电量是“最后一次读到的”，设备离线时不代表当前值。
            // 用 IsConnected 区分，并在离线时保留百分比（用户想知道睡前剩多少）。
            // 状态文案统一走 Protocols.InterpretBluetoothState，保证与耳机一致 ——
            // 两个 Reader 各写一份文案，改一处就会忘另一处。
            var state = Protocols.InterpretBluetoothState(info.Connected, charging: -1);

            return (new Reading
            {
                Name = ModelName.Length > 0 ? ModelName : "维咖 MAX102",
                Percent = info.Percent,
                Charging = state.Charging,  // 系统属性没有充电状态，恒 false
                Connected = state.Connected,
                Status = state.Status,
                Transport = "蓝牙",
            }, "");
        }
    }
}
