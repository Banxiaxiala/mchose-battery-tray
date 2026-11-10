// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [读系统属性，不连设备] Windows 自己已经在维护 BLE 设备电量
//    （「设置 → 蓝牙」里显示的那个百分比）。读 PnP 属性是毫秒级、
//    不需要连设备、不会跟系统抢连接。走 GATT 需要 BLE 栈（WinRT），
//    而且已配对的 BLE HID 会被系统独占，去连它会失败或影响键盘输入。
// 2. [按设备名匹配，不按 MAC 写死] BLE 设备的 MAC 会随重配对变化，
//    只允许按 FriendlyName 前缀匹配（MAX102-BT1/BT2/BT3）。
// 3. [区分“没读到”和“没值时”] 属性不存在 → NoValue；属性存在但为 0
//    也是合法电量（真的没电了），不能当成“读不到”。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Text;

namespace PeripheralBattery.Core
{
    /// <summary>一个蓝牙（BTHLE）节点上的电量快照</summary>
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
        /// <summary>DEVPKEY_Device_IsConnected</summary>
        public bool Connected { get; init; }

        public override string ToString()
        {
            return $"{FriendlyName} [{Address}] "
                 + (Percent.HasValue ? Percent.Value + "%" : "无电量属性")
                 + (Connected ? " 在线" : " 离线");
        }
    }

    /// <summary>
    /// 经 SetupAPI 读取 BTHLE 设备节点上的电量属性。
    ///
    /// 为什么不用 BluetoothFindFirstDevice / bthprops：那是经典蓝牙（BR/EDR）
    /// 的枚举，BLE 设备（BTHLE\DEV_xxx）根本不在里面。
    /// </summary>
    public static class BluetoothBattery
    {
        /// <summary>
        /// 枚举所有带电量属性的 BTHLE 节点。
        /// namePrefix 为空表示不过滤；给了前缀就只回名字以它开头的节点。
        /// </summary>
        public static List<BluetoothBatteryInfo> Query(string? namePrefix = null)
        {
            var results = new List<BluetoothBatteryInfo>();

            IntPtr devInfo = Win32.SetupDiGetClassDevsW(IntPtr.Zero, "BTHLE", IntPtr.Zero,
                Win32.DIGCF_PRESENT | Win32.DIGCF_ALLCLASSES);
            if (devInfo == IntPtr.Zero || devInfo == Win32.InvalidHandle) return results;

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

                    results.Add(new BluetoothBatteryInfo
                    {
                        Address = MacFromInstanceId(instanceId),
                        FriendlyName = friendly,
                        Percent = percent,
                        UpdatedAt = ReadFileTime(devInfo, ref data,
                            Win32.BluetoothBatteryFmtid, Win32.BluetoothBatteryTimestampPid),
                        Connected = ReadBool(devInfo, ref data,
                            Win32.DeviceIsConnectedFmtid, Win32.DeviceIsConnectedPid),
                    });
                }
            }
            catch (Exception)
            {
                // 枚举失败不该让托盘崩：返回已收集到的部分
            }
            finally
            {
                Win32.SetupDiDestroyDeviceInfoList(devInfo);
            }

            return results;
        }

        /// <summary>查单个设备（按名字前缀），取电量最高/最新的那一个节点</summary>
        public static BluetoothBatteryInfo? QueryOne(string namePrefix)
        {
            var all = Query(namePrefix);
            if (all.Count == 0) return null;

            // 同一台设备可能有多个 BTHLE 节点（多通道），取更新时间最新的
            BluetoothBatteryInfo best = all[0];
            for (int i = 1; i < all.Count; i++)
            {
                if (all[i].UpdatedAt > best.UpdatedAt) best = all[i];
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

        /// <summary>从 BTHLE\DEV_c00672d99623\… 里取地址，转成 C0:06:72:D9:96:23</summary>
        public static string MacFromInstanceId(string instanceId)
        {
            int idx = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || instanceId.Length < idx + 16) return "";
            string raw = instanceId.Substring(idx + 4, 12).ToUpperInvariant();
            if (raw.Length != 12) return "";
            return string.Join(":", raw.Substring(0, 2), raw.Substring(2, 2),
                raw.Substring(4, 2), raw.Substring(6, 2), raw.Substring(8, 2),
                raw.Substring(10, 2));
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
