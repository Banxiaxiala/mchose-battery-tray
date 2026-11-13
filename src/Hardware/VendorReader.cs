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
        }

        /// <summary>单次读超时。私有协议实测应答在 30ms 内，给 400ms 覆盖唤醒。</summary>
        private const double QueryTimeout = 0.4;

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
