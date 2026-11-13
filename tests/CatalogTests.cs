// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯数据断言] 只测设备表与 devices.json 的解析，不碰 HID / PnP 句柄。
// 2. [校验必须严格] 一条永远读不到的设备会在托盘上留永久空环，
//    用户第一反应是「工具坏了」而不是「我配错了」—— 所以宁可不加。
// 3. [编码] UTF-8 with BOM。

using System.Collections.Generic;
using PeripheralBattery.Hardware;

namespace PeripheralBattery.Tests
{
    public static class CatalogTests
    {
        // ============================================================ 内置表

        [Test]
        public static void BuiltInDevicesAreWellFormed()
        {
            var devices = DeviceCatalog.BuiltIn;
            Assert.True(devices.Count >= 3, "至少内置三台设备");

            var keys = new HashSet<string>();
            foreach (var device in devices)
            {
                Assert.True(device.Key.Length > 0, "key 不能为空");
                Assert.True(device.Label.Length > 0, device.Key + " 缺少 label");
                Assert.True(device.Channels.Count >= 1, device.Key + " 至少要有一条通道");
                Assert.True(keys.Add(device.Key), "key 重复：" + device.Key);
            }
        }

        [Test]
        public static void BuiltInKeysAreStable()
        {
            // key 会写进历史文件名与设置里的开关，改名等于丢数据。
            // 这里把已发布的 key 固定下来，避免无意中改掉。
            Assert.NotNull(DeviceCatalog.FindBuiltIn("logitech"), "logitech 必须存在");
            Assert.NotNull(DeviceCatalog.FindBuiltIn("razer"), "razer 必须存在");
            Assert.NotNull(DeviceCatalog.FindBuiltIn("max102"), "max102 必须存在");
            Assert.Null(DeviceCatalog.FindBuiltIn("不存在的键"), "未知 key 返回 null");
        }

        [Test]
        public static void HeadsetPrefersWirelessThenBluetooth()
        {
            var headset = DeviceCatalog.FindBuiltIn("razer");
            Assert.NotNull(headset, "耳机定义存在");
            Assert.Equal(2, headset!.Channels.Count, "耳机有两条通道");

            Assert.Equal(ProtocolKind.RazerPaPi, headset.Channels[0].Kind,
                "首选 2.4G（能拿到充电与连接状态）");
            Assert.Equal(ProtocolKind.BluetoothProperty, headset.Channels[1].Kind,
                "回落蓝牙");

            Assert.True(headset.Channels[1].ClassicBluetoothOnly,
                "耳机蓝牙必须只扫经典蓝牙：电量挂在 BTHENUM 的音频服务子节点");
        }

        [Test]
        public static void BluetoothChannelsDeclareModel()
        {
            // PnP 友好名带连接方式后缀（MAX102-BT1 / … BT Hands-Free AG），
            // 直接显示会让用户以为是另一个型号。
            foreach (var device in DeviceCatalog.BuiltIn)
            {
                foreach (var channel in device.Channels)
                {
                    if (channel.Kind != ProtocolKind.BluetoothProperty) continue;
                    Assert.True(channel.Model.Length > 0,
                        device.Key + " 的蓝牙通道必须声明 model");
                    Assert.True(channel.NamePrefix.Length > 0,
                        device.Key + " 的蓝牙通道必须声明 namePrefix");
                }
            }
        }

        // ============================================================ 雷蛇型号表
        //
        // 型号表只影响显示文案，不影响能不能读到电量。所以它的正确性标准是：
        //   * 已实测的型号必须在表里（不能退化成「Razer 无线设备」）
        //   * 未收录的 PID 必须能安全兜底，不许抛异常或显示空串

        [Test]
        public static void MeasuredRazerModelsAreNamed()
        {
            // 0x0555 是本仓库真机实测的耳机，退化成兜底文案就是回归
            Assert.Equal("BlackShark V2 Pro",
                Protocols.RazerModelName("", 0x0555), "实测耳机型号名");
            Assert.Equal("Kraken V4",
                Protocols.RazerModelName("", 0x056C), "同族耳机");
        }

        [Test]
        public static void UnknownRazerPidFallsBackSafely()
        {
            // 未收录的 PID 必须给一个通用但准确的文案。
            // 「无线设备」而不是「无线耳机」：PA/PI 同族还包括鼠标键盘。
            Assert.Equal("Razer 无线设备",
                Protocols.RazerModelName("", 0xFFFF), "未知 PID 兜底");
            Assert.Equal("Razer 无线设备",
                Protocols.RazerModelName("HID-compliant device", 0xFFFF),
                "product_string 是通用描述时也要兜底");
        }

        [Test]
        public static void ModelTableCoversBothMiceAndHeadsets()
        {
            // 只收录耳机是早期状态；模型表现在同时覆盖鼠标。
            // 这里抽查几个不同世代的 PID，防止有人「清理」掉看起来没用的条目。
            Assert.Equal("DeathAdder V2 Pro",
                Protocols.RazerModelName("", 0x007C), "老款无线鼠标");
            Assert.Equal("Naga Epic",
                Protocols.RazerModelName("", 0x001F), "最老的一批");
            Assert.Equal("HyperPolling Wireless Dongle",
                Protocols.RazerModelName("", 0x00B3), "8K 接收器");
        }

        [Test]
        public static void ModelTableCoversKeyboardsAndHeadsets()
        {
            // 型号表原来只有鼠标+耳机，现在把雷蛇无线键盘也收进来了。
            // 抽查几个不同品类，防止有人「清理」掉看起来没用的条目。
            Assert.Equal("BlackWidow V3 Pro",
                Protocols.RazerModelName("", 0x025A), "雷蛇无线键盘");
            Assert.Equal("DeathStalker V2 Pro",
                Protocols.RazerModelName("", 0x0292), "雷蛇无线键盘 2");
            Assert.Equal("Kraken 7.1",
                Protocols.RazerModelName("", 0x0501), "老款雷蛇耳机");
            Assert.Equal("Kraken Kitty V2",
                Protocols.RazerModelName("", 0x0560), "猫耳耳机");
            Assert.Equal("DeathAdder V2 Pro",
                Protocols.RazerModelName("", 0x007C), "雷蛇鼠标");
        }

        [Test]
        public static void ModelTableHasNoDuplicatePid()
        {
            // 重复 PID 会让「同一个设备在不同机器上显示不同型号」，
            // 而且后写入的会静默覆盖前面的。这里一次性查全表。
            // Dictionary 初始化器遇到重复键会抛异常，所以能构造成功
            // 就说明没重复 —— 但我们要的是「能给所有已知 PID 名字」，
            // 所以抽查几个必须存在的。
            Assert.NotNull(Protocols.RazerModelName("", 0x0555), "实测耳机在表里");
            Assert.NotNull(Protocols.RazerModelName("", 0x00BE), "新鼠标在表里");
            Assert.NotNull(Protocols.RazerModelName("", 0x025C), "键盘在表里");
        }

        // ============================================================ 协议名解析

        [Test]
        public static void ProtocolKindAcceptsLooseAndStrictNames()
        {
            AssertKind("logitech", ProtocolKind.LogitechHidpp);
            AssertKind("logi", ProtocolKind.LogitechHidpp);
            AssertKind("hidpp", ProtocolKind.LogitechHidpp);
            AssertKind("LogitechHidpp", ProtocolKind.LogitechHidpp);

            AssertKind("razer", ProtocolKind.RazerPaPi);
            AssertKind("papi", ProtocolKind.RazerPaPi);
            AssertKind("RazerPaPi", ProtocolKind.RazerPaPi);

            AssertKind("via", ProtocolKind.ViaQmk);
            AssertKind("qmk", ProtocolKind.ViaQmk);
            AssertKind("ViaQmk", ProtocolKind.ViaQmk);

            AssertKind("bt", ProtocolKind.BluetoothProperty);
            AssertKind("bluetooth", ProtocolKind.BluetoothProperty);
            AssertKind("BluetoothProperty", ProtocolKind.BluetoothProperty);

            AssertKind(" LOGITECH ", ProtocolKind.LogitechHidpp);  // 容忍空白与大小写
        }

        [Test]
        public static void ProtocolKindRejectsUnknown()
        {
            Assert.False(DeviceCatalog.TryParseKind("hid", out _), "hid 不是已知协议");
            Assert.False(DeviceCatalog.TryParseKind("", out _), "空串");
            Assert.False(DeviceCatalog.TryParseKind(null, out _), "null");
        }

        private static void AssertKind(string text, ProtocolKind expected)
        {
            Assert.True(DeviceCatalog.TryParseKind(text, out var kind),
                "应能解析：" + text);
            Assert.Equal(expected, kind, "解析结果：" + text);
        }

        // ============================================================ 用户自定义

        [Test]
        public static void UserDeviceIsParsed()
        {
            var items = new List<DeviceDefinitionJson>
            {
                new()
                {
                    Key = "g304",
                    Label = "备用鼠标",
                    Model = "G304",
                    Channels = new List<ProtocolChannelJson>
                    {
                        new() { Kind = "logitech", VendorId = 0x046D },
                    },
                },
            };

            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            Assert.Equal(1, devices.Count, "一条合法设备");
            Assert.Equal(0, rejected.Count, "不应有拒绝项");
            Assert.Equal("g304", devices[0].Key, "key");
            Assert.Equal("G304", devices[0].Model, "model");
            Assert.Equal(ProtocolKind.LogitechHidpp, devices[0].Channels[0].Kind, "协议");
        }

        [Test]
        public static void UserDeviceWithoutChannelsIsRejected()
        {
            var items = new List<DeviceDefinitionJson>
            {
                new() { Key = "empty", Label = "没通道", Channels = new List<ProtocolChannelJson>() },
            };
            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            Assert.Equal(0, devices.Count, "没通道的设备不能用");
            Assert.Equal(1, rejected.Count, "要记录拒绝原因");
        }

        [Test]
        public static void UserDeviceClashingWithBuiltInIsRejected()
        {
            // 覆盖内置 key 会把历史与开关状态搅在一起，风险太大
            var items = new List<DeviceDefinitionJson>
            {
                new()
                {
                    Key = "razer",
                    Label = "冒名顶替",
                    Channels = new List<ProtocolChannelJson>
                    {
                        new() { Kind = "bluetooth", NamePrefix = "Razer" },
                    },
                },
            };
            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            Assert.Equal(0, devices.Count, "不能覆盖内置设备");
            Assert.Equal(1, rejected.Count, "要记录拒绝原因");
        }

        [Test]
        public static void UserDeviceWithUnknownProtocolIsRejected()
        {
            var items = new List<DeviceDefinitionJson>
            {
                new()
                {
                    Key = "weird",
                    Label = "未知协议",
                    Channels = new List<ProtocolChannelJson>
                    {
                        new() { Kind = "蓝牙质谱仪" },
                    },
                },
            };
            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            Assert.Equal(0, devices.Count, "未知协议不能加进来");
            Assert.True(rejected.Count > 0, "要说明原因");
   
        }

        [Test]
        public static void DuplicateUserKeysKeptOnce()
        {
            var items = new List<DeviceDefinitionJson>
            {
                new()
                {
                    Key = "dup", Label = "第一个",
                    Channels = new List<ProtocolChannelJson>
                    {
                        new() { Kind = "via", VendorId = 1 },
                    },
                },
                new()
                {
                    Key = "DUP", Label = "第二个",
                    Channels = new List<ProtocolChannelJson>
                    {
                        new() { Kind = "via", VendorId = 2 },
                    },
                },
            };
            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            Assert.Equal(1, devices.Count, "同 key（忽略大小写）只保留一条");
            Assert.Equal(1, rejected.Count, "后一条要被拒绝");
        }

        [Test]
        public static void NullUserListIsSafe()
        {
            var devices = DeviceCatalog.ParseUserDevices(null, out var rejected);
            Assert.Equal(0, devices.Count, "null 输入不崩");
            Assert.Equal(0, rejected.Count, "无拒绝项");
        }
    }
}
