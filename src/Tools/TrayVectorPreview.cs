// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [复用几何，不复制参数] 本文件从 RingGeometry.Default 读半径与描边，
//    绝不在这里另写一套数字。否则改了 TrayIconArt 而忘记改这里，
//    README 上的图就和真实图标不一致 —— 那种图比没有图更糟。
// 2. [真矢量] 输出 SVG 的 <circle> / <path d="A ...">，不是位图。
//    GitHub 用 <img> 引用 SVG 时由浏览器矢量渲染，放大到任何尺寸都不糊。
// 3. [固定深色底] GitHub 的 camo 代理不会给 SVG 下发 prefers-color-scheme，
//    所以不用媒体查询，固定一套配色，保证在深浅两种网页背景下都能看清。
// 4. [只用于预览] 不进产物；发布版不需要它。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PeripheralBattery.Models;
using PeripheralBattery.UI;

namespace PeripheralBattery.Tools
{
    /// <summary>
    /// 把托盘图标输出成矢量 SVG（README 用）。
    ///
    /// 为什么值得单独做：托盘图标只有 24px，README 里必须放大才能看清三圈，
    /// 而位图放大必糊。图标本身是纯几何（圆 + 圆弧），天生适合矢量。
    /// </summary>
    internal static class TrayVectorPreview
    {
        /// <summary>单格宽度（CSS px）</summary>
        private const int CellWidth = 220;

        /// <summary>图标在格子里的绘制尺寸</summary>
        private const int IconSize = 132;

        private sealed class Case
        {
            public string Title = "";
            public string Note = "";
            public Reading?[]? Rings;
        }

        public static void Run(string outputPath, bool light = false)
        {
            var cases = new List<Case>
            {
                new()
                {
                    Title = "全部正常",
                    Note = "三圈按电量描弧",
                    Rings = new[] { Make(82), Make(55), Make(74) },
                },
                new()
                {
                    Title = "低电量",
                    Note = "≤30% 黄，≤10% 红",
                    Rings = new[] { Make(24), Make(8), Make(62) },
                },
                new()
                {
                    Title = "充电中",
                    Note = "整圈点亮变绿",
                    Rings = new[] { Make(47, true), Make(90, true), Make(30) },
                },
                new()
                {
                    Title = "设备离线",
                    Note = "离线只留淡轨道",
                    Rings = new[] { Make(66), Offline(), Make(41) },
                },
                new()
                {
                    Title = "关闭一台",
                    Note = "环位自动补位，不空洞",
                    Rings = new[] { Make(66), Make(41) },
                },
            };

            int cols = cases.Count;
            int width = cols * CellWidth;
            int headerHeight = 46;
            int iconTop = headerHeight + 10;
            int smallTop = iconTop + IconSize + 18;
            int titleTop = smallTop + 34;
            int height = titleTop + 54;

            string background = light ? "#f3f3f3" : "#202020";
            string foreground = light ? "#1a1a1a" : "#ffffff";
            string muted = light ? "#6a6a6a" : "#9a9a9a";

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" " +
                $"height=\"{height}\" viewBox=\"0 0 {width} {height}\" " +
                "font-family=\"'Microsoft YaHei UI','PingFang SC','Hiragino Sans GB'," +
                "'Segoe UI',sans-serif\">");
            sb.AppendLine($"<rect width=\"{width}\" height=\"{height}\" fill=\"{background}\"/>");
            sb.AppendLine($"<text x=\"16\" y=\"30\" font-size=\"18\" font-weight=\"600\" " +
                $"fill=\"{foreground}\">托盘图标 · {(light ? "浅色" : "深色")}任务栏</text>");

            for (int i = 0; i < cols; i++)
            {
                var item = cases[i];
                double cellX = i * CellWidth;
                double iconX = cellX + (CellWidth - IconSize) / 2.0;

                // 放大图
                sb.AppendLine($"<g transform=\"translate({N(iconX)},{N(iconTop)})\">");
                AppendIcon(sb, item.Rings, foreground, IconSize);
                sb.AppendLine("</g>");

                // 1:1 实际尺寸（24px），用来验证任务栏上的真实观感
                sb.AppendLine($"<g transform=\"translate({N(cellX + 16)},{N(smallTop)})\">");
                AppendIcon(sb, item.Rings, foreground, 24);
                sb.AppendLine("</g>");
                sb.AppendLine($"<text x=\"{N(cellX + 48)}\" y=\"{N(smallTop + 17)}\" " +
                    $"font-size=\"12\" fill=\"{muted}\">1:1 实际尺寸</text>");

                sb.AppendLine($"<text x=\"{N(cellX + CellWidth / 2.0)}\" y=\"{titleTop}\" " +
                    "font-size=\"15\" font-weight=\"600\" text-anchor=\"middle\" " +
                    $"fill=\"{foreground}\">{Esc(item.Title)}</text>");
                sb.AppendLine($"<text x=\"{N(cellX + CellWidth / 2.0)}\" y=\"{titleTop + 22}\" " +
                    $"font-size=\"12\" text-anchor=\"middle\" fill=\"{muted}\">" +
                    $"{Esc(item.Note)}</text>");
            }

            sb.AppendLine("</svg>");

            string dir = Path.GetDirectoryName(outputPath) ?? ".";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            // 不写 BOM：GitHub 上的 XML 解析器对 BOM 处理不一致，纯 UTF-8 最稳
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 画一个多环图标。几何参数全部来自 TrayIconArt.Default，
        /// 与真实托盘图标共用同一份定义。
        /// </summary>
        private static void AppendIcon(StringBuilder sb, Reading?[]? rings,
            string foreColor, int pixelSize)
        {
            var geometry = TrayIconArt.Default;
            var palette = RingColors.Default;

            // 以 24px 为基准换算：几何参数是按 24px 反推的
            double factor = pixelSize / 24.0;
            double center = pixelSize / 2.0;

            for (int i = 0; i < geometry.Radii.Length; i++)
            {
                var reading = TrayIconArt.RingAt(rings, i);
                double radius = geometry.Radii[i] * factor;
                double stroke = geometry.Strokes[i] * factor;

                // 轨道：始终画，让用户看出「这台设备还在监控范围内」
                sb.AppendLine($"<circle cx=\"{N(center)}\" cy=\"{N(center)}\" " +
                    $"r=\"{N(radius)}\" fill=\"none\" " +
                    $"stroke=\"{foreColor}\" stroke-opacity=\"" +
                    $"{N(geometry.TrackAlpha / 255.0, 3)}\" " +
                    $"stroke-width=\"{N(stroke)}\"/>");

                bool hasReading = reading != null && reading.Percent.HasValue;
                if (!hasReading) continue;

                int percent = reading!.Percent!.Value;
                bool charging = reading.Charging;
                // 阈值取默认（30/10/55），与真实托盘一致。
                // 不传自定义阈值是刻意的：这是 README 图，展示默认观感。
                string color = Hex(palette.ForBattery(percent, charging,
                    lowThreshold: 30, criticalThreshold: 10, midThreshold: 55));
                double sweep = charging ? 360.0 : Math.Clamp(percent / 100.0, 0, 1) * 360.0;

                // 与 TrayIconArt 保持一致：太短的弧看不见，给个下限
                if (!charging && sweep < 12.0) sweep = 12.0;

                if (sweep >= 359.99)
                {
                    sb.AppendLine($"<circle cx=\"{N(center)}\" cy=\"{N(center)}\" " +
                        $"r=\"{N(radius)}\" fill=\"none\" stroke=\"{color}\" " +
                        $"stroke-width=\"{N(stroke)}\" stroke-linecap=\"round\"/>");
                    continue;
                }

                // 从 12 点方向顺时针画弧。
                // SVG 的 y 轴向下，所以「-90 度」是正上方；顺时针用 sweep-flag=1。
                double angle = -90.0 * Math.PI / 180.0;
                double end = angle + sweep * Math.PI / 180.0;
                double x1 = center + radius * Math.Cos(angle);
                double y1 = center + radius * Math.Sin(angle);
                double x2 = center + radius * Math.Cos(end);
                double y2 = center + radius * Math.Sin(end);
                int largeArc = sweep > 180.0 ? 1 : 0;

                sb.AppendLine($"<path d=\"M {N(x1)} {N(y1)} " +
                    $"A {N(radius)} {N(radius)} 0 {largeArc} 1 {N(x2)} {N(y2)}\" " +
                    $"fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(stroke)}\" " +
                    "stroke-linecap=\"round\"/>");
            }
        }

        private static Reading Make(int percent, bool charging = false)
        {
            return new Reading
            {
                Percent = percent,
                Charging = charging,
                Connected = true,
            };
        }

        private static Reading Offline()
        {
            return new Reading { Percent = null, Connected = false };
        }

        private static string Hex(System.Drawing.Color color)
        {
            return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        }

        private static string N(double value, int decimals = 2)
        {
            return value.ToString("0." + new string('#', decimals),
                CultureInfo.InvariantCulture);
        }

        private static string Esc(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
