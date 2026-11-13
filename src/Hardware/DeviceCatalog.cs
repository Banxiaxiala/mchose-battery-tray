// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [设备 = 数据，不是代码分支] 想加一台设备，只应该在 DeviceCatalog 里
//    加一条声明（键、标签、协议链、匹配规则），**不要**去改 Program、
//    卡片、托盘、历史里的任何代码。要加的是「新型号」，不是「新逻辑」。
// 2. [协议可组合] 一台设备可能有多条通道（耳机：2.4G 优先，蓝牙回落），
//    所以设备声明里给的是**协议链**，按顺序尝试，第一条读到就停。
// 3. [匹配规则也是数据] 按 VID 还是按蓝牙名前缀，写在声明里；
//    Reader 只负责「给我一个通道，我去读」，不负责判断「这是不是我」。
// 4. [纯数据，零 I/O] 本文件不许出现任何 Win32 / HID / WPF 调用 ——
//    它要能被单元测试直接导入，用来断言「用户配的型号能被正确解析」。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;

namespace PeripheralBattery.Hardware
{
    /// <summary>
    /// 一台设备用哪条通道读。
    ///
    /// 取值的意义：同一个 VID 下可能有多种设备形态（接收器 / 直连），
    /// 由具体的 Reader 根据 <see cref="ProtocolKind"/> 决定用哪套报文。
    /// </summary>
    public enum ProtocolKind
    {
        /// <summary>罗技 HID++ 2.0（短/长集合，槽位探测）</summary>
        LogitechHidpp = 0,
        /// <summary>雷蛇 64 字节 PA/PI 中断报文（2.4G）</summary>
        RazerPaPi = 1,
        /// <summary>维咖 VIA（QMK cmd 20）</summary>
        ViaQmk = 2,
        /// <summary>蓝牙系统电量属性（BTHLE / BTHENUM）</summary>
        BluetoothProperty = 3,
    }

    /// <summary>
    /// 一条可用的读取通道：用哪种协议 + 怎么认出目标设备。
    ///
    /// 匹配字段组合使用（全部非空条件必须同时满足）：
    ///   * <see cref="VendorId"/> / <see cref="ProductId"/>   按 HID 的 VID/PID
    ///   * <see cref="NamePrefix"/>                          按 PnP / product 名前缀
    ///   两者都给时按「AND」处理 —— 例如「雷蛇厂商 + 名字含 BlackShark」。
    /// </summary>
    public sealed class ProtocolChannel
    {
        public ProtocolKind Kind { get; init; }

        /// <summary>HID 厂商号；0 表示不限</summary>
        public int VendorId { get; init; }

        /// <summary>HID 产品号；0 表示不限（同厂商的其它型号也能命中）</summary>
        public int ProductId { get; init; }

        /// <summary>名字前缀/包含（用于蓝牙通道、或同 VID 多型号区分）；空表示不限</summary>
        public string NamePrefix { get; init; } = "";

        /// <summary>
        /// 蓝牙通道是否只扫经典蓝牙（BTHENUM）的音频服务节点。
        ///
        /// 耳机必须为 true：它的电量挂在经典蓝牙的音频服务子节点上，
        /// 而 BTHLE 树下也可能有同名设备（比如蓝牙鼠标）。
        /// 键盘为 false：它在 BTHLE 的 DEV_ 节点上。
        /// </summary>
        public bool ClassicBluetoothOnly { get; init; }

        /// <summary>
        /// 走这条通道时卡片上显示的型号名；空表示由 Reader 自己推断。
        ///
        /// 蓝牙通道必须给：PnP 友好名带连接方式后缀（"MAX102-BT1"、
        /// "… BT Hands-Free AG"），直接显示会让用户以为是另一个型号。
        /// </summary>
        public string Model { get; init; } = "";

        /// <summary>给人看的通道说明，用于 --probe 与排查</summary>
        public string Describe()
        {
            string target = VendorId != 0
                ? $"{VendorId:X4}:{(ProductId != 0 ? ProductId.ToString("X4") : "----")}"
                : "任意 VID";
            if (NamePrefix.Length > 0) target += " 名含 \"" + NamePrefix + "\"";
            if (ClassicBluetoothOnly) target += " [经典蓝牙]";
            return Kind + " · " + target;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 一台被支持的设备型号。
    ///
    /// 加新型号：在 <see cref="DeviceCatalog.BuiltIn"/> 里加一条即可。
    /// 如果只是「同一协议的另一个型号」（比如另一款罗技鼠标），
    /// 连 Reader 都不用改 —— VID 相同就会被自动认出来。
    /// </summary>
    public sealed class DeviceDefinition
    {
        /// <summary>
        /// 稳定的标识，用做历史文件名后缀与设置里的开关键。
        /// **一旦发布就不要改** —— 改了等于丢掉用户的历史与开关状态。
        /// </summary>
        public string Key { get; init; } = "";

        /// <summary>卡片与托盘提示上的标签，如「鼠标」「耳机」</summary>
        public string Label { get; init; } = "";

        /// <summary>卡片上显示的型号名（可被设备自报的名字覆盖）</summary>
        public string Model { get; init; } = "";

        /// <summary>
        /// 协议链：按顺序尝试，第一条读到就停。
        /// 空表示这台设备没有可用通道（不应出现在内置表里）。
        /// </summary>
        public IReadOnlyList<ProtocolChannel> Channels { get; init; } =
            Array.Empty<ProtocolChannel>();

        /// <summary>是否默认开启监控。用户关掉后以设置为准。</summary>
        public bool EnabledByDefault { get; init; } = true;

        public override string ToString()
        {
            return Label + " (" + Model + " / " + Key + ")";
        }
    }

    /// <summary>
    /// 内置设备表 + 用户自定义设备。
    ///
    /// 用户自定义的来源优先级：
    ///   1. 程序目录下 devices.json（便携）
    ///   2. %LOCALAPPDATA%\外设电量\devices.json
    /// 两者同名时以程序目录那份为准（与设置/历史一致）。
    ///
    /// 为什么允许用户加：逆向新设备需要抓包、写解析、跑测试，门槛很高；
    /// 但「另一个型号用同一个协议」这种情形很常见，用户填个 VID/PID 就能用。
    /// </summary>
    public static class DeviceCatalog
    {
        /// <summary>
        /// 内置设备。顺序即默认托盘环顺序（外 → 内）。
        ///
        /// ⚠️ 这个列表是**唯一**的设备定义处。Program 只负责把
        /// DeviceDefinition 转成运行时的槽位对象，不许在这里之外再写死设备。
        /// </summary>
        public static IReadOnlyList<DeviceDefinition> BuiltIn { get; } =
            new List<DeviceDefinition>
            {
                new()
                {
                    Key = "logitech",
                    Label = "鼠标",
                    Model = "罗技 LIGHTSPEED",
                    Channels = new List<ProtocolChannel>
                    {
                        new()
                        {
                            Kind = ProtocolKind.LogitechHidpp,
                            VendorId = Protocols.LogiVendor,
                        },
                    },
                },
                new()
                {
                    Key = "razer",
                    Label = "耳机",
                    Model = "雷蛇 BlackShark",
                    Channels = new List<ProtocolChannel>
                    {
                        // 首选 2.4G：一次能拿到电量 + 充电 + 连接状态
                        new()
                        {
                            Kind = ProtocolKind.RazerPaPi,
                            VendorId = Protocols.RazerVendor,
                        },
                        // 蓝牙模式没有厂商 HID 通道，只能读系统属性
                        new()
                        {
                            Kind = ProtocolKind.BluetoothProperty,
                            NamePrefix = "Razer",
                            ClassicBluetoothOnly = true,
                            Model = "雷蛇 BlackShark",
                        },
                    },
                },
                new()
                {
                    Key = "max102",
                    Label = "键盘",
                    Model = "维咖 MAX102",
                    Channels = new List<ProtocolChannel>
                    {
                        new()
                        {
                            Kind = ProtocolKind.ViaQmk,
                            VendorId = Protocols.ViaVendorMax102,
                            ProductId = Protocols.ViaProductMax102,
                        },
                        new()
                        {
                            Kind = ProtocolKind.BluetoothProperty,
                            NamePrefix = "MAX102",
                            ClassicBluetoothOnly = false,
                            Model = "维咖 MAX102",
                        },
                    },
                },
            };

        /// <summary>按 Key 找内置定义；找不到返回 null</summary>
        public static DeviceDefinition? FindBuiltIn(string key)
        {
            foreach (var item in BuiltIn)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// 解析用户自定义设备（从 JSON 反序列化后的中间形态）。
        ///
        /// 校验原则：**宁可不加，也不要加一条读不出数据的设备** ——
        /// 一条永远失败的设备会在托盘上留一个永久空环，
        /// 用户第一反应是「这工具坏了」而不是「我配错了」。
        ///
        /// 所以这里会剔掉：
        ///   * Key / Label 为空的
        ///   * 没有任何通道的
        ///   * Key 与内置设备重名的（会覆盖历史与开关，风险太大）
        /// </summary>
        public static List<DeviceDefinition> ParseUserDevices(
            IEnumerable<DeviceDefinitionJson>? items,
            out List<string> rejected)
        {
            var result = new List<DeviceDefinition>();
            rejected = new List<string>();

            if (items == null) return result;

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null) continue;

                string key = (item.Key ?? "").Trim();
                string label = (item.Label ?? "").Trim();

                if (key.Length == 0 || label.Length == 0)
                {
                    rejected.Add("缺少 key 或 label");
                    continue;
                }
                if (FindBuiltIn(key) != null || !seenKeys.Add(key))
                {
                    rejected.Add(key + "：key 与内置设备或其它自定义项重复");
                    continue;
                }

                var channels = new List<ProtocolChannel>();
                foreach (var raw in item.Channels ?? new List<ProtocolChannelJson>())
                {
                    if (raw == null) continue;
                    if (!TryParseKind(raw.Kind, out ProtocolKind kind))
                    {
                        rejected.Add(key + "：不认识的协议 \"" + raw.Kind + "\"");
                        continue;
                    }
                    channels.Add(new ProtocolChannel
                    {
                        Kind = kind,
                        VendorId = raw.VendorId,
                        ProductId = raw.ProductId,
                        NamePrefix = (raw.NamePrefix ?? "").Trim(),
                        ClassicBluetoothOnly = raw.ClassicBluetoothOnly,
                        Model = (raw.Model ?? "").Trim(),
                    });
                }

                if (channels.Count == 0)
                {
                    rejected.Add(key + "：没有任何可用通道");
                    continue;
                }

                result.Add(new DeviceDefinition
                {
                    Key = key,
                    Label = label,
                    Model = (item.Model ?? label).Trim(),
                    Channels = channels,
                    EnabledByDefault = item.EnabledByDefault,
                });
            }
            return result;
        }

        /// <summary>
        /// 协议名 → 枚举。允许两种写法：
        ///   * 宽松写法："logitech" / "hidpp" / "razer" / "papi" / "via" / "bluetooth"
        ///   * 严格写法："LogitechHidpp" / "RazerPaPi" / "ViaQmk" / "BluetoothProperty"
        /// 宽松写法是给用户手填的；严格写法是序列化时的输出。
        /// </summary>
        public static bool TryParseKind(string? text, out ProtocolKind kind)
        {
            kind = ProtocolKind.BluetoothProperty;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text!.Trim().ToLowerInvariant())
            {
                case "logitech": case "logitechhidpp": case "hidpp": case "logi":
                    kind = ProtocolKind.LogitechHidpp; return true;
                case "razer": case "razerpapi": case "papi":
                    kind = ProtocolKind.RazerPaPi; return true;
                case "via": case "viaqmk": case "qmk": case "viaqmk ":
                    kind = ProtocolKind.ViaQmk; return true;
                case "bluetooth": case "bluetoothproperty": case "bt":
                    kind = ProtocolKind.BluetoothProperty; return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>devices.json 里一台设备的原始形态（字段名与 JSON 一致）</summary>
    public sealed class DeviceDefinitionJson
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Model { get; set; }
        public List<ProtocolChannelJson>? Channels { get; set; }
        public bool EnabledByDefault { get; set; } = true;
    }

    /// <summary>devices.json 里一条通道的原始形态</summary>
    public sealed class ProtocolChannelJson
    {
        public string? Kind { get; set; }
        public string? Model { get; set; }
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string? NamePrefix { get; set; }
        public bool ClassicBluetoothOnly { get; set; }
    }
}
