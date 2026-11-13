// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [零第三方依赖] 只用 Core/Win32.cs 里的 P/Invoke，不许引入 hidapi 等包。
// 2. [常驻挂起读] PendingRead 必须让读请求一直挂在驱动里，取到数据才重启下一轮。
//    罗技接收器对「不可达槽位」只在短集合上回 3~5ms 的错误帧；若每次 poll 都
//    CancelIoEx 再重建 ReadFile，取消与重建之间的空窗恰好会吃掉这条帧，
//    空槽位便永远判不出来、只能等满超时。
// 3. [路径偏移] SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize 固定是 DWORD(4)，
//    设备路径从第 4 字节开始（不分 x86/x64）。按 x64 写成 8 会吃掉路径前 4 个
//    字符，CreateFile 直接报错误 123。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PeripheralBattery.Core
{
    /// <summary>一个已枚举到的 HID 集合（只描述，不持有句柄）</summary>
    public sealed class HidInterface
    {
        public string Path { get; init; } = "";
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public int UsagePage { get; init; }
        public int Usage { get; init; }
        public string Product { get; init; } = "";
        public string Serial { get; init; } = "";
        public int InputLength { get; init; }
        public int OutputLength { get; init; }
        public int FeatureLength { get; init; }

        public override string ToString()
        {
            return $"{VendorId:X4}:{ProductId:X4} up={UsagePage:X4} u={Usage:X4} "
                 + $"io={InputLength}/{OutputLength} feat={FeatureLength} prod={Product}";
        }
    }

    public static class HidEnumerator
    {
        /// <summary>
        /// 枚举 HID 集合，顺带带回 Input/Output/Feature 报告长度。
        /// 协议层判断「这个接口能不能用」全靠报告长度
        /// （罗技短集合 7 / 长集合 20、雷蛇 64/64 且 feature=0）。
        /// </summary>
        public static List<HidInterface> Enumerate(int? vendorId = null, int? productId = null)
        {
            var results = new List<HidInterface>();

            Win32.HidD_GetHidGuid(out Guid guid);
            IntPtr infoSet = Win32.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                Win32.DIGCF_PRESENT | Win32.DIGCF_DEVICEINTERFACE);
            if (infoSet == IntPtr.Zero || infoSet == Win32.InvalidHandle) return results;

            try
            {
                var iface = new Win32.SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = (uint)Marshal.SizeOf<Win32.SP_DEVICE_INTERFACE_DATA>();

                for (uint index = 0; ; index++)
                {
                    if (!Win32.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref guid,
                                                           index, ref iface))
                    {
                        break;
                    }
                    string? path = GetDevicePath(infoSet, ref iface);
                    if (string.IsNullOrEmpty(path)) continue;

                    var described = Describe(path!, vendorId, productId);
                    if (described != null) results.Add(described);
                }
            }
            finally
            {
                Win32.SetupDiDestroyDeviceInfoList(infoSet);
            }
            return results;
        }

        private static string? GetDevicePath(IntPtr infoSet,
                                             ref Win32.SP_DEVICE_INTERFACE_DATA iface)
        {
            if (!Win32.SetupDiGetDeviceInterfaceDetailW(infoSet, ref iface, IntPtr.Zero,
                                                        0, out uint required, IntPtr.Zero))
            {
                if (required == 0) return null;
            }
            if (required == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // cbSize 固定 4 字节（x64 也是 4，不是 8），路径紧随其后
                Marshal.WriteInt32(buffer, 8);
                if (!Win32.SetupDiGetDeviceInterfaceDetailW(infoSet, ref iface, buffer,
                        required, out _, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static HidInterface? Describe(string path, int? vendorId, int? productId)
        {
            // 只用 0 权限打开：足够读属性，且不会撞上键盘类的防监听独占锁
            IntPtr handle = Win32.OpenDevice(path, readWrite: false, overlapped: false);
            if (handle == Win32.InvalidHandle || handle == IntPtr.Zero) return null;
            try
            {
                var attrs = new Win32.HIDD_ATTRIBUTES();
                attrs.Size = (uint)Marshal.SizeOf<Win32.HIDD_ATTRIBUTES>();
                if (!Win32.HidD_GetAttributes(handle, ref attrs)) return null;
                if (vendorId.HasValue && attrs.VendorID != vendorId.Value) return null;
                if (productId.HasValue && attrs.ProductID != productId.Value) return null;

                if (!Win32.HidD_GetPreparsedData(handle, out IntPtr pre) || pre == IntPtr.Zero)
                {
                    return null;
                }
                Win32.HIDP_CAPS caps;
                try
                {
                    caps = new Win32.HIDP_CAPS { Reserved = new ushort[17] };
                    if (Win32.HidP_GetCaps(pre, ref caps) != 0x00110000) return null;
                }
                finally
                {
                    Win32.HidD_FreePreparsedData(pre);
                }

                return new HidInterface
                {
                    Path = path,
                    VendorId = attrs.VendorID,
                    ProductId = attrs.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    Product = ReadString(handle, isProduct: true),
                    Serial = ReadString(handle, isProduct: false),
                    InputLength = caps.InputReportByteLength,
                    OutputLength = caps.OutputReportByteLength,
                    FeatureLength = caps.FeatureReportByteLength,
                };
            }
            finally
            {
                Win32.CloseHandle(handle);
            }
        }

        private static string ReadString(IntPtr handle, bool isProduct)
        {
            var buffer = new char[256];
            bool ok = isProduct
                ? Win32.HidD_GetProductString(handle, buffer, (uint)buffer.Length)
                : Win32.HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Length);
            if (!ok) return "";
            int end = Array.IndexOf(buffer, '\0');
            if (end < 0) end = buffer.Length;
            return new string(buffer, 0, end).Trim();
        }
    }

    /// <summary>打开着的 HID 句柄，支持带超时的重叠读写</summary>
    public sealed class HidDevice : IDisposable
    {
        private IntPtr _handle;
        private readonly IntPtr _event;
        private bool _disposed;

        public string Path { get; }
        public bool IsClosed { get { return _disposed || _handle == IntPtr.Zero; } }

        public HidDevice(string path)
        {
            Path = path;
            // 必须以 FILE_FLAG_OVERLAPPED 打开，否则重叠 I/O 退化为同步阻塞
            _handle = Win32.OpenDevice(path, readWrite: true, overlapped: true);
            if (_handle == Win32.InvalidHandle || _handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"打开 HID 设备失败（错误 {Marshal.GetLastWin32Error()}）：{path}");
            }
            _event = Win32.CreateEventW(IntPtr.Zero, true, false, null);
            if (_event == IntPtr.Zero)
            {
                Win32.CloseHandle(_handle);
                _handle = IntPtr.Zero;
                throw new InvalidOperationException("CreateEvent 失败");
            }
        }

        public IntPtr Handle { get { return _handle; } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                Win32.CancelIoEx(_handle, IntPtr.Zero);
            }
            if (_event != IntPtr.Zero) Win32.CloseHandle(_event);
            if (_handle != IntPtr.Zero) Win32.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        /// <summary>写一条输出报告（中断 OUT）。返回写入字节数，失败返回 -1</summary>
        /// <summary>
        /// 写 Feature Report。
        ///
        /// 与 <see cref="Write"/> 的区别：Write 走中断 OUT 端点（流式、有超时、
        /// 可重叠），Feature 走控制端点（一问一答、同步、无超时）。
        /// 有些设备的控制通道只暴露 Feature —— 中断端点什么也收不到。
        ///
        /// buffer[0] 必须是 report id。
        /// </summary>
        public bool SetFeature(byte[] data)
        {
            if (IsClosed || data == null || data.Length == 0) return false;
            try
            {
                return Win32.HidD_SetFeature(_handle, data, (uint)data.Length);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 读 Feature Report。buffer[0] 传 report id，返回时被填充为完整报告。
        /// 失败或全是 0 时返回 false —— 设备没更新数据时读回来是全零。
        /// </summary>
        public bool GetFeature(byte[] buffer)
        {
            if (IsClosed || buffer == null || buffer.Length == 0) return false;
            try
            {
                if (!Win32.HidD_GetFeature(_handle, buffer, (uint)buffer.Length))
                {
                    return false;
                }
                foreach (byte value in buffer)
                {
                    if (value != 0) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int Write(byte[] data, double timeout = 0.5)
        {
            if (IsClosed) return -1;

            IntPtr buffer = Marshal.AllocHGlobal(data.Length);
            var ov = new Win32.OVERLAPPED();
            try
            {
                Marshal.Copy(data, 0, buffer, data.Length);
                ov.hEvent = _event;
                Win32.ResetEvent(_event);

                bool ok = WriteFileRaw(_handle, buffer, (uint)data.Length, out uint written, ref ov);
                if (!ok)
                {
                    if (Marshal.GetLastWin32Error() != Win32.ERROR_IO_PENDING) return -1;
                    if (Win32.WaitForSingleObject(_event, (uint)(timeout * 1000)) != Win32.WAIT_OBJECT_0)
                    {
                        Win32.CancelIoEx(_handle, IntPtr.Zero);
                        Win32.GetOverlappedResult(_handle, ref ov, out written, true);
                        return -1;
                    }
                    if (!Win32.GetOverlappedResult(_handle, ref ov, out written, false)) return -1;
                }
                return (int)written;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>一次性同步读；超时返回空数组。低延迟轮询请用 PendingRead</summary>
        public byte[] Read(int length, double timeout = 0.2)
        {
            if (IsClosed) return Array.Empty<byte>();

            IntPtr buffer = Marshal.AllocHGlobal(length);
            var ov = new Win32.OVERLAPPED();
            try
            {
                ov.hEvent = _event;
                Win32.ResetEvent(_event);

                bool ok = ReadFileRaw(_handle, buffer, (uint)length, out uint read, ref ov);
                if (!ok)
                {
                    if (Marshal.GetLastWin32Error() != Win32.ERROR_IO_PENDING)
                    {
                        return Array.Empty<byte>();
                    }
                    if (Win32.WaitForSingleObject(_event, (uint)(timeout * 1000)) != Win32.WAIT_OBJECT_0)
                    {
                        Win32.CancelIoEx(_handle, IntPtr.Zero);
                        Win32.GetOverlappedResult(_handle, ref ov, out read, true);
                        return Array.Empty<byte>();
                    }
                    if (!Win32.GetOverlappedResult(_handle, ref ov, out read, false))
                    {
                        return Array.Empty<byte>();
                    }
                }
                if (read == 0) return Array.Empty<byte>();
                var data = new byte[read];
                Marshal.Copy(buffer, data, 0, (int)read);
                return data;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(IntPtr hFile, IntPtr lpBuffer, uint nBytes,
            out uint written, ref Win32.OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, uint nBytes,
            out uint read, ref Win32.OVERLAPPED ov);

        private static bool WriteFileRaw(IntPtr h, IntPtr buf, uint n, out uint written,
            ref Win32.OVERLAPPED ov)
        {
            return WriteFile(h, buf, n, out written, ref ov);
        }

        private static bool ReadFileRaw(IntPtr h, IntPtr buf, uint n, out uint read,
            ref Win32.OVERLAPPED ov)
        {
            return ReadFile(h, buf, n, out read, ref ov);
        }
    }

    /// <summary>
    /// 常驻挂起读。
    ///
    /// 读请求一直挂在驱动里，报告一到达就能立刻取走，不存在「取消 → 重建」的
    /// 空窗（那条 3~5ms 的不可达回绝帧正是掉在这里）。取到数据之后才重新挂
    /// 下一轮，因此 Poll 语义是「等这一条」，不会一次丢两条。
    /// </summary>
    public sealed class PendingRead : IDisposable
    {
        private readonly HidDevice _device;
        private readonly int _length;
        private IntPtr _buffer;
        private Win32.OVERLAPPED _ov;
        private readonly IntPtr _event;
        private bool _pending;
        private bool _broken;
        private bool _cancelled;
        private uint _lastRead;

        public PendingRead(HidDevice device, int length)
        {
            _device = device;
            _length = length;
            _buffer = Marshal.AllocHGlobal(length);
            _event = Win32.CreateEventW(IntPtr.Zero, true, false, null);
            if (_event == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");
            _ov.hEvent = _event;
        }

        private void Arm()
        {
            if (_pending || _broken || _cancelled || _device.IsClosed) return;
            Win32.ResetEvent(_event);
            bool ok = ReadFile(_device.Handle, _buffer, (uint)_length, out _lastRead, ref _ov);
            if (ok)
            {
                // 同步完成（数据已在 buffer 里），下一次 Poll 会立刻取走
                _pending = true;
            }
            else if (Marshal.GetLastWin32Error() == Win32.ERROR_IO_PENDING)
            {
                _pending = true;
            }
            else
            {
                _broken = true;
            }
        }

        private byte[] Take()
        {
            bool ok = Win32.GetOverlappedResult(_device.Handle, ref _ov, out uint count, false);
            _pending = false;
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() == Win32.ERROR_INVALID_PARAMETER)
                {
                    _broken = true;
                }
                return Array.Empty<byte>();
            }
            if (count == 0) { Arm(); return Array.Empty<byte>(); }
            var data = new byte[count];
            Marshal.Copy(_buffer, data, 0, (int)count);
            Arm();
            return data;
        }

        public PendingRead Start() { Arm(); return this; }

        /// <summary>在 timeout 内等一条报告；没有就返回空数组</summary>
        public byte[] Poll(double timeout = 0.0)
        {
            if (_cancelled) return Array.Empty<byte>();
            if (!_pending)
            {
                Arm();
                if (!_pending) return Array.Empty<byte>();
            }
            uint wait = timeout > 0 ? (uint)(timeout * 1000) : 0u;
            if (Win32.WaitForSingleObject(_event, wait) != Win32.WAIT_OBJECT_0)
            {
                return Array.Empty<byte>();
            }
            return Take();
        }

        public void Dispose()
        {
            if (_cancelled) return;
            _cancelled = true;
            if (_pending && _ov.hEvent != IntPtr.Zero && !_device.IsClosed)
            {
                Win32.CancelIoEx(_device.Handle, IntPtr.Zero);
                Win32.GetOverlappedResult(_device.Handle, ref _ov, out _, true);
                _pending = false;
            }
            if (_event != IntPtr.Zero) Win32.CloseHandle(_event);
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, uint nBytes,
            out uint read, ref Win32.OVERLAPPED ov);
    }
}
