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
        public static void Run(string outputPath)
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
                },
            };

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["PreviewMode"] = true;

            var card = new BatteryCard();
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

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 画一层背景，方便看透明/亚克力区域
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2A)),
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
