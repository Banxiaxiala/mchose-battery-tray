// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [离线预览] 本文件只用于「不出家门看卡片长什么样」：用合成数据渲染
//    卡片并截屏，方便调视觉时快速迭代，不必等真机历史积累。
// 2. [不进产物] 只编译进 Debug 构建；发布版不需要它。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeripheralBattery.Models;
using PeripheralBattery.Services;
using PeripheralBattery.UI;

namespace PeripheralBattery.Tools
{
    internal static class CardPreview
    {
        public static void Run(string outputPath, string style = "bar",
            int rangeHours = 24, bool light = false, int width = 0)
        {
            double now = TimeText.UnixNow();

            // 鼠标：24 小时里每小时掉 1.6%，当前 51%
            var mouseSamples = new List<Sample>();
            for (int i = 24; i >= 0; i--)
            {
                mouseSamples.Add(new Sample(now - i * 3600, 51 + (int)Math.Round(i * 1.6), false));
            }
            var mouseAnalysis = HistoryAnalysis.Analyze(mouseSamples, 51, false, now,
                mouseSamples[^1]);
            mouseAnalysis.Forecast = HistoryAnalysis.ForecastOf(mouseSamples, 51, false, now);

            // 耳机：8 小时前开始离线，历史停在 73%
            var headsetSamples = new List<Sample>();
            for (int i = 10; i >= 3; i--)
            {
                headsetSamples.Add(new Sample(now - i * 3600, 70 + i, false));
            }
            var headsetAnalysis = HistoryAnalysis.Analyze(headsetSamples, null, false, now,
                headsetSamples[^1]);

            // 键盘：缓慢下滑，当前 68%
            var keyboardSamples = new List<Sample>();
            for (int i = 20; i >= 0; i--)
            {
                keyboardSamples.Add(new Sample(now - i * 3600, 68 + i / 2, false));
            }
            var keyboardAnalysis = HistoryAnalysis.Analyze(keyboardSamples, 68, false, now,
                keyboardSamples[^1]);

            // 折线图数据：用与分析同源的样本重采样，保证两种样式看到的是同一份历史
            List<ChartPoint> PointsOf(List<Sample> source, int hours, out string from,
                out string to, out double fromT, out double toT)
            {
                double window = hours * 3600.0;
                fromT = now - window;
                toT = now;
                var folded = HistoryAnalysis.Fold(source, HistoryAnalysis.HourSpan);
                var points = HistoryAnalysis.SamplePoints(
                    folded, fromT, toT, 60);
                var start = TimeText.FromUnix(now - window);
                var end = TimeText.FromUnix(now);
                from = hours <= 24 ? start.ToString("HH:mm") : start.ToString("yyyy-MM-dd");
                to = hours <= 24 ? "现在" : end.ToString("MM-dd");
                return points;
            }

            // 柱状图数据：按 time range 分桶，与折线共用同一份历史。
            // 柱状图也必须跟着时间选择变 —— 只给 24 小时的话，
            // 用户选 30 天会发现柱状图毫无变化。
            (int min, int max, int active) ProbeStats(List<Sample> source, int hours)
            {
                var folded = HistoryAnalysis.Fold(source, HistoryAnalysis.HourSpan);
                return HistoryAnalysis.RangeStats(source, folded, folded, now, hours * 3600.0);
            }

            List<Bucket> BucketsOf(List<Sample> source, int hours)
            {
                return HistoryAnalysis.BuildBuckets(source, now, hours, null, -1);
            }

            var devices = new List<CardDevice>
            {
                new()
                {
                    Label = "鼠标",
                    Reading = new Reading
                    {
                        Name = "PRO X 2", Percent = 51, Charging = false,
                        Connected = true, Status = "放电中", Transport = "HID++",
                    },
                    Analysis = mouseAnalysis,
                    Points = PointsOf(mouseSamples, rangeHours,
                        out string f_mouseSamples, out string t_mouseSamples,
                        out double ft_mouseSamples, out double tt_mouseSamples),
                    RangeFrom = f_mouseSamples,
                    RangeTo = t_mouseSamples,
                    FromUnix = ft_mouseSamples,
                    Buckets = BucketsOf(mouseSamples, rangeHours),
                    RangeMin = ProbeStats(mouseSamples, rangeHours).min,
                    RangeMax = ProbeStats(mouseSamples, rangeHours).max,
                    RangeActiveHours = ProbeStats(mouseSamples, rangeHours).active,
                    ToUnix = tt_mouseSamples,
                },
                new()
                {
                    Label = "耳机",
                    Reading = new Reading
                    {
                        Name = "BlackShark V2 Pro", Percent = null, Charging = false,
                        Connected = false, Status = "", Transport = "PA-PI",
                    },
                    Analysis = headsetAnalysis,
                    Points = PointsOf(headsetSamples, rangeHours,
                        out string f_headsetSamples, out string t_headsetSamples,
                        out double ft_headsetSamples, out double tt_headsetSamples),
                    RangeFrom = f_headsetSamples,
                    RangeTo = t_headsetSamples,
                    FromUnix = ft_headsetSamples,
                    Buckets = BucketsOf(headsetSamples, rangeHours),
                    RangeMin = ProbeStats(headsetSamples, rangeHours).min,
                    RangeMax = ProbeStats(headsetSamples, rangeHours).max,
                    RangeActiveHours = ProbeStats(headsetSamples, rangeHours).active,
                    ToUnix = tt_headsetSamples,
                },
                new()
                {
                    Label = "键盘",
                    Reading = new Reading
                    {
                        Name = "维咖 MAX102", Percent = 68, Charging = false,
                        Connected = true, Status = "已连接", Transport = "VIA",
                    },
                    Analysis = keyboardAnalysis,
                    Points = PointsOf(keyboardSamples, rangeHours,
                        out string f_keyboardSamples, out string t_keyboardSamples,
                        out double ft_keyboardSamples, out double tt_keyboardSamples),
                    RangeFrom = f_keyboardSamples,
                    RangeTo = t_keyboardSamples,
                    FromUnix = ft_keyboardSamples,
                    Buckets = BucketsOf(keyboardSamples, rangeHours),
                    RangeMin = ProbeStats(keyboardSamples, rangeHours).min,
                    RangeMax = ProbeStats(keyboardSamples, rangeHours).max,
                    RangeActiveHours = ProbeStats(keyboardSamples, rangeHours).active,
                    ToUnix = tt_keyboardSamples,
                },
            };

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["PreviewMode"] = true;

            // 浅色主题用于 README 的对照图。必须在建控件之前设置：
            // Palette 的刷子是按当前主题缓存的，建完再改不会重绘。
            if (light) Theme.ForceDark = false;

            var card = new BatteryCard();
            card.ApplyChartSettings(style, rangeHours);
            if (width > 0) card.ApplyMetrics(width, 54, 0);
            card.Render(devices);
            card.Anchor = new Point(2300, 1400);
            card.Show();

            // 等布局与动画稳定后再截图
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    Save(card, outputPath);
                    Console.WriteLine("已保存：" + outputPath);
                }
                catch (Exception exc)
                {
                    Console.WriteLine("截图失败：" + exc.Message);
                }
                app.Shutdown();
            };
            timer.Start();
            app.Run();
        }

        /// <summary>
        /// 截图倍率。1 DIP = 3 像素。
        ///
        /// 为什么不是 1x：卡片只有 288 DIP 宽，README 里会被放大到 260~300 px
        /// 显示，1x 位图在 4K 屏或网页缩放下明显发糊。3x（864 px 宽）在任何
        /// 常见显示倍率下都够用，同时文件仍在 100 KB 以内。
        ///
        /// 用 RenderTargetBitmap 的 dpi 参数而不是事后 resize：
        /// 前者让 WPF 真的按高分辨率布局与栅格化（文字是清晰的新渲染），
        /// 后者只是把已有的位图插值放大，模糊无法挽回。
        /// </summary>
        private const int RenderScale = 3;

        private static void Save(Window window, string path)
        {
            window.UpdateLayout();
            int width = (int)Math.Ceiling(window.ActualWidth);
            int height = (int)Math.Ceiling(window.ActualHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("窗口尺寸为 0");

            // 必须渲染窗口的【内容元素】，不能用 VisualBrush(window)：
            // Window 本身是一个顶层宿主，它的客户区由 DWM 组合，
            // VisualBrush 把它画到 DrawingVisual 里只会得到一片空白。
            var content = window.Content as FrameworkElement
                ?? throw new InvalidOperationException("窗口没有内容");
            content.UpdateLayout();

            int pixelWidth = width * RenderScale;
            int pixelHeight = height * RenderScale;

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 画一层背景，方便看透明/亚克力区域。
                // 必须跟随主题：这个底板是预览专用（真实运行时是 DWM 亚克力），
                // 深色底板配浅色文字会看不清。
                Color backdrop = Theme.IsDark
                    ? Color.FromRgb(0x20, 0x24, 0x2A)
                    : Color.FromRgb(0xF3, 0xF4, 0xF6);

                // 坐标系仍是 DIP（width/height），dpi 由 RenderTargetBitmap 放大
                context.DrawRectangle(new SolidColorBrush(backdrop),
                    null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(content), null,
                    new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
