// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [装配，不是逻辑] 本文件只做「DeviceDefinition → 可执行的读函数」这一层
//    转换。协议字节、匹配细节都在各自的 Reader 里，这里不许出现任何偏移量。
// 2. [协议链是数据] 一台设备的多个通道按声明顺序尝试，第一条读到就停。
//    新增「同协议的另一个型号」只需要改 DeviceCatalog，本文件不用动 ——
//    除非引入了**新的协议类型**，那才需要在这里补一个 case。
// 3. [失败必须可解释] 全部通道都失败时，要把每条通道的失败原因串起来返回，
//    否则用户只会看到一句「读不到」，没法判断是没插、没配对还是固件不支持。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PeripheralBattery.Core;
using PeripheralBattery.Models;

namespace PeripheralBattery.Hardware
{
    /// <summary>
    /// 把 <see cref="DeviceDefinition"/> 的协议链装配成一个读函数。
    ///
    /// 这样 Program 只关心「有哪些设备」，不关心「每台设备用哪个 Reader」。
    /// </summary>
    public static class DeviceBinder
    {
        /// <summary>
        /// 为一个设备定义生成读函数。返回的函数可以被反复调用，
        /// 每次调用都会按协议链顺序尝试所有通道。
        /// </summary>
        public static Func<(Reading? reading, string error)> Bind(
            DeviceDefinition definition)
        {
            // Reader 实例在闭包里复用：
            //   * LogitechReader 内部有槽位缓存（避免每轮从 1 号槽盲扫）
            //   * RazerReader / Max102Reader 是无状态的，但复用也无害
            // 每次绑定只创建一次，不要放进读函数里 —— 那会让缓存失效。
            var logi = new LogitechReader();
            var razer = new RazerReader();

            // VIA 族的 Reader 需要按设备参数构造（命令号 / 电量偏移 / 蓝牙前缀），
            // 所以在这里按定义里**第一条 VIA 通道**创建一次并复用。
            // 复用很重要：Reader 内部状态（如超时时钟）不该每轮重建。
            var via = BuildViaReader(definition);

            var channels = definition.Channels?.ToList() ?? new List<ProtocolChannel>();

            return () =>
            {
                if (channels.Count == 0)
                {
                    return (null, definition.Label + " 没有配置任何读取通道");
                }

                var failures = new List<string>();
                foreach (var channel in channels)
                {
                    Reading? reading = null;
                    string error = "";

                    try
                    {
                        (reading, error) = ReadOne(channel, logi, razer, via);
                    }
                    catch (Exception exc)
                    {
                        error = exc.GetType().Name + " " + exc.Message;
                    }

                    if (reading != null) return (reading, "");
                    if (!string.IsNullOrEmpty(error)) failures.Add(error);
                }

                return (null, Summarize(definition, failures));
            };
        }

        /// <summary>
        /// 为设备构造 VIA 族的 Reader。
        ///
        /// 用定义里第一条 VIA 通道的参数；没有 VIA 通道时返回一个
        /// 永远读不到的占位实例（不会被调用，因为链里没有 ViaQmk）。
        /// </summary>
        private static Max102Reader BuildViaReader(DeviceDefinition definition)
        {
            foreach (var channel in definition.Channels)
            {
                if (channel.Kind != ProtocolKind.ViaQmk) continue;

                byte command = channel.BatteryCommand != 0
                    ? channel.BatteryCommand
                    : Protocols.ViaCmdGetBattery;
                int offset = channel.BatteryOffset > 0
                    ? channel.BatteryOffset
                    : 4;

                // 蓝牙前缀从**蓝牙通道**取，不从 VIA 通道取 ——
                // 两者描述的是不同通道，混用会让「VIA 有前缀但没蓝牙」
                // 这类配置出现意外行为。
                string bluetoothPrefix = "";
                foreach (var other in definition.Channels)
                {
                    if (other.Kind == ProtocolKind.BluetoothProperty)
                    {
                        bluetoothPrefix = other.NamePrefix;
                        break;
                    }
                }

                string model = definition.Model;
                foreach (var other in definition.Channels)
                {
                    if (other.Kind == ProtocolKind.ViaQmk && other.Model.Length > 0)
                    {
                        model = other.Model;
                        break;
                    }
                }

                return new Max102Reader(channel.VendorId, channel.ProductId,
                    command, offset, bluetoothPrefix, model);
            }

            return new Max102Reader();
        }

        /// <summary>按通道类型分派到对应的 Reader</summary>
        private static (Reading? reading, string error) ReadOne(
            ProtocolChannel channel, LogitechReader logi, RazerReader razer,
            Max102Reader via)
        {
            // 每条通道只调用对应的**单通道**方法：
            // Reader.Read() 是「设备级」的降级链（先私有协议再蓝牙），
            // 那是旧架构留下的。现在链路由 DeviceCatalog 声明，
            // 混用会让「只配蓝牙」的设备仍去做一轮 HID 枚举。
            switch (channel.Kind)
            {
                case ProtocolKind.LogitechHidpp:
                    return Filter(logi.Read(), channel);

                case ProtocolKind.RazerPaPi:
                    return Filter(razer.ReadWirelessOnly(), channel);

                case ProtocolKind.ViaQmk:
                    return Filter(via.ReadViaOnly(), channel);

                case ProtocolKind.BluetoothProperty:
                    return ReadBluetooth(channel);

                default:
                    return (null, "未知协议 " + channel.Kind);
            }
        }

        /// <summary>
        /// VID/PID/名字过滤。
        ///
        /// Reader 本身是「读这个东西」，它不知道也不该知道用户配的是哪个型号。
        /// 过滤放在这里，新增型号才能只改数据表。
        ///
        /// 现在的实现是「读完再判」而不是「先判再读」：Reader 内部已经做了
        /// 枚举与择优（罗技选接收器、雷蛇按 PID 排序），在这里重做一遍
        /// 枚举反而会重复打开句柄。代价是多花几毫秒，换来的是 Reader 完全
        /// 不需要感知型号 —— 这个交换是划算的。
        /// </summary>
        private static (Reading? reading, string error) Filter(
            (Reading? reading, string error) result, ProtocolChannel channel)
        {
            if (result.reading == null) return result;

            var reading = result.reading;
            string name = reading.Name ?? "";

            if (channel.NamePrefix.Length > 0 &&
                name.IndexOf(channel.NamePrefix, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return (null, "名字不含 \"" + channel.NamePrefix + "\"（读到的是 "
                    + (name.Length > 0 ? name : "无型号") + "）");
            }

            // VID/PID 目前只用于「这条通道该不该试」，真正匹配由 Reader 内部
            // 的枚举完成（它拿得到 HID 描述符，这里拿不到）。
            return (reading, "");
        }

        /// <summary>蓝牙通道：按名字前缀与「是否经典蓝牙」读系统属性</summary>
        private static (Reading? reading, string error) ReadBluetooth(
            ProtocolChannel channel)
        {
            if (channel.NamePrefix.Length == 0)
            {
                return (null, "蓝牙通道必须配置 name_prefix");
            }

            BluetoothBatteryInfo? info;
            try
            {
                info = BluetoothBattery.QueryOne(channel.NamePrefix,
                    channel.ClassicBluetoothOnly);
            }
            catch (Exception exc)
            {
                return (null, "读蓝牙电量失败：" + exc.Message);
            }

            if (info == null)
            {
                return (null, "没有找到蓝牙设备 \"" + channel.NamePrefix + "\"");
            }
            if (!info.Percent.HasValue)
            {
                return (null, "蓝牙节点存在但系统还没读到电量");
            }

            // 系统属性没有充电位（pid 3 实测恒缺失），传 -1 让文案如实说「未知」
            var state = Protocols.InterpretBluetoothState(info.Connected, charging: -1);

            // 型号名：优先用设备定义里写的（用户能改），
            // 反推 PnP 友好名只是兜底 —— 例如键盘的友好名是 "MAX102-BT1"，
            // 直接显示它会让用户以为是另一个型号。
            string name = channel.Model.Length > 0
                ? channel.Model
                : Protocols.RazerModelNameFromBluetooth(info.FriendlyName);

            return (new Reading
            {
                Name = name,
                Percent = info.Percent,
                Charging = state.Charging,
                Connected = state.Connected,
                Status = state.Status,
                Transport = "蓝牙",
            }, "");
        }

        /// <summary>
        /// 把所有通道的失败原因合成一句话。
        ///
        /// 只保留前两条：托盘悬停提示有 63 字符上限，三条以上就会被裁掉，
        /// 裁掉的部分通常恰好是「最有用的那条」。
        /// </summary>
        private static string Summarize(DeviceDefinition definition,
            List<string> failures)
        {
            if (failures.Count == 0)
            {
                return definition.Label + " 没有应答";
            }

            // 单条失败时原样返回 —— Reader 的文案里通常已经带设备名
            // （「罗技设备已离线…」），再加一次前缀会变成「鼠标：鼠标：…」。
            if (failures.Count == 1) return failures[0];

            var builder = new StringBuilder();
            for (int i = 0; i < Math.Min(2, failures.Count); i++)
            {
                if (i > 0) builder.Append("；");
                builder.Append(failures[i]);
            }
            if (failures.Count > 2)
            {
                builder.Append("（共 ").Append(failures.Count).Append(" 条通道失败）");
            }
            return builder.ToString();
        }
    }
}
