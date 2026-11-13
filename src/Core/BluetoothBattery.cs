// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [读系统属性，不连设备] Windows 自己已经在维护蓝牙设备电量
//    （「设置 → 蓝牙」里显示的那个百分比）。读 PnP 属性是毫秒级、
//    不需要连设备、不会跟系统抢连接。走 GATT 需要 BLE 栈（WinRT），
//    而且已配对的 BLE HID 会被系统独占，去连它会失败或影响键盘输入。
// 2. [两个枚举器都要扫] 电量属性同时出现在两棵树里，缺一棵就有一类设备
//    永远读不到 —— 实测：
//      * BTHLE   （低功耗）键盘 MAX102-BT1，属性挂在 DEV 节点上
//      * BTHENUM （经典蓝牙）耳机 BlackShark V2 Pro BT，属性挂在
//        Hands-Free AG 服务节点上，**DEV 节点本身没有电量**
//    只扫 BTHLE 时耳机在蓝牙模式下永远显示「没找到」，这就是本次补丁的起因。
// 3. [按设备名匹配，不按 MAC 写死] 蓝牙设备的 MAC 会随重配对变化，
//    只允许按 FriendlyName 前缀匹配（MAX102-BT1/BT2/BT3、Razer …）。
// 4. [在线状态看「同一 MAC 的任意节点」] BTHENUM 的服务子节点
//    DEVPKEY_Device_IsConnected 恒为 False（它只是链路里的一段服务），
//    真正表示链路在线的父节点 BTHENUM\DEV_<MAC> 没有电量属性。
//    所以必须按 MAC 聚合：任一节点在线即视为在线，否则会在耳机明明
//    正在放歌时显示「蓝牙离线」。
// 5. [区分“没读到”和“没值时”] 属性不存在 → NoValue；属性存在但为 0
//    也是合法电量（真的没电了），不能当成“读不到”。
// 6. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Text;
using PeripheralBattery.Hardware;

namespace PeripheralBattery.Core
{
    /// <summary>一个蓝牙节点上的电量快照</summary>
    public sealed class BluetoothBatteryInfo
    {
        /// <summary>Windows 记录的蓝牙地址，例如 c00672d99623</summary>
        public string Address { get; init; } = "";
        /// <summary>PnP 节点友好名，例如 MAX102-BT1</summary>
        public string FriendlyName { get; init; } = "";
        /// <summary>null = 属性不存在（系统还没读到过）</summary>
        public int? Percent { get; init; }
        /// <summary>系统记录该电量的时刻（UTC）；0 = 不知道</summary>
        public DateTime UpdatedAt { get; init; }
        /// <summary>
        /// 链路是否在线。取自**同一 MAC 的任意节点**（见文件头第 4 条），
        /// 不是这个服务节点自己的 IsConnected。
        /// </summary>
        public bool Connected { get; init; }

        public override string ToString()
        {
            return $"{FriendlyName} [{Address}] "
                 + (Percent.HasValue ? Percent.Value + "%" : "无电量属性")
                 + (Connected ? " 在线" : " 离线");
        }
    }

    /// <summary>
    /// 经 SetupAPI 读取蓝牙设备节点上的电量属性。
    ///
    /// 为什么不用 BluetoothFindFirstDevice / bthprops：那是设备枚举，
    /// 不带电量遥测，而且拿不到 BTHLE\DEV_xxx 这种 PnP 节点。
    /// </summary>
    public static class BluetoothBattery
    {
        /// <summary>
        /// 扫哪几棵枚举树。顺序会体现在「同一台设备多个节点」的取舍上，
        /// 但 QueryOne 另有按时间戳取新的规则，这里顺序只影响可读性。
        /// </summary>
        private static readonly string[] Enumerators = { "BTHLE", "BTHENUM" };

        /// <summary>
        /// 枚举所有带电量属性的蓝牙节点。
        /// namePrefix 为空表示不过滤；给了前缀就只回名字含它的节点。
        ///
        /// classicAudioOnly=true 时只扫经典蓝牙（BTHENUM）的音频服务节点。
        /// 耳机必须用这个模式：BLE 树下也可能有同名雷蛇设备（蓝牙鼠标），
        /// 而且只有 BTHENUM 的音频服务节点才是耳机的电量来源。
        /// </summary>
        public static List<BluetoothBatteryInfo> Query(string? namePrefix = null,
            bool classicAudioOnly = false)
        {
            var raw = new List<RawNode>();
            // MAC → 是否在线。两个枚举器都要参与聚合，见文件头第 4 条。
            var online = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (string enumerator in Enumerators)
            {
                // BTHENUM 里混着蓝牙鼠标/串口/传真等一大堆设备，
                // 只有音频类服务节点才属于「耳机」。BTHLE 不用过滤：
                // 它本身就是按需暴露的服务集合，靠名字前缀足够。
                bool audioOnly = enumerator == "BTHENUM";
                if (classicAudioOnly && enumerator != "BTHENUM") continue;
                Collect(enumerator, namePrefix, audioOnly, raw, online);
            }

            var results = new List<BluetoothBatteryInfo>(raw.Count);
            foreach (var node in raw)
            {
                results.Add(new BluetoothBatteryInfo
                {
                    Address = node.Address,
                    FriendlyName = node.FriendlyName,
                    Percent = node.Percent,
                    UpdatedAt = node.UpdatedAt,
                    Connected = node.Address.Length > 0
                        && online.TryGetValue(node.Address, out bool up) && up,
                });
            }
            return results;
        }

        /// <summary>中间结果：先收齐，再按 MAC 统一补在线状态</summary>
        private sealed class RawNode
        {
            public string Address = "";
            public string FriendlyName = "";
            public int? Percent;
            public DateTime UpdatedAt;
        }

        private static void Collect(string enumerator, string? namePrefix, bool audioOnly,
            List<RawNode> nodes, Dictionary<string, bool> online)
        {
            IntPtr devInfo = Win32.SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero,
                Win32.DIGCF_PRESENT | Win32.DIGCF_ALLCLASSES);
            if (devInfo == IntPtr.Zero || devInfo == Win32.InvalidHandle) return;

            try
            {
                var data = new Win32.SP_DEVINFO_DATA
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal
                        .SizeOf<Win32.SP_DEVINFO_DATA>(),
                };

                for (uint index = 0; Win32.SetupDiEnumDeviceInfo(devInfo, index, ref data); index++)
                {
                    string instanceId = InstanceId(devInfo, ref data);
                    if (instanceId.Length == 0) continue;

                    string address = MacFromInstanceId(instanceId);
                    if (address.Length > 0 &&
                        ReadBool(devInfo, ref data, Win32.DeviceIsConnectedFmtid,
                            Win32.DeviceIsConnectedPid))
                    {
                        online[address] = true;
                    }

                    if (audioOnly && !Protocols.IsBluetoothAudioInstance(instanceId)) continue;

                    string friendly = ReadString(devInfo, ref data,
                        Win32.DeviceFriendlyNameFmtid, Win32.DeviceFriendlyNamePid);
                    if (friendly.Length == 0) continue;

                    if (!string.IsNullOrEmpty(namePrefix) &&
                        friendly.IndexOf(namePrefix!, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    int? percent = ReadByte(devInfo, ref data,
                        Win32.BluetoothBatteryFmtid, Win32.BluetoothBatteryPid);
                    if (!percent.HasValue) continue;   // 系统压根没记录过电量

                    nodes.Add(new RawNode
                    {
                        Address = address,
                        FriendlyName = friendly,
                        Percent = percent,
                        UpdatedAt = ReadFileTime(devInfo, ref data,
                            Win32.BluetoothBatteryFmtid, Win32.BluetoothBatteryTimestampPid),
                    });
                }
            }
            catch (Exception)
            {
                // 某棵树枚举失败不该让整轮读数崩：已经收到的部分照常返回
            }
            finally
            {
                Win32.SetupDiDestroyDeviceInfoList(devInfo);
            }
        }

        /// <summary>查单个设备（按名字前缀），取电量最新的那个节点</summary>
        public static BluetoothBatteryInfo? QueryOne(string namePrefix,
            bool classicAudioOnly = false)
        {
            var all = Query(namePrefix, classicAudioOnly);
            if (all.Count == 0) return null;

            // 同一台设备可能有多个节点（BTHENUM 的 A2DP/HFP、BTHLE 的多通道），
            // 取更新时间最新的；时间戳都缺失时优先在线的那个。
            BluetoothBatteryInfo best = all[0];
            for (int i = 1; i < all.Count; i++)
            {
                var candidate = all[i];
                bool newer = candidate.UpdatedAt > best.UpdatedAt;
                bool newerWhenEqual = candidate.UpdatedAt == best.UpdatedAt
                    && candidate.Connected && !best.Connected;
                if (newer || newerWhenEqual) best = candidate;
            }
            return best;
        }

        // ======================================================== 底层读属性
        private static string InstanceId(IntPtr devInfo, ref Win32.SP_DEVINFO_DATA data)
        {
            var buffer = new StringBuilder(512);
            uint required;
            return Win32.SetupDiGetDeviceInstanceIdW(devInfo, ref data, buffer,
                (uint)buffer.Capacity, out required)
                ? buffer.ToString()
                : "";
        }

        /// <summary>
        /// 从 InstanceId 里取蓝牙地址，转成 C0:06:72:D9:96:23。
        ///
        /// 实测两种形态，都要认：
        ///   BTHLE  ：BTHLE\DEV_C00672D99623\7&amp;387A7373&amp;0&amp;C00672D99623
        ///   BTHENUM：BTHENUM\{0000111e-…}_LOCALMFG&amp;0002\7&amp;2b6a77e0&amp;0&amp;445ECD407C2A_C00000000
        ///
        /// BTHENUM 没有 DEV_ 前缀，地址在最后一个 "&amp;0&amp;" 之后，
        /// 以 "_C00000000" 结尾。漏掉这条，耳机的在线判定就无处可取。
        /// </summary>
        public static string MacFromInstanceId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "";

            int idx = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string raw = TakeHex12(instanceId, idx + 4);
                if (raw.Length == 12) return FormatMac(raw);
            }

            int marker = instanceId.LastIndexOf("&0&", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                string raw = TakeHex12(instanceId, marker + 3);
                if (raw.Length == 12) return FormatMac(raw);
            }
            return "";
        }

        /// <summary>从 offset 起取 12 个十六进制字符；中间有任何非 hex 就判失败</summary>
        private static string TakeHex12(string text, int offset)
        {
            if (offset < 0 || offset + 12 > text.Length) return "";
            for (int i = offset; i < offset + 12; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return "";
            }
            return text.Substring(offset, 12);
        }

        private static string FormatMac(string raw)
        {
            string upper = raw.ToUpperInvariant();
            return string.Join(":", upper.Substring(0, 2), upper.Substring(2, 2),
                upper.Substring(4, 2), upper.Substring(6, 2), upper.Substring(8, 2),
                upper.Substring(10, 2));
        }

        private static int? ReadByte(IntPtr devInfo, ref Win32.SP_DEVINFO_DATA data,
            Guid fmtid, uint pid)
        {
            var key = new Win32.DEVPROPKEY { fmtid = fmtid, pid = pid };
            var buffer = new byte[16];
            uint type, required;
            if (!Win32.SetupDiGetDevicePropertyW(devInfo, ref data, ref key, out type,
                    buffer, (uint)buffer.Length, out required, 0))
            {
                return null;
            }
            // DEVPROP_TYPE_BYTE = 0x11；读成 bool/int 时也按首字节取值
            if (type != 0x11 && type != 0x03 && type != 0x07 && type != 0x08) return null;
            return buffer[0];
        }

        private static bool ReadBool(IntPtr devInfo, ref Win32.SP_DEVINFO_DATA data,
            Guid fmtid, uint pid)
        {
            var key = new Win32.DEVPROPKEY { fmtid = fmtid, pid = pid };
            var buffer = new byte[16];
            uint type, required;
            if (!Win32.SetupDiGetDevicePropertyW(devInfo, ref data, ref key, out type,
                    buffer, (uint)buffer.Length, out required, 0))
            {
                return false;
            }
            return buffer[0] != 0;
        }

        private static DateTime ReadFileTime(IntPtr devInfo, ref Win32.SP_DEVINFO_DATA data,
            Guid fmtid, uint pid)
        {
            var key = new Win32.DEVPROPKEY { fmtid = fmtid, pid = pid };
            var buffer = new byte[16];
            uint type, required;
            if (!Win32.SetupDiGetDevicePropertyW(devInfo, ref data, ref key, out type,
                    buffer, (uint)buffer.Length, out required, 0))
            {
                return DateTime.MinValue;
            }
            long fileTime = BitConverter.ToInt64(buffer, 0);
            if (fileTime <= 0) return DateTime.MinValue;
            try
            {
                // 属性是 FILETIME（UTC），转本地时间给界面用
                return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static string ReadString(IntPtr devInfo, ref Win32.SP_DEVINFO_DATA data,
            Guid fmtid, uint pid)
        {
            var key = new Win32.DEVPROPKEY { fmtid = fmtid, pid = pid };
            var buffer = new byte[512];
            uint type, required;
            if (!Win32.SetupDiGetDevicePropertyW(devInfo, ref data, ref key, out type,
                    buffer, (uint)buffer.Length, out required, 0))
            {
                return "";
            }
            int end = (int)Math.Min(required, (uint)buffer.Length);
            if (end < 2) return "";
            // UTF-16 字符串，可能带结尾 \0
            return Encoding.Unicode.GetString(buffer, 0, end).TrimEnd('\0').Trim();
        }
    }
}
