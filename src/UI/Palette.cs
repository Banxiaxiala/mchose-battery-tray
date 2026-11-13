// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [配色唯一来源] 所有颜色只允许定义在本文件。改动视觉时必须先改这里，
//    再去对照 assets/preview.png 出图核对，不许在控件里散写十六进制。
// 2. [深浅双主题] 每一组颜色都必须同时给出浅色与深色值。
//    只做一套然后靠透明度“凑”出来的效果，在另一种主题下一定是灰糊的。
// 3. [亚克力配合] 亚克力开启时卡片底色必须透明（否则盖住毛玻璃），
//    关闭时必须换成不透明底板（否则文字直接叠在桌面上，看不清）。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Windows.Media;

namespace PeripheralBattery.UI
{
    public static class Palette
    {
        // ---------------------------------------------------------- 文字
        public static readonly Color TextPrimaryLight = Color.FromRgb(0x0F, 0x17, 0x2A);
        public static readonly Color TextPrimaryDark = Color.FromRgb(0xF2, 0xF5, 0xF9);
        public static readonly Color TextSecondaryLight = Color.FromRgb(0x47, 0x55, 0x69);
        public static readonly Color TextSecondaryDark = Color.FromRgb(0xB4, 0xBE, 0xCC);
        public static readonly Color TextFaintLight = Color.FromRgb(0x64, 0x74, 0x8B);
        public static readonly Color TextFaintDark = Color.FromRgb(0x88, 0x94, 0xA6);

        // ---------------------------------------------------------- 电量档位
        // 参照 logi-tray 的 AppPalette：绿=充足，黄=偏低，红=告急
        public static readonly Color BatteryHigh = Color.FromRgb(0x22, 0xC5, 0x5E);
        // 四档必须能在 24px 图标上互相区分：
        //   充足 绿 → 一般 黄 → 偏低 橙 → 告急 红
        // 早期版本是「一般 橙 / 偏低 红 / 告急 深红」，后两者在 24px 上
        // 几乎一样，用户分不清「该充电了」和「马上要关机了」。
        public static readonly Color BatteryMid = Color.FromRgb(0xE8, 0xC4, 0x3C);
        public static readonly Color BatteryLow = Color.FromRgb(0xF5, 0x8F, 0x28);
        public static readonly Color BatteryCritical = Color.FromRgb(0xDC, 0x26, 0x26);
        public static readonly Color Charging = Color.FromRgb(0x22, 0xC5, 0x5E);

        public static readonly Color AccentLight = Color.FromRgb(0x00, 0x67, 0xC0);
        public static readonly Color AccentDark = Color.FromRgb(0x60, 0xCD, 0xFF);

        // ---------------------------------------------------------- 表面
        public static readonly Color SurfaceLight = Color.FromRgb(0xF8, 0xFA, 0xFC);
        public static readonly Color SurfaceDark = Color.FromRgb(0x10, 0x14, 0x1A);
        public static readonly Color TrackLight = Color.FromArgb(0x26, 0x00, 0x00, 0x00);
        public static readonly Color TrackDark = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

            // ---------------------------------------------------------- 用户自定义
        // 由 Settings.ApplyToPalette 在启动与设置变化时写入。
        // 留空（null）= 用内置默认，这样升级后新增的颜色项自动可用。
        private static Color? _high, _mid, _low, _critical, _charging;

        public static int ThresholdCritical { get; private set; } = 10;
        public static int ThresholdLow { get; private set; } = 30;
        public static int ThresholdMid { get; private set; } = 55;

        /// <summary>
        /// 应用用户配色与阈值。
        ///
        /// 解析失败的项保持「使用默认」，绝不因为颜色写错就让整张卡片变黑 ——
        /// settings.json 是让用户手改的，写错一个字符很常见。
        /// </summary>
        public static void ApplyCustom(
            string? high, string? mid, string? low, string? critical, string? charging,
            int thresholdCritical, int thresholdLow, int thresholdMid)
        {
            _high = Parse(high);
            _mid = Parse(mid);
            _low = Parse(low);
            _critical = Parse(critical);
            _charging = Parse(charging);

            // 阈值必须严格递增，否则会出现「不可达档位」
            int a = Math.Clamp(thresholdCritical, 1, 60);
            int b = Math.Clamp(thresholdLow, a + 1, 90);
            int c = Math.Clamp(thresholdMid, b + 1, 99);
            ThresholdCritical = a;
            ThresholdLow = b;
            ThresholdMid = c;
        }

        /// <summary>#RRGGBB / #AARRGGBB；解析失败返回 null（= 用默认）</summary>
        private static Color? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Trim();
            if (value.StartsWith("#")) value = value.Substring(1);
            if (value.Length != 6 && value.Length != 8) return null;
            try
            {
                uint packed = Convert.ToUInt32(value, 16);
                if (value.Length == 6)
                {
                    return Color.FromRgb(
                        (byte)((packed >> 16) & 0xFF),
                        (byte)((packed >> 8) & 0xFF),
                        (byte)(packed & 0xFF));
                }
                return Color.FromArgb(
                    (byte)((packed >> 24) & 0xFF),
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>电量 → 颜色。阈值与托盘图标共用同一套（可被设置覆盖）</summary>
        public static Color ForBattery(int percent, bool charging)
        {
            if (charging) return _charging ?? Charging;
            if (percent < 0) return _mid ?? BatteryMid;
            if (percent <= ThresholdCritical) return _critical ?? BatteryCritical;
            if (percent <= ThresholdLow) return _low ?? BatteryLow;
            if (percent <= ThresholdMid) return _mid ?? BatteryMid;
            return _high ?? BatteryHigh;
        }

        /// <summary>档位文案；阈值与颜色共用同一套边界</summary>
        public static string LevelTextOf(int percent, bool charging)
        {
            if (charging) return "充电中";
            if (percent <= ThresholdCritical) return "极低";
            if (percent <= ThresholdLow) return "偏低";
            if (percent <= ThresholdMid) return "一般";
            if (percent <= 80) return "良好";
            return "充足";
        }

        /// <summary>电量 → 档位文案</summary>
        public static string LevelText(int percent, bool charging, bool connected)
        {
            if (!connected) return "已离线";
            return LevelTextOf(percent, charging);
        }

        public static Brush BrushOf(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>主题状态：跟随系统，读注册表，不提供手动开关</summary>
    public static class Theme
    {
        private static bool? _isDark;

        /// <summary>
        /// 强制指定明暗（null = 跟随系统）。
        /// 设置里关掉「跟随系统主题」时会设为 true，让托盘图标恒用白线。
        /// </summary>
        public static bool? ForceDark { get; set; }

        public static bool IsDark
        {
            get
            {
                if (ForceDark.HasValue) return ForceDark.Value;
                if (!_isDark.HasValue) _isDark = ReadSystemDark();
                return _isDark.Value;
            }
        }

        /// <summary>任务栏是否浅色。读不到就按深色（白线）处理，与系统默认一致</summary>
        private static bool ReadSystemDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? value = key?.GetValue("SystemUsesLightTheme");
                if (value is int light) return light == 0;
            }
            catch (Exception) { }
            return true;
        }

        public static void Invalidate()
        {
            _isDark = null;
        }
    }
}
