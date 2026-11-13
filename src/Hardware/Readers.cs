// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [只做 I/O 编排] 本文件负责「把字节发出去、把应答收回来」，所有字节布局
//    必须调用 Hardware/Protocols.cs 里的纯函数，严禁在此硬编码偏移量。
// 2. [两遍探测] 槽位探测必须保留「轻探 + 耐心等」两遍：第一遍靠短集合的
//    错误帧毫秒级排除空槽位，第二遍只对真正超时的槽位给足唤醒窗口。
//    改成单遍会让唤醒中的设备永远等不到应答。
// 3. [禁止 SetOutputReport] 雷蛇固件会拒绝 HidD_SetOutputReport（错误码 87），
//    必须走 WriteFile 写中断 OUT 端点。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using PeripheralBattery.Core;
using PeripheralBattery.Models;
using PeripheralBattery.Hardware;

namespace PeripheralBattery.Hardware
{
    /// <summary>一个 LIGHTSPEED 接收器：短集合 + 长集合</summary>
    public sealed class LogitechReceiver : IDisposable
    {
        private readonly HidDevice _short;
        private readonly HidDevice _long;
        private readonly PendingRead _shortPending;
        private readonly PendingRead _longPending;

        public (int vendor, int product, string serial) Key { get; }

        public LogitechReceiver(HidInterface shortIface, HidInterface longIface)
        {
            Key = (longIface.VendorId, longIface.ProductId, longIface.Serial);
            _short = new HidDevice(shortIface.Path);
            _long = new HidDevice(longIface.Path);
            _shortPending = new PendingRead(_short, shortIface.InputLength > 0 ? shortIface.InputLength : 7);
            _longPending = new PendingRead(_long, longIface.InputLength > 0 ? longIface.InputLength : 20);
            _shortPending.Start();
            _longPending.Start();
        }

        public void Dispose()
        {
            _shortPending.Dispose();
            _longPending.Dispose();
            _short.Dispose();
            _long.Dispose();
        }

        private void Drain()
        {
            foreach (var pending in new[] { _longPending, _shortPending })
            {
                for (int i = 0; i < 16; i++)
                {
                    if (pending.Poll(0).Length == 0) break;
                }
            }
        }

        private (string state, byte[] payload) Transfer(int deviceIndex, int featureIndex,
            int function, byte[]? parameters = null, double timeout = Protocols.QuickTimeout)
        {
            Drain();
            byte[] request = Protocols.BuildLogiRequest(deviceIndex, featureIndex, function, parameters);
            Tracer.Frame($"[罗技 slot={deviceIndex} feat=0x{featureIndex:X2} fn={function}] 发 →", request);
            _long.Write(request);

            var deadline = Stopwatch.StartNew();
            while (true)
            {
                double left = timeout - deadline.Elapsed.TotalSeconds;
                if (left <= 0) return ("timeout", Array.Empty<byte>());

                // 先看短集合：不可达时接收器 3~5ms 就在这里回绝
                var frame = _shortPending.Poll(0);
                if (frame.Length > 0 &&
                    Protocols.MatchLogiAbsent(frame, deviceIndex, featureIndex, function))
                {
                    Tracer.Frame("收 ← 短集合错误帧（该槽位不可达）", frame);
                    return ("absent", Array.Empty<byte>());
                }

                frame = _longPending.Poll(Math.Min(left, 0.02));
                if (frame.Length > 0)
                {
                    var payload = Protocols.MatchLogiReply(frame, deviceIndex, featureIndex, function);
                    if (payload != null)
                    {
                        Tracer.Frame("收 ←", frame);
                        return ("ok", payload);
                    }
                    Tracer.Frame("收 ← （四字段不匹配，丢弃）", frame);
                }
            }
        }

        private byte[]? Call(int deviceIndex, int featureIndex, int function,
            byte[]? parameters = null, double timeout = Protocols.QuickTimeout)
        {
            var (state, payload) = Transfer(deviceIndex, featureIndex, function, parameters, timeout);
            return state == "ok" ? payload : null;
        }

        /// <summary>查特性索引；0 表示不支持。设备不在该槽位抛 LookupError 语义</summary>
        private int? FeatureIndex(int deviceIndex, int featureId, out bool absent,
            double timeout = Protocols.QuickTimeout)
        {
            absent = false;
            var (state, payload) = Transfer(deviceIndex, Protocols.FeatRoot, 0x00,
                new[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) }, timeout);
            if (state == "absent") { absent = true; return null; }
            if (state != "ok" || payload.Length == 0) return null;
            return payload[0] == 0 ? null : payload[0];
        }

        private string DeviceName(int deviceIndex, double timeout = Protocols.QuickTimeout)
        {
            int? index = FeatureIndex(deviceIndex, Protocols.FeatDeviceName, out _, timeout);
            if (!index.HasValue || index.Value == 0) return "";

            var head = Call(deviceIndex, index.Value, 0x00, null, timeout);
            if (head == null || head.Length == 0 || head[0] == 0) return "";

            int length = Math.Min((int)head[0], 64);
            var name = new System.Text.StringBuilder();
            for (int offset = 0; offset < length; offset += 16)
            {
                var chunk = Call(deviceIndex, index.Value, 0x01, new[] { (byte)offset }, timeout);
                if (chunk == null || chunk.Length == 0) break;
                int end = Array.IndexOf(chunk, (byte)0);
                if (end < 0) end = chunk.Length;
                name.Append(System.Text.Encoding.UTF8.GetString(chunk, 0, end));
                if (name.Length >= length) break;
            }
            return name.ToString().Trim();
        }

        private (Reading? reading, string label) ReadBattery(int deviceIndex, double timeout)
        {
            var attempts = new (int feature, int parser)[]
            {
                (Protocols.FeatUnifiedBattery, 0),
                (Protocols.FeatBatteryStatus, 1),
                (Protocols.FeatBatteryVoltage, 2),
            };

            foreach (var (feature, parser) in attempts)
            {
                int? index = FeatureIndex(deviceIndex, feature, out bool absent, timeout);
                if (absent) throw new DeviceAbsentException();
                if (!index.HasValue || index.Value == 0) continue;

                if (parser == 0)
                {
                    var caps = Call(deviceIndex, index.Value, 0x00, null, timeout);
                    var status = Call(deviceIndex, index.Value, 0x01, null, timeout);
                    var parsed = Protocols.ParseLogiUnifiedBattery(caps, status);
                    if (parsed.HasValue)
                    {
                        return (new Reading
                        {
                            Percent = parsed.Value.percent,
                            Status = parsed.Value.text,
                            Charging = parsed.Value.charging,
                            Transport = "HID++",
                        }, "HID++");
                    }
                }
                else if (parser == 1)
                {
                    var status = Call(deviceIndex, index.Value, 0x00, null, timeout);
                    var parsed = Protocols.ParseLogiBatteryStatus(status);
                    if (parsed.HasValue)
                    {
                        return (new Reading
                        {
                            Percent = parsed.Value.percent,
                            Status = parsed.Value.text,
                            Charging = parsed.Value.charging,
                            Transport = "0x1000",
                        }, "0x1000");
                    }
                }
                else
                {
                    var data = Call(deviceIndex, index.Value, 0x00, null, timeout);
                    var parsed = Protocols.ParseLogiBatteryVoltage(data);
                    if (parsed.HasValue)
                    {
                        return (new Reading
                        {
                            Percent = parsed.Value.percent,
                            Status = parsed.Value.text,
                            Charging = parsed.Value.charging,
                            Transport = "0x1001",
                        }, "0x1001");
                    }
                }
            }
            return (null, "");
        }

        /// <summary>读一个槽位。返回 (Reading, 槽位状态 ok/absent/timeout)</summary>
        public (Reading? reading, string state) Probe(int deviceIndex,
            double timeout = Protocols.QuickTimeout)
        {
            Reading? reading;
            try
            {
                (reading, _) = ReadBattery(deviceIndex, timeout);
            }
            catch (DeviceAbsentException)
            {
                Tracer.Note($"槽位 {deviceIndex}: 接收器明确回绝（不可达）");
                return (null, "absent");
            }
            catch (Exception exc)
            {
                Tracer.Note($"槽位 {deviceIndex}: 异常 {exc.GetType().Name} {exc.Message}");
                return (null, "timeout");
            }
            if (reading == null)
            {
                Tracer.Note($"槽位 {deviceIndex}: 应答里没有可用电量");
                return (null, "timeout");
            }

            try { reading.Name = DeviceName(deviceIndex, timeout); }
            catch { reading.Name = ""; }
            reading.Connected = true;
            return (reading, "ok");
        }

        private sealed class DeviceAbsentException : Exception { }

        /// <summary>
        /// 把枚举结果配成 (短集合, 长集合)，按优先级排好序。
        ///
        /// 一个罗技 HID++ 端点有两种形态，必须都支持：
        ///
        ///   A. **接收器**（如 046D:C54D "USB Receiver"）
        ///      设备在它后面按 1~7 号槽位分布，要逐个槽位探测。
        ///   B. **设备直连**（如 046D:C09B "PRO X 2"，插着数据线时出现）
        ///      设备自己就提供一个端点，只在 1 号槽位应答。
        ///
        /// 排序规则：**接收器优先**。
        ///
        /// 为什么不能用枚举顺序：两者 PID 不同，`C09B < C54D`，
        /// 按字符串排序会把直连设备排在前面。此时如果鼠标正插着线，
        /// 直连端点会先命中并返回「充电中 100%」，看上去一切正常；
        /// 但一旦拔掉数据线，无线那台的读数就从没被采集过，
        /// 用户会以为工具坏了。接收器优先 + 命中即返回，
        /// 能保证无线链路永远优先被读到。
        ///
        /// 而直连设备本身也提供短/长集合，配对逻辑与接收器完全一致，
        /// 所以这里不需要特判，只排序即可。
        /// </summary>
        public static List<(HidInterface? shortIface, HidInterface longIface)> GroupReceivers(
            List<HidInterface> interfaces)
        {
            var grouped = new Dictionary<string, Dictionary<int, HidInterface>>();
            foreach (var iface in interfaces)
            {
                if (iface.UsagePage != Protocols.LogiUsagePage) continue;
                string key = iface.VendorId + ":" + iface.ProductId + ":" + iface.Serial;
                if (!grouped.TryGetValue(key, out var byUsage))
                {
                    byUsage = new Dictionary<int, HidInterface>();
                    grouped[key] = byUsage;
                }
                byUsage[iface.Usage] = iface;
            }

            var pairs = new List<(HidInterface?, HidInterface)>();
            foreach (var byUsage in grouped.Values)
            {
                byUsage.TryGetValue(Protocols.LogiUsageShort, out var shortIface);
                if (byUsage.TryGetValue(Protocols.LogiUsageLong, out var longIface))
                {
                    pairs.Add((shortIface, longIface));
                }
            }

            // 接收器优先：product 字符串带 "Receiver" 的排前面；
            // 同组内按 PID 保证顺序稳定（枚举顺序在不同批次会变）。
            pairs.Sort((a, b) =>
            {
                bool aReceiver = IsReceiver(a.Item2);
                bool bReceiver = IsReceiver(b.Item2);
                if (aReceiver != bReceiver) return aReceiver ? -1 : 1;
                return a.Item2.ProductId.CompareTo(b.Item2.ProductId);
            });
            return pairs;
        }

        private static bool IsReceiver(HidInterface iface)
        {
            return iface.Product != null
                && iface.Product.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>对外入口：枚举 → 探测槽位 → 返回一台设备的读数</summary>
    public sealed class LogitechReader
    {
        private static readonly int[] Slots = { 1, 2, 3, 4, 5, 6, 7 };
        // 记住上次成功的槽位，避免每轮都从 1 号盲扫
        private static readonly Dictionary<string, int> SlotCache = new();

        public (Reading? reading, string error) Read()
        {
            var interfaces = HidEnumerator.Enumerate(Protocols.LogiVendor);
            Tracer.Section("罗技 HID 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            var pairs = LogitechReceiver.GroupReceivers(interfaces);
            Tracer.Note($"配成 {pairs.Count} 个接收器");
            if (pairs.Count == 0) return (null, "没有找到罗技接收器");

            string lastError = "接收器上没有应答的设备";
            foreach (var (shortIface, longIface) in pairs)
            {
                LogitechReceiver receiver;
                try
                {
                    receiver = new LogitechReceiver(shortIface ?? longIface, longIface);
                }
                catch (Exception exc)
                {
                    lastError = "打开接收器失败：" + exc.Message;
                    continue;
                }

                try
                {
                    var order = Slots.ToList();
                    string cacheKey = string.Join(":", receiver.Key);
                    if (SlotCache.TryGetValue(cacheKey, out int cached) && order.Contains(cached))
                    {
                        order.Remove(cached);
                        order.Insert(0, cached);
                    }

                    // 第一遍：轻探。空槽位靠短集合错误帧毫秒级排除
                    var pending = new List<int>();
                    foreach (int slot in order)
                    {
                        var (reading, state) = receiver.Probe(slot, Protocols.QuickTimeout);
                        if (reading != null)
                        {
                            SlotCache[cacheKey] = slot;
                            return (reading, "");
                        }
                        if (state == "timeout") pending.Add(slot);
                    }

                    // 第二遍：只有「什么都没回」的槽位值得再等 —— 设备可能正在唤醒
                    foreach (int slot in pending)
                    {
                        var (reading, _) = receiver.Probe(slot, Protocols.PatientTimeout);
                        if (reading != null)
                        {
                            SlotCache[cacheKey] = slot;
                            return (reading, "");
                        }
                    }
                    if (pending.Count == 0)
                    {
                        lastError = "罗技设备已离线（接收器上没有设备应答）";
                    }
                }
                finally
                {
                    receiver.Dispose();
                }
            }
            return (null, lastError);
        }
    }

    /// <summary>雷蛇 2.4G 无线耳机</summary>
    public sealed class RazerReader
    {
        /// <summary>
        /// 蓝牙通道的设备名前缀。实测的 PnP 友好名都以此开头：
        ///   Razer BlackShark V2 Pro BT
        ///   Razer BlackShark V2 Pro BT Hands-Free AG
        /// 不能写死完整型号：换一款雷蛇耳机就失效了。
        /// </summary>
        private const string BluetoothNamePrefix = "Razer";

        /// <summary>
        /// 发一条命令，失败重试一次。
        ///
        /// 必须重试：2.4G 空口会丢帧，单次尝试偶发失败就把整轮判成
        /// 「读不到」，界面随之报掉线 —— 这就是「明明在用却说离线」的根因。
        /// FerrisPulse 的 RazerHeadsetHelper.QueryValue 同样是 attempt &lt; 2。
        /// </summary>
        private static int QueryWithRetry(HidDevice device, byte cmd,
            double timeout = 0.6)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int value = Query(device, cmd, timeout);
                if (value >= 0) return value;
            }
            return -1;
        }

        private static int Query(HidDevice device, byte cmd, double timeout = 0.6)
        {
            byte[] request = Protocols.BuildRazerFrame(cmd);
            Tracer.Frame($"[雷蛇 cmd=0x{cmd:X2}] 发 →", request);
            try { device.Write(request, 0.3); }
            catch (Exception exc)
            {
                Tracer.Note("[雷蛇] 写入失败：" + exc.Message);
                return -1;
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < timeout)
            {
                var frame = device.Read(Protocols.RazerReportLen, 0.05);
                if (frame.Length == 0) continue;
                int value = Protocols.ParseRazerReply(frame, cmd);
                if (value >= 0)
                {
                    Tracer.Frame($"收 ← (值={value})", frame);
                    return value;
                }
                Tracer.Frame("收 ← （命令不匹配，丢弃）", frame);
            }
            Tracer.Note($"[雷蛇 cmd=0x{cmd:X2}] 超时，无匹配应答");
            return -1;
        }

        public (Reading? reading, string error) Read()
        {
            var (hidReading, hidError) = ReadWireless();
            if (hidReading != null) return (hidReading, "");

            var (btReading, btError) = ReadBluetooth();
            if (btReading != null) return (btReading, "");

            return (null, ChooseError(hidError, btError));
        }

        /// <summary>
        /// 两条链路都失败时的文案。
        ///
        /// 三种组合要区别对待：
        ///   * 两条都“没找到” → 耳机根本没连，合并成一句，别让用户以为要装驱动
        ///   * 只有蓝牙没找到 → 报 2.4G 的错误（那才是用户能动手的方向：
        ///     “耳机可能已休眠”比“系统未记录蓝牙电量”有用）
        ///   * 其他情况 → 报蓝牙的错误（蓝牙能走通却失败，说明真是读不到）
        /// </summary>
        private static string ChooseError(string hidError, string btError)
        {
            bool hidMissing = hidError.IndexOf("没有找到", StringComparison.Ordinal) >= 0;
            bool btMissing = btError.IndexOf("没有找到", StringComparison.Ordinal) >= 0;

            if (hidMissing && btMissing)
            {
                return "耳机未连接（2.4G 接收器与蓝牙都没找到设备）";
            }
            if (btMissing) return hidError.Length > 0 ? hidError : btError;
            if (!string.IsNullOrEmpty(btError)) return btError;
            return string.IsNullOrEmpty(hidError) ? "耳机未连接" : hidError;
        }

        // ======================================================== 2.4G（PA/PI）
        private (Reading? reading, string error) ReadWireless()
        {
            var all = HidEnumerator.Enumerate(Protocols.RazerVendor);
            Tracer.Section("雷蛇 HID 接口");
            foreach (var iface in all) Tracer.Note(iface.ToString());

            var interfaces = all
                .Where(i => i.UsagePage == Protocols.RazerUsagePage)
                .OrderBy(i => i.ProductId)
                .ThenBy(i => i.Usage != 0x0001)
                .ToList();
            Tracer.Note($"其中 usagePage=0xFF00 的有 {interfaces.Count} 个");

            if (interfaces.Count == 0) return (null, "没有找到雷蛇 2.4G 接口");

            string lastError = "雷蛇设备没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开雷蛇接口失败：" + exc.Message;
                    continue;
                }

                try
                {
                    int battery = QueryWithRetry(device, Protocols.RazerCmdBattery);
                    if (battery < 0 || battery > 100)
                    {
                        lastError = "雷蛇电量查询无应答（耳机可能已休眠）";
                        continue;
                    }

                    int charging = QueryWithRetry(device, Protocols.RazerCmdCharging);
                    int connection = QueryWithRetry(device, Protocols.RazerCmdConnection);

                    // 三种情况的判定全部集中在 Protocols.InterpretRazerState，
                    // 那里有脱机断言锁住语义（尤其 connection == -1 不能当 0）。
                    var state = Protocols.InterpretRazerState(charging, connection);

                    return (new Reading
                    {
                        Name = Protocols.RazerModelName(iface.Product, iface.ProductId),
                        Percent = battery,
                        Charging = state.Charging,
                        Connected = state.Connected,
                        Status = state.Status,
                        Transport = "PA-PI",
                    }, "");
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ======================================================== 蓝牙（系统属性）
        /// <summary>
        /// 耳机在蓝牙模式下没有厂商私有 HID 通道（走的是 A2DP/HFP 标准服务），
        /// 只能读 Windows 自己维护的电量属性。
        ///
        /// ⚠️ 电量挂在经典蓝牙（BTHENUM）的**音频服务子节点**上，
        /// 例如 BTHENUM\{0000111E-…Hands-Free AG\…；而 DEV 父节点没有电量、
        /// 但只有它带 DEVPKEY_Device_IsConnected=True。在线状态由
        /// BluetoothBattery 按 MAC 聚合，这里不能拿子节点的 IsConnected 当结论
        /// （实测恒为 False，会把正在放歌的耳机判成离线）。
        /// </summary>
        private (Reading? reading, string error) ReadBluetooth()
        {
            BluetoothBatteryInfo? info;
            try
            {
                info = BluetoothBattery.QueryOne(BluetoothNamePrefix, classicAudioOnly: true);
            }
            catch (Exception exc)
            {
                return (null, "读蓝牙电量失败：" + exc.Message);
            }

            if (info == null)
            {
                return (null, "没有找到蓝牙模式的雷蛇耳机（系统未记录其电量）");
            }
            if (!info.Percent.HasValue)
            {
                return (null, "蓝牙耳机节点存在但系统还没读到电量");
            }

            Tracer.Note("蓝牙 " + info);

            // 系统属性没有充电状态（pid 3 实测恒缺失），传 -1 让文案如实说「未知」
            var state = Protocols.InterpretBluetoothState(info.Connected, charging: -1);

            return (new Reading
            {
                Name = Protocols.RazerModelNameFromBluetooth(info.FriendlyName),
                Percent = info.Percent,
                Charging = state.Charging,
                Connected = state.Connected,
                Status = state.Status,
                Transport = "蓝牙",
            }, "");
        }
    }
}
