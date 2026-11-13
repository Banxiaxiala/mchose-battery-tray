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
        /// <summary>ATK / 怒喵 64 字节私有报文</summary>
        AtkVendor = 4,
        /// <summary>飞智 GPA6 私有帧（0xFFA0）</summary>
        FlydigiVendor = 5,
        /// <summary>漫步者 EC 私有帧（XOR 掩码 + 累加和）</summary>
        EdifierVendor = 6,
        /// <summary>迈从鼠标 Feature Report 控制通道（数据异或 0xFF）</summary>
        MchoseVendor = 7,
        /// <summary>盖世小鸡 65 字节报告（0xFF7A）</summary>
        GameSirVendor = 8,
        /// <summary>华硕 ROG 64/65 字节报告（CC 12 帧头）</summary>
        AsusVendor = 9,
        /// <summary>HyperX 62 字节报文（report id 0x66）</summary>
        HyperXVendor = 10,
        /// <summary>宏碁 VK M3 33 字节报文（命令 0x10）</summary>
        AcerVendor = 11,
        /// <summary>迈从键盘 0x13 帧（累加和校验）</summary>
        MchoseKeyboardVendor = 12,
        /// <summary>NuPhy 65 字节帧（00 55 帧头）</summary>
        NuphyVendor = 13,
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
        /// VIA 族：读电量的命令号。0 = 用该协议的默认值。
        ///
        /// 维咖 MAX102 是补丁命令 0x14（载荷偏移 4）；
        /// 高斯 GS3108T-PRO 是 0x20（载荷偏移 3）。
        /// 两者端点相同、报文长度相同，只有这两处不同。
        /// </summary>
        public byte BatteryCommand { get; init; }

        /// <summary>VIA 族：电量字段在 32 字节载荷里的偏移；0 表示用默认</summary>
        public int BatteryOffset { get; init; }

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
                // 以下四台都是「移植实现、未真机验证」，一律默认关闭 ——
                // 不能让没验证过的设备占用托盘环，否则用户会以为工具坏了。
                // 装了的用户在设置面板里打开。
                GanssGs3108(),
                AtkV75X(),
                FlydigiDevice(),
                EdifierHaloSpace(),
                MchoseMouse(),
                GameSirNova2(),
                AsusRogPelta(),
                HyperXCloud3(),
                AcerVkM3(),
                MchoseKeyboard(),
                NuphyKeyboard(),
            };

        /// <summary>
        /// ATK V75X 机械键盘（ATK / 怒喵同族）。
        ///
        /// 协议移植自 FerrisPulse 的 AtkDeviceHelper（该型号做过抓包）。
        /// 本仓库**未在真机上验证过**，所以等级是 🟡。
        /// </summary>
        private static DeviceDefinition AtkV75X()
        {
            return new DeviceDefinition
            {
                Key = "atk-v75x",
                Label = "ATK 键盘",
                Model = "ATK V75X",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.AtkVendor,
                        VendorId = AtkVendorProtocol.VendorId,
                        Model = "ATK V75X",
                    },
                    // ATK 键盘也是三模，2.4G 不在时走蓝牙
                    new()
                    {
                        Kind = ProtocolKind.BluetoothProperty,
                        NamePrefix = "ATK",
                        ClassicBluetoothOnly = false,
                        Model = "ATK V75X",
                    },
                },
            };
        }

        /// <summary>
        /// 飞智（Flydigi）手柄 / 键盘。
        ///
        /// 协议移植自 FerrisPulse 的 FlydigiDeviceHelper。
        /// 电量是**分档**的（GPA6 私有帧），不是精确百分比 ——
        /// 换算规则见 FlydigiProtocol.TryParseInfo。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition FlydigiDevice()
        {
            return new DeviceDefinition
            {
                Key = "flydigi",
                Label = "飞智手柄",
                Model = "飞智 GPA6",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.FlydigiVendor,
                        VendorId = FlydigiProtocol.VendorId,
                        Model = "飞智 GPA6",
                    },
                },
            };
        }

        /// <summary>
        /// 漫步者 花再 Halo Space 头戴耳机。
        ///
        /// 协议移植自 FerrisPulse 的 EdifierDeviceHelper：
        /// EC 帧 + 载荷 XOR 0xA5 + 累加和校验。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition EdifierHaloSpace()
        {
            return new DeviceDefinition
            {
                Key = "edifier-halo-space",
                Label = "漫步者耳机",
                Model = "花再 Halo Space",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.EdifierVendor,
                        VendorId = EdifierProtocol.VendorId,
                        Model = "花再 Halo Space",
                    },
                },
            };
        }

        /// <summary>
        /// 迈从 G75 PRO / G87 V2 / Z75S 键盘。
        ///
        /// 与迈从**鼠标**是两套完全不同的协议 —— 鼠标走 Feature Report，
        /// 键盘走中断端点 + 累加和校验。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition MchoseKeyboard()
        {
            return new DeviceDefinition
            {
                Key = "mchose-keyboard",
                Label = "迈从键盘",
                Model = "迈从 G75 PRO",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.MchoseKeyboardVendor,
                        VendorId = MchoseMouseProtocol.VendorId,
                        Model = "迈从 G75 PRO",
                    },
                },
            };
        }

        /// <summary>
        /// NuPhy 三模键盘（WH80 / Air75 V2 / Halo75 V2 / Air60 V2）。
        ///
        /// 读电量要两步：先 0x04 读连接模式，再 0x05 按模式算偏移读电量。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition NuphyKeyboard()
        {
            return new DeviceDefinition
            {
                Key = "nuphy",
                Label = "NuPhy 键盘",
                Model = "NuPhy Air75 V2",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.NuphyVendor,
                        NamePrefix = "NuPhy",
                        Model = "NuPhy Air75 V2",
                    },
                },
            };
        }

        /// <summary>
        /// 宏碁 VK M3 / OMR331 系列无线鼠标。
        ///
        /// 协议移植自 FerrisPulse AcerDeviceHelper。
        /// 注意「充电时电量填 100 当哨兵」这个坑，见 AcerProtocol。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition AcerVkM3()
        {
            return new DeviceDefinition
            {
                Key = "acer-vkm3",
                Label = "宏碁鼠标",
                Model = "宏碁 VK M3",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.AcerVendor,
                        VendorId = AcerProtocol.VendorIdWireless,
                        Model = "宏碁 VK M3",
                    },
                },
            };
        }

        /// <summary>
        /// HyperX 飓风3 无线耳机。
        ///
        /// 协议移植自 FerrisPulse HyperXDeviceHelper。
        /// 明确排除飓风3S（0x06BE）—— 它的报文协议不同。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition HyperXCloud3()
        {
            return new DeviceDefinition
            {
                Key = "hyperx-cloud3",
                Label = "HyperX 耳机",
                Model = "HyperX 飓风3",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.HyperXVendor,
                        VendorId = HyperXProtocol.VendorId,
                        Model = "HyperX 飓风3",
                    },
                },
            };
        }

        /// <summary>
        /// 盖世小鸡 启明星2 手柄。
        ///
        /// 协议移植自 FerrisPulse GameSirDeviceHelper。
        /// 注意它的「充电中但无百分比」哨兵值（255），见 GameSirProtocol。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition GameSirNova2()
        {
            return new DeviceDefinition
            {
                Key = "gamesir-nova2",
                Label = "小鸡手柄",
                Model = "盖世小鸡 启明星2",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.GameSirVendor,
                        VendorId = GameSirProtocol.VendorId,
                        Model = "盖世小鸡 启明星2",
                    },
                },
            };
        }

        /// <summary>
        /// 华硕 ROG PELTA 无线耳机。
        ///
        /// 协议移植自 FerrisPulse AsusDeviceHelper（PR #39）。
        /// 电量与充电是两个独立命令，要发两次。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition AsusRogPelta()
        {
            return new DeviceDefinition
            {
                Key = "asus-rog-pelta",
                Label = "ROG 耳机",
                Model = "ROG PELTA",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.AsusVendor,
                        VendorId = AsusProtocol.VendorId,
                        Model = "ROG PELTA",
                    },
                },
            };
        }

        /// <summary>
        /// 迈从 A5 V2 Ultra 8K 电竞鼠标（含 G75 PRO / G87 V2 同族）。
        ///
        /// 协议移植自 FerrisPulse 的 MchoseMouseHelper。
        /// 特点是走 Feature Report 而不是中断端点，见 MchoseMouseProtocol。
        /// 本仓库未真机验证，🟡。
        /// </summary>
        private static DeviceDefinition MchoseMouse()
        {
            return new DeviceDefinition
            {
                Key = "mchose-mouse",
                Label = "迈从鼠标",
                Model = "迈从 A5 V2 Ultra",
                EnabledByDefault = false,
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.MchoseVendor,
                        VendorId = MchoseMouseProtocol.VendorId,
                        Model = "迈从 A5 V2 Ultra",
                    },
                },
            };
        }

        /// <summary>
        /// 高斯 GS3108T-PRO 三模键盘。
        ///
        /// 与维咖 MAX102 同一个 VIA 端点（0xFF60/0x61、33 字节），
        /// 但用的是原厂命令 0x20（不是维咖的补丁命令 0x14），
        /// 电量在载荷偏移 3。
        ///
        /// 协议依据：FerrisPulse 的 GanssDeviceHelper —— 那里对
        /// DeviceDriver.exe 做了静态分析并实机抓包验证过帧格式
        /// （端点 0xFF60/0x61，请求 00020100…0021，应答电量在载荷 [3]）。
        /// 本仓库**未在真机上验证过这台键盘**，所以等级是 🟡。
        /// </summary>
        private static DeviceDefinition GanssGs3108()
        {
            return new DeviceDefinition
            {
                Key = "ganss-gs3108t",
                // Label 是卡片/提示上的标题，必须与维咖区分开 ——
                // 两台都叫「键盘」时，用户看到「键盘 读取失败」根本不知道是哪台。
                Label = "键盘2",
                Model = "高斯 GS3108T-PRO",
                EnabledByDefault = false,   // 不是所有用户都有这台，默认不占环
                Channels = new List<ProtocolChannel>
                {
                    new()
                    {
                        Kind = ProtocolKind.ViaQmk,
                        // 注意 VID 是 0x05AC（Apple 的厂商号），不是高斯自己的。
                        // 这是键盘固件自报的值 —— 抓包时就是这么枚举出来的，
                        // 不能「纠正」成看起来更合理的厂商号，否则枚举不到。
                        VendorId = 0x05AC,
                        ProductId = 0x024F,
                        BatteryCommand = Protocols.ViaCmdGetBatteryGeneric,
                        BatteryOffset = Protocols.ViaGenericBatteryOffset,
                        Model = "高斯 GS3108T-PRO",
                    },
                    new()
                    {
                        Kind = ProtocolKind.BluetoothProperty,
                        NamePrefix = "GS3108",
                        ClassicBluetoothOnly = false,
                        Model = "高斯 GS3108T-PRO",
                    },
                },
            };
        }

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
                case "atk": case "atkvendor":
                    kind = ProtocolKind.AtkVendor; return true;
                case "flydigi": case "flydigivendor":
                    kind = ProtocolKind.FlydigiVendor; return true;
                case "edifier": case "edifiervendor":
                    kind = ProtocolKind.EdifierVendor; return true;
                case "mchose": case "mchosevendor":
                    kind = ProtocolKind.MchoseVendor; return true;
                case "gamesir": case "gamesirvendor":
                    kind = ProtocolKind.GameSirVendor; return true;
                case "asus": case "rog": case "asusvendor":
                    kind = ProtocolKind.AsusVendor; return true;
                case "hyperx": case "hyperxvendor":
                    kind = ProtocolKind.HyperXVendor; return true;
                case "acer": case "acervendor":
                    kind = ProtocolKind.AcerVendor; return true;
                case "mchosekeyboard": case "mchose-kb":
                    kind = ProtocolKind.MchoseKeyboardVendor; return true;
                case "nuphy": case "nuphyvendor":
                    kind = ProtocolKind.NuphyVendor; return true;
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
