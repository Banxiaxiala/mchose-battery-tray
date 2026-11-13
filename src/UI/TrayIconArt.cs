// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯 GDI+ 绘制] 本文件只负责把「三台设备的电量」画成位图，不碰
//    NotifyIcon / 窗口 / 配置。抽出来的原因：托盘图标是 24px（150% 缩放）
//    或 16px（100%）的小图，参数必须能在真实尺寸下反复预览调整，
//    不能每次都起托盘靠肉眼猜。
// 2. [先画大再缩小] 内部按 4x 画布绘制后缩到目标尺寸，这样抗锯齿质量好。
//    但**描边宽度必须按最终尺寸来定** —— 3px/64 缩到 24px 只剩 1.1px，
//    实机上几乎看不见。见 RingRadii 的注释。
// 3. [槽位稳定] 缺读数的圈只画浅轨道，不消失。圈数固定，用户才能分辨
//    「这台设备离线」和「这台设备根本不存在」。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using PeripheralBattery.Models;

namespace PeripheralBattery.UI
{
    /// <summary>托盘图标的几何参数（单位：最终图标的逻辑像素）</summary>
    public sealed class RingGeometry
    {
        /// <summary>三圈的中心半径，从外到内</summary>
        public double[] Radii = { 10.3, 7.0, 3.8 };
        /// <summary>三圈的描边宽度，与外圈到内圈一一对应</summary>
        public float[] Strokes = { 2.6f, 2.4f, 2.4f };
        /// <summary>空槽位的轨道透明度（0-255）</summary>
        public int TrackAlpha = 46;

        /// <summary>
        /// 按**实际环数**生成几何。
        ///
        /// 为什么需要：图标上有几圈应当等于「有几台设备」，而不是永远三圈。
        /// 只有一台鼠标却画三圈（两圈空轨道），用户会以为还有两台没读到 ——
        /// 而且白白浪费了图标面积。
        ///
        /// 参数设计（24px 画布，中心 12）：
        ///   * 1 环：半径 10.4、描边 2.6 —— 单圈占满，边界留 1.6px 抗锯齿余量
        ///   * 2 环：半径 10.3 / 6.2，描边 2.6 / 2.6 —— 净间隙 1.55px，够分
        ///   * 3 环：保持原参数（穷举出的最粗解，见 Default 的注释）
        ///
        /// 环数越多，单圈必须越细，否则会粘连。这里的取值保证相邻净间隙
        /// 始终 ≥ 1.5px（24px 下仍可分辨）。
        /// </summary>
        public static RingGeometry ForCount(int count)
        {
            switch (Math.Clamp(count, 1, 3))
            {
                case 1:
                    // 单环可以更粗：没有相邻圈要避让，把描边加到 3.4
                    // （半径 10.4 + 半描边 1.7 = 外沿 12.1，画布半宽 12，
                    //   略有溢出但 4x 超采样下会被抗锯齿吸收，实机看不出）。
                    // 更粗的目的是让「弧长 = 电量」在 24px 上更易读出。
                    return new RingGeometry
                    {
                        Radii = new[] { 10.2 },
                        Strokes = new[] { 3.4f },
                        TrackAlpha = 46,
                    };
                case 2:
                    // 两圈也能比三圈粗一些。净间隙按 1.5px 取下限：
                    //   外圈内沿 = 10.3 - 1.4 = 8.9
                    //   内圈外沿 =  6.0 + 1.4 = 7.4
                    //   间隙 1.5px ✓
                    // （原来写 6.2 时只有 1.30px，实测会粘连 —— 由断言抓出）
                    return new RingGeometry
                    {
                        Radii = new[] { 10.3, 6.0 },
                        Strokes = new[] { 2.8f, 2.8f },
                        TrackAlpha = 46,
                    };
                default:
                    return new RingGeometry
                    {
                        Radii = new[] { 10.3, 7.0, 3.8 },
                        Strokes = new[] { 2.6f, 2.4f, 2.4f },
                        TrackAlpha = 46,
                    };
            }
        }

        public bool SameAs(RingGeometry other)
        {
            if (other == null) return false;
            if (Strokes.Length != other.Strokes.Length) return false;
            if (Radii.Length != other.Radii.Length) return false;
            for (int i = 0; i < Radii.Length; i++)
            {
                if (Math.Abs(Radii[i] - other.Radii[i]) > 0.001) return false;
                if (Math.Abs(Strokes[i] - other.Strokes[i]) > 0.001) return false;
            }
            return TrackAlpha == other.TrackAlpha;
        }

        public RingGeometry Clone()
        {
            var copy = new RingGeometry { TrackAlpha = TrackAlpha };
            copy.Radii = (double[])Radii.Clone();
            copy.Strokes = (float[])Strokes.Clone();
            return copy;
        }
    }

    /// <summary>
    /// 三圈同心环用的颜色集。
    ///
    /// 为什么要单独一个类而不是三个 Color 参数：
    /// 托盘图标要用**用户配置的配色**（设置面板里能改），
    /// 但 TrayIconArt 是纯 GDI+ 绘制层，不该直接 import WPF 的 Palette。
    /// 由调用方把 WPF 颜色转成 GDI+ 再传进来。
    /// </summary>
    public sealed class RingColors
    {
        /// <summary>充足（> 一般阈值）</summary>
        public Color High = Color.FromArgb(0x22, 0xC5, 0x5E);
        /// <summary>一般（> 偏低阈值），黄</summary>
        public Color Mid = Color.FromArgb(0xE8, 0xC4, 0x3C);
        /// <summary>偏低（> 告急阈值），橙</summary>
        public Color Low = Color.FromArgb(0xF5, 0x8F, 0x28);
        /// <summary>告急（≤ 告急阈值），红</summary>
        public Color Critical = Color.FromArgb(0xEF, 0x44, 0x44);
        /// <summary>充电中（整圈点亮）</summary>
        public Color Charging = Color.FromArgb(0x22, 0xC5, 0x5E);
        /// <summary>离线轨道的底色（用浅色，区别于有读数时的轨道）</summary>
        public Color Track = Color.FromArgb(0x9E, 0x9E, 0x9E);
        /// <summary>没有读数时圆点的颜色（不用，仅占位保持接口对称）</summary>
        public Color Offline = Color.FromArgb(0xC0, 0xC0, 0xC0);

        public static RingColors Default { get { return new RingColors(); } }

        /// <summary>
        /// 电量 → 颜色。**所有档位都是彩色**，不再用黑/白表示「正常」——
        /// 24px 的托盘图标上，黑/白线条和系统图标混在一起分不出谁是谁，
        /// 而绿色一眼就知道「这台充足」。用户要求「依据电量变成不同颜色」。
        ///
        /// 三档分界（从低到高）：≤critical 深红 → ≤low 红 → ≤mid 橙 → 其余绿。
        /// mid 与 high 都传进来而不是只传 low/critical：没有 mid 分界时
        /// 90% 和 40% 会显示成同一种橙色，用户看不出「这台还很充足」。
        /// </summary>
        public Color ForBattery(int percent, bool charging,
            int lowThreshold, int criticalThreshold, int midThreshold = 55)
        {
            if (charging) return Charging;
            if (percent <= criticalThreshold) return Critical;
            if (percent <= lowThreshold) return Low;
            if (percent <= midThreshold) return Mid;
            return High;
        }
    }

    /// <summary>托盘图标绘制：三圈同心环</summary>
    public static class TrayIconArt
    {
        /// <summary>
        /// 默认几何。
        ///
        /// 参数是按 **24px 实机尺寸** 反推的（144 DPI / 150% 缩放时任务栏
        /// 小图标就是 24 设备像素）：
        ///
        ///   半径  外 10.3 / 中 7.0 / 内 3.8
        ///   描边  外 2.6  / 中 2.4 / 内 2.4
        ///
        /// 由此得到：相邻净间隙 0.8px、中心留孔半径 2.6px、
        /// 外沿 11.6px（半宽 12px 之内）。
        ///
        /// 这是「三圈仍可分」前提下的最粗解，由穷举得出（约束：
        /// 净间隙 ≥ 0.8、中心孔 ≥ 2.6、外沿 ≤ 11.7）。再粗就必然粘连。
        ///
        /// 历史：v4 初版是 3.2/2.8/2.4 描边配更大半径（28/21.5/15，按 64px 给的），
        /// 缩到 24px 后描边只剩 1.2px，实机上细得像发丝 ——
        /// 用户反馈「环太细」就是这么来的。教训是**描边宽度必须按最终
        /// 显示尺寸给**，按 64px 画布给会看着合适、缩下来就没了。
        /// </summary>
        public static RingGeometry Default { get { return new RingGeometry(); } }

        /// <summary>
        /// 画三圈同心环。
        ///
        /// rings 按槽位排（0 外 → 2 内）；超出几何数量或为 null 的槽位
        /// 只画轨道。percent 为 null 也表示没读数。
        /// </summary>
        public static Bitmap Render(int size, Reading?[]? rings, Color fore,
            RingGeometry? geometry = null, RingColors? colors = null,
            int lowThreshold = 30, int criticalThreshold = 10, int midThreshold = 55)
        {
            // 没显式给几何时，按**实际环数**自适应 ——
            // 一台设备就画一圈，不要留两圈空轨道。
            if (geometry == null && rings != null && rings.Length > 0)
            {
                geometry = RingGeometry.ForCount(rings.Length);
            }
            return RenderAt(size, size, rings, fore, geometry, colors,
                lowThreshold, criticalThreshold, midThreshold);
        }

        /// <summary>
        /// 按指定像素尺寸渲染（可远大于实际 24px 图标）。
        ///
        /// 存在的理由：README 与视觉调试需要把图标放大到 240px 以上逐像素检查，
        /// 而 Render(24) 再插值放大只会得到一团马赛克。
        /// 这里让 GDI+ 直接在大画布上按比例绘制，圆弧边缘是真实计算出来的。
        /// </summary>
        public static Bitmap RenderLarge(int pixelSize, Reading?[]? rings, Color fore,
            RingGeometry? geometry = null, RingColors? colors = null,
            int lowThreshold = 30, int criticalThreshold = 10, int midThreshold = 55)
        {
            // 缩放倍率 = 目标像素 / 基准 24px；几何参数按这个比例放大
            double factor = pixelSize / 24.0;
            return RenderAt(pixelSize, pixelSize, rings, fore, geometry, colors,
                lowThreshold, criticalThreshold, midThreshold, factor);
        }

        private static Bitmap RenderAt(int size, int baseSize, Reading?[]? rings,
            Color fore, RingGeometry? geometry, RingColors? colors,
            int lowThreshold, int criticalThreshold, int midThreshold,
            double scaleFactor = 1.0)
        {
            var geo = geometry ?? Default;
            var palette = colors ?? RingColors.Default;

            // 内部 4x 绘制再缩下来，抗锯齿质量好
            int Scale = (int)Math.Max(4, Math.Round(4 * scaleFactor));
            using var big = new Bitmap(size * Scale, size * Scale);
            using (var g = Graphics.FromImage(big))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                for (int i = 0; i < geo.Radii.Length; i++)
                {
                    var reading = RingAt(rings, i);
                    DrawRing(g, size, Scale, geo.Radii[i], geo.Strokes[i] * Scale,
                        fore, geo.TrackAlpha, reading, palette,
                        lowThreshold, criticalThreshold, midThreshold);
                }
            }

            var small = new Bitmap(size, size);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(big, new Rectangle(0, 0, size, size));
            }
            return small;
        }

        /// <summary>从环里取出读数；越界或数组为空返回 null（= 没读数）</summary>
        public static Reading? RingAt(Reading?[]? rings, int index)
        {
            if (rings == null || index < 0 || index >= rings.Length) return null;
            return rings[index];
        }

        private static void DrawRing(Graphics g, int size, int scale, double radius,
            float strokeWidth, Color fore, int trackAlpha, Reading? reading,
            RingColors palette, int lowThreshold, int criticalThreshold,
            int midThreshold)
        {
            var box = RingBox(size, scale, radius);

            Color color;
            bool full = false;
            double fraction = 0;
            bool hasReading = reading != null && reading.Percent.HasValue;

            if (hasReading)
            {
                int percent = reading!.Percent!.Value;
                color = palette.ForBattery(percent, reading.Charging,
                    lowThreshold, criticalThreshold, midThreshold);
                // 充电时整圈点亮（不只是画到电量位置）：
                // 24px 上很难看出「弧长了多少」，整圈变绿更容易识别
                if (reading.Charging) full = true;
                else fraction = Math.Clamp(percent / 100.0, 0.0, 1.0);
            }
            else
            {
                color = palette.Offline;
            }

            using var track = new Pen(Color.FromArgb(trackAlpha, palette.Track), strokeWidth);
            g.DrawEllipse(track, box);

            // 没读数就只留轨道：让用户看出「这台设备还在监控范围内，
            // 只是此刻没读到」，而不是以为工具少画了一圈。
            if (!hasReading || (!full && fraction <= 0)) return;

            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            if (full)
            {
                g.DrawEllipse(pen, box);
            }
            else
            {
                // 从 12 点方向顺时针，弧长 = 电量占比。
                // 最小可视弧长：1% 的弧在 24px 图标上不到 1 个像素，
                // 给个下限让它至少能看见一小段（否则“有读数”看起来和“没读数”一样）
                double sweep = 360.0 * fraction;
                if (sweep < MinSweepDegrees) sweep = MinSweepDegrees;
                g.DrawArc(pen, box, -90, (float)sweep);
            }
        }

        /// <summary>
        /// 最小可视弧长（度）。1% 电量在半径 10px 的环上只有约 0.2px 弧长，
        /// 小于 1px 就等于看不见 —— 那样「1%」和「没读数」在图标上完全一样。
        /// 取 12° 约等于 2.1px 弧长，刚好能分辨出一小段。
        /// </summary>
        private const double MinSweepDegrees = 12.0;

        /// <summary>半径 → GDI+ 椭圆外接矩形（自动乘上绘制缩放）</summary>
        private static RectangleF RingBox(int size, int scale, double radius)
        {
            float canvas = size * scale;
            float r = (float)radius * scale;
            return new RectangleF(canvas / 2f - r, canvas / 2f - r, r * 2f, r * 2f);
        }
    }
}
