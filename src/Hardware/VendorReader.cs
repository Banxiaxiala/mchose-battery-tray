// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [I/O 与解析分离] 本文件只做「枚举 → 写 → 读 → 交给纯函数解析」。
//    字节布局全部在 VendorProtocols.cs，这里不许出现偏移量。
// 2. [一个 Reader 服务三个协议族] ATK / Flydigi / Edifier 的 I/O 流程完全一样
//    （找端点 → 写一帧 → 等匹配应答），差别只在帧格式。
//    所以用一次枚举 + 按协议分派的写法，而不是三个几乎一样的 Reader。
// 3. [必须校验应答身份] 键盘鼠标会主动推状态帧。不校验命令号就直接取
//    某个固定偏移，会把推送当电量 —— 这是最容易出的错，且很难发现。
// 4. [读不到时说清为什么] 「找不到端点」与「端点在了但没应答」
//    排查方向完全不同，文案必须分开。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PeripheralBattery.Core;
using PeripheralBattery.Models;

namespace PeripheralBattery.Hardware
{
    /// <summary>
    /// 厂商私有协议的通用读取器（ATK / 飞智 / 漫步者）。
    ///
    /// 这三家的报文互不相同，但 I/O 流程一致，所以共用一套骨架。
    /// 需要新增一家时：在 <see cref="VendorKind"/> 加一个值、
    /// 在 VendorProtocols 加纯解析、在这里补一个分派分支。
    /// </summary>
    public sealed class VendorReader
    {
        /// <summary>支持哪些厂商协议</summary>
        public enum VendorKind
        {
            Atk,
            Flydigi,
            Edifier,
            Mchose,
            GameSir,
            Asus,
            HyperX,
            Acer,
            MchoseKeyboard,
            Nuphy,
        }

        /// <summary>单次读超时。私有协议实测应答在 30ms 内，给 400ms 覆盖唤醒。</summary>
        private const double QueryTimeout = 0.4;

        /// <summary>
        /// 解析一条应答并取出数值。
        ///
        /// 用自定义委托而不是 Func：Func 不支持 out 参数，
        /// 而这些 TryParse 系列的签名都是 `bool TryParse(byte[], out int)`。
        /// </summary>
        private delegate bool ReplyParser(byte[] report, out int value);

        private readonly VendorKind _kind;
        private readonly string _model;

        public VendorReader(VendorKind kind, string model)
        {
            _kind = kind;
            _model = model;
        }

        public (Reading? reading, string error) Read()
        {
            switch (_kind)
            {
                case VendorKind.Atk:
                    return ReadAtk();
                case VendorKind.Flydigi:
                    return ReadFlydigi();
                case VendorKind.Edifier:
                    return ReadEdifier();
                case VendorKind.Mchose:
                    return ReadMchose();
                case VendorKind.GameSir:
                    return ReadGameSir();
                case VendorKind.Asus:
                    return ReadAsus();
                case VendorKind.HyperX:
                    return ReadHyperX();
                case VendorKind.Acer:
                    return ReadAcer();
                case VendorKind.MchoseKeyboard:
                    return ReadMchoseKeyboard();
                case VendorKind.Nuphy:
                    return ReadNuphy();
                default:
                    return (null, "未知厂商协议 " + _kind);
            }
        }

        // ============================================================ ATK
        private (Reading? reading, string error) ReadAtk()
        {
            var interfaces = HidEnumerator.Enumerate(AtkVendorProtocol.VendorId)
                .Where(i => i.UsagePage == AtkVendorProtocol.UsagePage
                         && i.Usage == AtkVendorProtocol.Usage)
                .ToList();

            Tracer.Section(_model + " ATK 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的 ATK 接口（未插线/未连 2.4G）");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try
                {
                    // 键盘类集合要读写权限才能真正发命令
                    device = new HidDevice(iface.Path);
                }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    byte[] request = AtkVendorProtocol.BuildQuery(
                        AtkVendorProtocol.CmdBattery, iface.OutputLength > 0
                            ? iface.OutputLength : AtkVendorProtocol.ReportLength);

                    Tracer.Frame("[ATK] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(
                            iface.InputLength > 0
                                ? iface.InputLength
                                : AtkVendorProtocol.ReportLength, 0.05);
                        if (frame.Length == 0) continue;

                        if (AtkVendorProtocol.TryParse(frame,
                                AtkVendorProtocol.CmdBattery, out int percent,
                                out bool charging))
                        {
                            Tracer.Frame($"[ATK] 收 ← ({percent}%)", frame);
                            return (Build(percent, charging, "ATK"), "");
                        }
                        Tracer.Frame("[ATK] 收 ← （命令号不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ 飞智
        private (Reading? reading, string error) ReadFlydigi()
        {
            var interfaces = HidEnumerator.Enumerate(FlydigiProtocol.VendorId)
                .Where(i => i.UsagePage == FlydigiProtocol.UsagePage)
                .ToList();

            Tracer.Section(_model + " 飞智接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的飞智接口（未插线/未连 2.4G）");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    // 请求帧：00 5A A5 01 …（前导 0 表示带 report id）
                    int length = iface.OutputLength > 0 ? iface.OutputLength : 32;
                    length = Math.Max(12, Math.Min(length, 65));
                    var request = new byte[length];
                    request[0] = 0x00;
                    request[1] = FlydigiProtocol.Magic0;
                    request[2] = FlydigiProtocol.Magic1;
                    request[3] = FlydigiProtocol.InfoCommand;

                    Tracer.Frame("[飞智] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        int readLength = iface.InputLength > 0 ? iface.InputLength : 32;
                        var frame = device.Read(readLength, 0.05);
                        if (frame.Length == 0) continue;

                        if (FlydigiProtocol.TryParseInfo(frame, frame.Length,
                                out int percent, out bool charging, out int mode))
                        {
                            Tracer.Frame($"[飞智] 收 ← ({percent}% {mode})", frame);
                            var reading = Build(percent, charging, "飞智");
                            reading.Status = FlydigiProtocol.ConnectionText(mode);
                            return (reading, "");
                        }
                        Tracer.Frame("[飞智] 收 ← （帧头/命令号不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ 漫步者
        private (Reading? reading, string error) ReadEdifier()
        {
            var interfaces = HidEnumerator.Enumerate(EdifierProtocol.VendorId)
                .Where(i => i.OutputLength > 0)
                .ToList();

            Tracer.Section(_model + " 漫步者接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的漫步者接口（未插线/未连 2.4G）");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    byte[]? request = EdifierProtocol.BuildFrame(
                        EdifierProtocol.CmdBatteryQuery, Array.Empty<byte>());
                    if (request == null)
                    {
                        lastError = "构造漫步者请求帧失败";
                        continue;
                    }

                    Tracer.Frame("[漫步者] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(EdifierProtocol.ReportLength, 0.05);
                        if (frame.Length == 0) continue;

                        if (EdifierProtocol.TryParseReport(frame,
                                EdifierProtocol.CmdBatteryQuery, out byte[] payload)
                            && EdifierProtocol.TryReadBattery(payload, out int percent))
                        {
                            Tracer.Frame($"[漫步者] 收 ← ({percent}%)", frame);
                            return (Build(percent, false, "EC"), "");
                        }
                        Tracer.Frame("[漫步者] 收 ← （校验/命令号不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ 迈从
        //
        // 与其他三家最大的不同：不走中断端点，而是 Feature Report 一问一答。
        // 所以这里既不用 Write 也不用 Read，而是 SetFeature + 等待 + GetFeature。
        private (Reading? reading, string error) ReadMchose()
        {
            var interfaces = HidEnumerator.Enumerate(MchoseMouseProtocol.VendorId)
                .Concat(HidEnumerator.Enumerate(MchoseMouseProtocol.VendorIdAlt))
                .Where(i => i.FeatureLength >= MchoseMouseProtocol.ShortPayload + 1)
                .ToList();

            Tracer.Section(_model + " 迈从接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的迈从控制接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    // 设备需要几十毫秒处理命令；重试时间递增。
                    // 立刻读会拿到上一次的数据 —— 这是控制通道的固有特性。
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        byte[] request = MchoseMouseProtocol.BuildIdentityRequest();
                        Tracer.Frame("[迈从] SetFeature →", request);

                        if (!device.SetFeature(request))
                        {
                            lastError = _model + " 写 Feature Report 失败";
                            continue;
                        }

                        System.Threading.Thread.Sleep(95 + attempt * 40);

                        var response = new byte[MchoseMouseProtocol.ShortPayload + 1];
                        response[0] = MchoseMouseProtocol.ReportShort;

                        if (!device.GetFeature(response))
                        {
                            Tracer.Note("[迈从] 读回全零，设备可能还没准备好");
                            continue;
                        }

                        Tracer.Frame("[迈从] GetFeature ←", response);

                        if (MchoseMouseProtocol.TryParseIdentity(response,
                                out int percent, out bool charging,
                                out int vid, out int pid))
                        {
                            Tracer.Note($"[迈从] VID={vid:X4} PID={pid:X4} {percent}%");
                            return (Build(percent, charging, "Feature"), "");
                        }
                        lastError = _model + " 应答校验失败";
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ 盖世小鸡
        private (Reading? reading, string error) ReadGameSir()
        {
            var interfaces = HidEnumerator.Enumerate(GameSirProtocol.VendorId)
                .Where(i => i.UsagePage == GameSirProtocol.UsagePage
                         && i.InputLength >= GameSirProtocol.ReportLength)
                .ToList();

            Tracer.Section(_model + " GameSir 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的 GameSir 接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    byte[] request = GameSirProtocol.BuildInfoQuery();
                    Tracer.Frame("[GameSir] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(GameSirProtocol.ReportLength, 0.05);
                        if (frame.Length == 0) continue;

                        if (GameSirProtocol.TryParse(frame, out int percent,
                                out bool charging, out bool chargingOnly))
                        {
                            Tracer.Frame($"[GameSir] 收 ← ({percent}%"
                                + (chargingOnly ? " 充电中无百分比" : "") + ")", frame);

                            if (chargingOnly)
                            {
                                // 哨兵值：设备在充电但固件不给电量。
                                // 如实说明，不伪造 0% 也不伪造 100%。
                                return (new Reading
                                {
                                    Name = _model,
                                    Percent = null,
                                    Charging = true,
                                    Connected = true,
                                    Status = "正在充电（固件不提供百分比）",
                                    Transport = "GameSir",
                                }, "");
                            }

                            return (Build(percent, charging, "GameSir"), "");
                        }
                        Tracer.Frame("[GameSir] 收 ← （帧头/标识不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ 华硕
        private (Reading? reading, string error) ReadAsus()
        {
            var interfaces = HidEnumerator.Enumerate(AsusProtocol.VendorId)
                .Where(i => i.UsagePage == AsusProtocol.UsagePage
                         && i.InputLength >= AsusProtocol.ReportLengthPelta)
                .ToList();

            Tracer.Section(_model + " 华硕接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的华硕接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    int reportLength = iface.OutputLength > 0
                        ? iface.OutputLength : AsusProtocol.ReportLengthPelta;
                    reportLength = Math.Max(AsusProtocol.ReportLengthPelta,
                        Math.Min(reportLength, AsusProtocol.ReportLengthScope));

                    int? battery = QueryAsus(device, AsusProtocol.KeyBattery,
                        reportLength, AsusProtocol.TryParseBattery);
                    if (!battery.HasValue)
                    {
                        lastError = _model + " 读电量超时";
                        continue;
                    }

                    // 充电是**独立命令**，要再发一次。读不到就当未充电 ——
                    // 这个字段丢了不影响电量显示，不必因此判整轮失败。
                    // TryParseCharging 的 out 是 bool，这里包一层转成 int
                    int? charge = QueryAsus(device, AsusProtocol.KeyCharging,
                        reportLength, ParseAsusCharging);
                    bool charging = charge.HasValue && charge.Value == 1;

                    Tracer.Note($"[华硕] 电量={battery.Value} 充电={charge?.ToString() ?? "?"}");
                    return (Build(battery.Value, charging, "ROG"), "");
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        /// <summary>把 TryParseCharging 的 out bool 适配成 out int</summary>
        private static bool ParseAsusCharging(byte[] report, out int value)
        {
            value = -1;
            if (!AsusProtocol.TryParseCharging(report, out bool charging)) return false;
            value = charging ? 1 : 0;
            return true;
        }

        /// <summary>
        /// 发一条华硕 key 命令并等应答。
        ///
        /// parse 回调把「应答 → 数值」这一步委托出去：
        /// 电量（key 7）在 [6]，充电（key 8）在 [5]，解析函数不同但流程一样。
        /// 这样避免写两份几乎相同的循环。
        /// </summary>
        private static int? QueryAsus(HidDevice device, byte key, int reportLength,
            ReplyParser parse)
        {
            byte[] request = AsusProtocol.BuildQuery(key, reportLength);
            Tracer.Frame($"[华硕 key={key}] 发 →", request);
            if (device.Write(request, 0.3) < 0) return null;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < QueryTimeout)
            {
                var frame = device.Read(reportLength, 0.05);
                if (frame.Length == 0) continue;

                if (parse(frame, out int value))
                {
                    Tracer.Frame($"[华硕 key={key}] 收 ← ({value})", frame);
                    return value;
                }
                // 占位帧（[5]=FF [6]=AA）会走到这里，正常丢弃
                Tracer.Frame($"[华硕 key={key}] 收 ← （不匹配，丢弃）", frame);
            }
            return null;
        }

        // ============================================================ HyperX
        private (Reading? reading, string error) ReadHyperX()
        {
            var interfaces = HidEnumerator.Enumerate(HyperXProtocol.VendorId)
                .Where(i => i.InputLength >= HyperXProtocol.PacketLength - 1)
                .ToList();

            Tracer.Section(_model + " HyperX 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的 HyperX 接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    int length = iface.OutputLength > 0
                        ? iface.OutputLength : HyperXProtocol.PacketLength;

                    int? battery = QueryHyperX(device, HyperXProtocol.CmdGetBattery,
                        HyperXProtocol.RspBattery, length, HyperXProtocol.TryParseBattery);
                    if (!battery.HasValue)
                    {
                        lastError = _model + " 读电量超时";
                        continue;
                    }

                    int? charge = QueryHyperX(device, HyperXProtocol.CmdGetCharging,
                        HyperXProtocol.RspCharging, length, ParseHyperXCharging);
                    bool charging = charge.HasValue && charge.Value == 1;

                    Tracer.Note($"[HyperX] 电量={battery.Value} 充电={charge?.ToString() ?? "?"}");
                    return (Build(battery.Value, charging, "HyperX"), "");
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        /// <summary>把 TryParseCharging 的 out bool 适配成 out int</summary>
        private static bool ParseHyperXCharging(byte[] report, out int value)
        {
            value = -1;
            if (!HyperXProtocol.TryParseCharging(report, out bool charging)) return false;
            value = charging ? 1 : 0;
            return true;
        }

        private static int? QueryHyperX(HidDevice device, byte command,
            byte expectedResponse, int length, ReplyParser parse)
        {
            byte[] request = HyperXProtocol.BuildQuery(command);
            Tracer.Frame($"[HyperX cmd=0x{command:X2}] 发 →", request);
            if (device.Write(request, 0.3) < 0) return null;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < QueryTimeout)
            {
                var frame = device.Read(length, 0.05);
                if (frame.Length == 0) continue;

                if (parse(frame, out int value))
                {
                    Tracer.Frame($"[HyperX 0x{expectedResponse:X2}] 收 ← ({value})", frame);
                    return value;
                }
                Tracer.Frame($"[HyperX 0x{expectedResponse:X2}] 收 ← （响应码不符，丢弃）", frame);
            }
            return null;
        }

        // ============================================================ 宏碁
        private (Reading? reading, string error) ReadAcer()
        {
            var interfaces = HidEnumerator.Enumerate(AcerProtocol.VendorIdWireless)
                .Concat(HidEnumerator.Enumerate(AcerProtocol.VendorIdWired))
                .Where(i => i.UsagePage == AcerProtocol.UsagePage
                         || i.OutputLength >= AcerProtocol.ReportLength)
                .ToList();

            Tracer.Section(_model + " 宏碁接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的宏碁接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    int length = iface.OutputLength > 0
                        ? iface.OutputLength : AcerProtocol.ReportLength;
                    length = Math.Max(AcerProtocol.ReportLength, length);

                    byte[] request = AcerProtocol.BuildInfoQuery(length);
                    Tracer.Frame("[宏碁] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(length, 0.05);
                        if (frame.Length < AcerProtocol.ReportLength) continue;

                        if (AcerProtocol.TryParse(frame, out int percent,
                                out bool charging))
                        {
                            Tracer.Frame($"[宏碁] 收 ← ({percent}% 充电={charging})", frame);

                            // 充电时设备用 100 当哨兵，退回上次非充电读数
                            int displayed = AcerProtocol.ResolveDisplayed(
                                percent, charging, _lastRealPercent);
                            if (!charging) _lastRealPercent = percent;

                            return (Build(displayed, charging, "Acer"), "");
                        }
                        Tracer.Frame("[宏碁] 收 ← （命令字节/设备 ID 不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        /// <summary>
        /// 最近一次「非充电」状态下的真实电量。
        ///
        /// 宏碁接收器在充电时会把电量字段填成 100 当哨兵（实测拔线 58%、
        /// 插线瞬间变 100%，物理上不可能），所以需要这个值来还原。
        /// 每个 Reader 实例独立，进程重启后第一次充电会显示 100% —— 可接受。
        /// </summary>
        private int _lastRealPercent = -1;

        // ============================================================ 迈从键盘
        private (Reading? reading, string error) ReadMchoseKeyboard()
        {
            var interfaces = HidEnumerator.Enumerate(MchoseMouseProtocol.VendorId)
                .Where(i => i.OutputLength >= MchoseKeyboardProtocol.ShortReportLength)
                .ToList();

            Tracer.Section(_model + " 迈从键盘接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的迈从键盘接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    int length = iface.OutputLength > 0
                        ? iface.OutputLength : MchoseKeyboardProtocol.ReportLength;

                    // G75 PRO 只收 20 字节，其它型号用 65。
                    // 先按端点能力选：端点只支持 20 就发短帧。
                    bool useShort = length <= MchoseKeyboardProtocol.ShortReportLength;
                    int frameLength = useShort
                        ? MchoseKeyboardProtocol.ShortReportLength
                        : MchoseKeyboardProtocol.ReportLength;
                    byte[] request = useShort
                        ? MchoseKeyboardProtocol.BuildShortQuery()
                        : MchoseKeyboardProtocol.BuildQuery();

                    Tracer.Frame($"[迈从键盘 {frameLength}B] 发 →", request);
                    if (device.Write(request, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(frameLength, 0.05);
                        if (frame.Length < frameLength) continue;

                        if (MchoseKeyboardProtocol.TryParse(frame, frameLength,
                                out int percent, out bool charging))
                        {
                            Tracer.Frame($"[迈从键盘] 收 ← ({percent}% 充电={charging})", frame);
                            return (Build(percent, charging, "MCHOSE"), "");
                        }
                        Tracer.Frame("[迈从键盘] 收 ← （校验/命令号不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        // ============================================================ NuPhy
        private (Reading? reading, string error) ReadNuphy()
        {
            var interfaces = HidEnumerator.Enumerate()
                .Where(i => i.Product != null
                         && i.Product.IndexOf("NuPhy", StringComparison.OrdinalIgnoreCase) >= 0
                         && i.OutputLength >= NuphyProtocol.ReportLength)
                .ToList();

            Tracer.Section(_model + " NuPhy 接口");
            foreach (var iface in interfaces) Tracer.Note(iface.ToString());

            if (interfaces.Count == 0)
            {
                return (null, "没有找到 " + _model + " 的 NuPhy 接口");
            }

            string lastError = _model + " 没有应答";
            foreach (var iface in interfaces)
            {
                HidDevice device;
                try { device = new HidDevice(iface.Path); }
                catch (Exception exc)
                {
                    lastError = "打开 " + _model + " 失败：" + exc.Message;
                    continue;
                }

                try
                {
                    // 第一步：读连接模式（决定电量字段偏移）
                    int mode = -1;
                    byte[] modeQuery = NuphyProtocol.BuildQuery(
                        NuphyProtocol.CmdGetMode, 0, 16);
                    Tracer.Frame("[NuPhy mode] 发 →", modeQuery);
                    if (device.Write(modeQuery, 0.3) >= 0)
                    {
                        var deadline = Stopwatch.StartNew();
                        while (deadline.Elapsed.TotalSeconds < QueryTimeout)
                        {
                            var frame = device.Read(NuphyProtocol.ReportLength, 0.05);
                            if (frame.Length == 0) continue;
                            mode = NuphyProtocol.ParseMode(frame);
                            if (mode >= 0)
                            {
                                Tracer.Frame($"[NuPhy mode] 收 ← 模式={mode}", frame);
                                break;
                            }
                        }
                    }
                    if (mode < 0)
                    {
                        lastError = _model + " 读连接模式失败";
                        continue;
                    }

                    // 第二步：按模式算偏移，读电量
                    ushort offset = NuphyProtocol.BatteryOffsetForMode(mode);
                    byte[] query = NuphyProtocol.BuildQuery(NuphyProtocol.CmdGetBattery,
                        offset, 1);
                    Tracer.Frame($"[NuPhy 电量 offset={offset}] 发 →", query);
                    if (device.Write(query, 0.3) < 0)
                    {
                        lastError = _model + " 写入失败";
                        continue;
                    }

                    var deadline2 = Stopwatch.StartNew();
                    while (deadline2.Elapsed.TotalSeconds < QueryTimeout)
                    {
                        var frame = device.Read(NuphyProtocol.ReportLength, 0.05);
                        if (frame.Length == 0) continue;

                        if (NuphyProtocol.TryParseBattery(frame, out int percent))
                        {
                            Tracer.Frame($"[NuPhy] 收 ← ({percent}%)", frame);
                            // NuPhy 的电量应答里没有独立的充电位，
                            // 参考实现用「电量 >= 100」推断，这里不猜测。
                            return (Build(percent, false, "NuPhy"), "");
                        }
                        Tracer.Frame("[NuPhy] 收 ← （帧头/命令不匹配，丢弃）", frame);
                    }
                    lastError = _model + " 读电量超时";
                }
                finally
                {
                    device.Dispose();
                }
            }
            return (null, lastError);
        }

        private Reading Build(int percent, bool charging, string transport)
        {
            return new Reading
            {
                Name = _model,
                Percent = percent,
                Charging = charging,
                Connected = true,
                Status = charging ? "充电中" : "放电中",
                Transport = transport,
            };
        }
    }
}
