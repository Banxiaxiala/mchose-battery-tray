// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [亚克力必须两级回退] Win11 用 DWMWA_SYSTEMBACKDROP_TYPE + 圆角；
//    Win10 用 SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)。
//    只写一套会导致另一个系统上卡片全透明、文字叠在桌面上看不清。
// 2. [WindowStyle=None 才能圆角] 有标题栏时 DWMWCP_ROUND 只会圆标题栏，
//    无边框窗口必须配合 WindowChrome 的 CornerRadius 才有真正的圆角。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PeripheralBattery.Core;

namespace PeripheralBattery.UI
{
    public static class Acrylic
    {
        /// <summary>Win11 22H2+ 判定：只有这些版本才有 SYSTEMBACKDROP_TYPE</summary>
        private static bool IsWindows11
        {
            get { return Environment.OSVersion.Version.Build >= 22000; }
        }

        /// <summary>
        /// 给窗口套上亚克力毛玻璃 + 圆角。
        ///
        /// 必须在 SourceInitialized 之后调用（那时 Handle 才存在）。
        /// 返回 false 表示系统不支持，调用方应换成不透明底板。
        /// </summary>
        public static bool Apply(Window window, bool dark, double cornerRadius = 10)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            bool ok = false;

            // ---- 第 1 级：Win11 的系统背景 + DWM 圆角 ----
            if (IsWindows11)
            {
                try
                {
                    int preference = cornerRadius >= 8
                        ? Win32.DWMWCP_ROUND
                        : Win32.DWMWCP_ROUNDSMALL;
                    Win32.DwmSetWindowAttribute(handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE,
                        ref preference, sizeof(int));

                    // DWMSBT_NONE：关掉系统自带的 Mica，改用下面的 Accent 方案，
                    // 因为标题栏（Transient）背景会盖住我们的圆角
                    int backdrop = Win32.DWMSBT_NONE;
                    Win32.DwmSetWindowAttribute(handle, Win32.DWMWA_SYSTEMBACKDROP_TYPE,
                        ref backdrop, sizeof(int));

                    int darkMode = dark ? 1 : 0;
                    Win32.DwmSetWindowAttribute(handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE,
                        ref darkMode, sizeof(int));
                    ok = true;
                }
                catch (Exception) { }
            }

            // ---- 第 2 级：Accent 亚克力（Win10 1803+ 与 Win11 都可用）----
            try
            {
                // 色调：深色用偏蓝的近黑，浅色用偏冷的近白。
                // 不透明度取 110~175：太低字看不清，太高就看不到毛玻璃了。
                int alpha = dark ? 175 : 110;
                byte r = (byte)(dark ? 16 : 248);
                byte g = (byte)(dark ? 20 : 250);
                byte b = (byte)(dark ? 26 : 252);
                // GradientColor 是 ABGR
                int gradient = (alpha << 24) | (b << 16) | (g << 8) | r;

                var accent = new Win32.AccentPolicy
                {
                    AccentState = Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    AccentFlags = 0,
                    GradientColor = gradient,
                    AnimationId = 0,
                };

                int size = Marshal.SizeOf<Win32.AccentPolicy>();
                IntPtr data = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, data, false);
                    var attribute = new Win32.WindowCompositionAttributeData
                    {
                        Attribute = Win32.WCA_ACCENT_POLICY,
                        Data = data,
                        SizeOfData = size,
                    };
                    ok = Win32.SetWindowCompositionAttribute(handle, ref attribute) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }
            catch (Exception) { }

            return ok;
        }

        /// <summary>关闭亚克力，退回完全不透明（系统不支持时的兜底）</summary>
        public static void Clear(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            try
            {
                var accent = new Win32.AccentPolicy
                {
                    AccentState = Win32.AccentState.ACCENT_DISABLED,
                };
                int size = Marshal.SizeOf<Win32.AccentPolicy>();
                IntPtr data = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, data, false);
                    var attribute = new Win32.WindowCompositionAttributeData
                    {
                        Attribute = Win32.WCA_ACCENT_POLICY,
                        Data = data,
                        SizeOfData = size,
                    };
                    Win32.SetWindowCompositionAttribute(handle, ref attribute);
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }
            catch (Exception) { }
        }
    }
}
