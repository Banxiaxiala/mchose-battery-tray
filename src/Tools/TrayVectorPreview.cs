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
                    Title = "只有一台",
                    Note = "单环，占满图标",
                    Rings = new[] { Make(82) },
                },
                new()
                {
                    Title = "两台",
                    Note = "两环均匀分布",
                    Rings = new[] { Make(82), Make(35) },
                },
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
        /// 生成「任务栏实景」图：一行模拟任务栏，托盘图标排在右端。
        ///
        /// 与状态对比图的分工：
        ///   对比图（Run）     —— 讲清每种状态长什么样，每格带说明文字
        ///   任务栏图（RunBar）—— 只展示真实观感，用来当 README 首图
        ///
        /// 首图必须「一眼看出这是什么」，所以画成任务栏而不是九宫格说明图。
        /// 圆角、系统图标位、时钟都能帮助读者立刻认出这是托盘区域。
        /// </summary>
        public static void RunBar(string outputPath, bool light = false)
        {
            const int width = 680;
            const int iconSize = 24;
            const int barHeight = 72;
            const int zoom = 5;              // 特写放大倍数
            const int legendBand = 212;      // 特写区高度（5 倍特写 120 + 标题与留白）
            const int height = barHeight + legendBand;

            // 颜色按主题走。Win11 的浅色主题任务栏是「白毛玻璃」：
            // 近白半透明底 + 极淡的底部阴影感。SVG 没有模糊滤镜，
            // 用两层近似：近白主体 + 顶部一条更亮的 1px 边。
            //
            // ⚠️ 之前这里被我写死成深色，理由是「深色在浅色页面更醒目」——
            // 但那样就看不出浅色主题下长什么样了，而且白毛玻璃本身就是
            // Win11 默认主题的观感。现在跟随 light 参数。
            string barColor = light ? "#F5F5F5" : "#2B2B2B";
            string border = light ? "#FFFFFF" : "#3D3D3D";
            string text = light ? "#1A1A1A" : "#FFFFFF";
            string muted = light ? "#5C5C5C" : "#C9C9C9";

            // 特写区底板：与任务栏形成对比（深色页面用深灰，浅色页面用白）
            string zoomBackdrop = light ? "#FFFFFF" : "#1F1F1F";
            string zoomBorder = light ? "#E5E5E5" : "#3A3A3A";
            // ⚠️ 图例文字画在**页面背景**上（不是底板里），而这张图无论
            // 深色还是浅色任务栏，最终都放在 README 的浅色页面上。
            // 所以图例文字必须始终用深色 —— 之前深色任务栏那版用了
            // 浅色文字（#E6E6E6），结果在白底上完全看不见。
            string zoomCaption = "#57606A";
            string legendText = "#1F2328";

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" " +
                $"height=\"{height}\" viewBox=\"0 0 {width} {height}\" " +
                "font-family=\"'Segoe UI','Microsoft YaHei UI',sans-serif\">");

            // 任务栏底板 + 顶部高光（只占上半部分，下半留给特写）
            sb.AppendLine($"<rect width=\"{width}\" height=\"{barHeight}\" fill=\"{barColor}\"/>");
            sb.AppendLine($"<rect x=\"0\" y=\"0\" width=\"{width}\" height=\"1\" " +
                $"fill=\"{border}\" fill-opacity=\"0.9\"/>");

            double centerY = barHeight / 2.0;

            // 居中的图标组 —— 「图标居中」是 Win11 与 Win10 最明显的区别。
            // 三个抽象图形：开始（四格）、搜索（放大镜）、任务视图（套叠方块）。
            double appStart = width / 2.0 - 52;
            double appY = centerY - 10;

            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    sb.AppendLine($"<rect x=\"{N(appStart + i * 11)}\" y=\"{N(appY + j * 11)}\" " +
                        "width=\"9\" height=\"9\" rx=\"1.5\" " +
                        $"fill=\"{(light ? "#0067C0" : "#60CDFF")}\"/>");
                }
            }

            double sx = appStart + 42, sy = appY + 10;
            sb.AppendLine($"<circle cx=\"{N(sx)}\" cy=\"{N(sy)}\" r=\"7\" " +
                $"fill=\"none\" stroke=\"{muted}\" stroke-width=\"1.8\"/>");
            sb.AppendLine($"<path d=\"M {N(sx + 5.5)} {N(sy + 5.5)} l 5 5\" " +
                $"stroke=\"{muted}\" stroke-width=\"1.8\" stroke-linecap=\"round\"/>");

            double vx = appStart + 82, vy = appY + 1;
            sb.AppendLine($"<rect x=\"{N(vx)}\" y=\"{N(vy + 3)}\" width=\"17\" height=\"15\" " +
                $"rx=\"2.5\" fill=\"none\" stroke=\"{muted}\" stroke-width=\"1.6\"/>");
            sb.AppendLine($"<rect x=\"{N(vx + 4)}\" y=\"{N(vy)}\" width=\"17\" height=\"15\" " +
                $"rx=\"2.5\" fill=\"none\" stroke=\"{muted}\" stroke-width=\"1.6\" " +
                "stroke-opacity=\"0.55\"/>");

            // 右端：系统图标位（网络 / 音量 / 电池的抽象形状，不画细节）
            double rightEdge = width - 20;

            // 时钟：两行右对齐（Win11 的排法）
            sb.AppendLine($"<text x=\"{N(rightEdge)}\" y=\"{N(centerY - 2)}\" " +
                $"font-size=\"11.5\" fill=\"{text}\" text-anchor=\"end\">14:20</text>");
            sb.AppendLine($"<text x=\"{N(rightEdge)}\" y=\"{N(centerY + 12)}\" " +
                $"font-size=\"11\" fill=\"{muted}\" text-anchor=\"end\">2026/11/13</text>");

            // 本程序的托盘图标（三个三环，代表三台设备都在）
            var rings = new[]
            {
                Make(82), Make(51), Make(24),
            };
            double iconX = rightEdge - 104;
            sb.AppendLine($"<g transform=\"translate({N(iconX)},{N(centerY - iconSize / 2.0)})\">");
            AppendIcon(sb, rings, text, iconSize);
            sb.AppendLine("</g>");

            // 展开箭头（系统托盘里的「^」）
            sb.AppendLine($"<path d=\"M {N(iconX + 42)} {N(centerY - 3)} " +
                $"l 4.5 4.5 l 4.5 -4.5\" fill=\"none\" stroke=\"{muted}\" " +
                "stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");

            // ---- 下半：放大特写 ----
            //
            // 这一半是画在 **README 的页面背景** 上的（不是任务栏里），
            // 所以配色必须按「页面是浅色」来定：深色文字 + 浅色底板。
            // 之前这里错用了任务栏的白色文字，放进 README 后图例完全看不见。
            double zoomSize = iconSize * zoom;
            double zoomX = 44;
            double zoomY = barHeight + 56;

            sb.AppendLine($"<text x=\"{N(zoomX)}\" y=\"{N(barHeight + 30)}\" " +
                $"font-size=\"12\" fill=\"{zoomCaption}\">" +
                $"托盘图标 · 放大 {zoom} 倍</text>");

            // 特写底板：浅色圆角块，与深色任务栏形成对比
            sb.AppendLine($"<rect x=\"{N(zoomX - 12)}\" y=\"{N(zoomY - 12)}\" " +
                $"width=\"{N(zoomSize + 24)}\" height=\"{N(zoomSize + 24)}\" rx=\"10\" " +
                $"fill=\"{zoomBackdrop}\" stroke=\"{zoomBorder}\"/>");

            sb.AppendLine($"<g transform=\"translate({N(zoomX)},{N(zoomY)})\">");
            // 特写图标的线条颜色要跟着底板：深底白线，白底深线
            AppendIcon(sb, rings, light ? "#1A1A1A" : "#FFFFFF", (int)zoomSize);
            sb.AppendLine("</g>");

            // 图例：深色文字，画在页面背景上
            double legendX = zoomX + zoomSize + 34;
            double legendY = zoomY + 26;
            string[] legendLines = { "外环  鼠标", "中环  耳机", "内环  键盘" };
            for (int i = 0; i < legendLines.Length; i++)
            {
                sb.AppendLine($"<text x=\"{N(legendX)}\" y=\"{N(legendY + i * 24)}\" " +
                    $"font-size=\"13\" fill=\"{legendText}\">" +
                    $"{Esc(legendLines[i])}</text>");
            }
            sb.AppendLine($"<text x=\"{N(legendX)}\" y=\"{N(legendY + legendLines.Length * 24 + 10)}\" " +
                $"font-size=\"12\" fill=\"{zoomCaption}\">" +
                "弧长 = 电量百分比 · 充电时整圈点亮</text>");

            sb.AppendLine("</svg>");

            string dir = Path.GetDirectoryName(outputPath) ?? ".";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 画一个多环图标。几何参数全部来自 TrayIconArt.Default，
        /// 与真实托盘图标共用同一份定义。
        /// </summary>
        private static void AppendIcon(StringBuilder sb, Reading?[]? rings,
            string foreColor, int pixelSize)
        {
            // 按**实际环数**取几何：一台设备就画一圈，不要留空轨道。
            // 与 TrayIconArt.Render 的行为保持一致。
            int ringCount = 0;
            if (rings != null)
            {
                foreach (var r in rings) if (r != null) ringCount++;
            }
            if (ringCount == 0) ringCount = 1;
            var geometry = RingGeometry.ForCount(ringCount);
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
