// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [自绘省内存] 24 根柱用 OnRender 现画，不用 24 个 Border。
//    WPF 每个 Border 都是一个视觉树节点，24 个 × 多台设备会明显拖慢首帧。
// 2. [休眠柱必须可区分] 没有样本的小时沿用上一个已知值，但要以半透明 +
//    更矮的样式画出来，否则「设备关机了 8 小时」会被误读成「电量一直很稳」。
// 3. [低电量区间抬升基线] 当整段区间落在 20~45% 时，从 0 起算会把所有柱子
//    画成齐平的一条线，看不出下降趋势。此时把基线抬到 min-15。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PeripheralBattery.Models;

namespace PeripheralBattery.UI
{
    /// <summary>24 小时电量柱状图</summary>
    public sealed class SparkChart : FrameworkElement
    {
        private List<Bucket> _buckets = new();
        private bool _dark = true;
        private string _leftLabel = "-24h";
        private string _rightLabel = "现在";

        /// <summary>柱宽。柱数多时会自动收窄，免得在窄卡片里溢出</summary>
        private const double DefaultBarWidth = 6.5;
        private const double MinBarWidth = 2.0;
        private const double DefaultGap = 2.5;
        private const double MinGap = 0.8;

        /// <summary>底部刻度带高度（容纳「-24h」这类时间标签）</summary>
        private const double ScaleBand = 15;

        /// <summary>鼠标指向的柱子索引；-1 = 没悬停</summary>
        private int _hoverIndex = -1;

        // OnRender 里算出的布局，供 MouseMove 命中用（避免重复计算）
        private double _barPitch;      // 每根柱占的宽度（柱宽 + 间隙）
        private double _firstBarX;
        private double _barWidth;
        private double _plotBottom;

        public SparkChart()
        {
            // FrameworkElement 默认对「无背景的绘制」不参与命中测试，
            // 不打开这个选项收不到 MouseMove。
            IsHitTestVisible = true;
            MouseMove += OnMouseMove;
            MouseLeave += (_, _) =>
            {
                if (_hoverIndex < 0) return;
                _hoverIndex = -1;
                InvalidateVisual();
            };
        }

        /// <summary>
        /// 按横向比例触发悬停。仅供 --preview 与视觉回归使用。
        /// 找不到有数据的柱子就什么也不做（不弹空气泡）。
        /// </summary>
        public void HoverAtRatio(double ratio)
        {
            if (_buckets.Count == 0 || _barPitch <= 0) return;
            double x = _firstBarX + Math.Clamp(ratio, 0, 0.999) * _barPitch * _buckets.Count;
            int index = (int)Math.Floor((x - _firstBarX) / _barPitch);
            _hoverIndex = index >= 0 && index < _buckets.Count && _buckets[index].HasData
                ? index
                : -1;
            InvalidateVisual();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_buckets.Count == 0 || _barPitch <= 0) return;

            double x = e.GetPosition(this).X;
            int index = (int)Math.Floor((x - _firstBarX) / _barPitch);

            // 只认有数据的柱子：空档弹出「--」反而干扰阅读
            int found = -1;
            if (index >= 0 && index < _buckets.Count && _buckets[index].HasData)
            {
                found = index;
            }

            if (found == _hoverIndex) return;
            _hoverIndex = found;
            InvalidateVisual();
        }

        public void SetData(List<Bucket> buckets, bool dark,
            string leftLabel = "", string rightLabel = "")
        {
            _buckets = buckets ?? new List<Bucket>();
            _dark = dark;
            _leftLabel = string.IsNullOrEmpty(leftLabel) ? "-24h" : leftLabel;
            _rightLabel = string.IsNullOrEmpty(rightLabel) ? "现在" : rightLabel;
            // 数据换了，旧的悬停索引已经不对应任何柱子
            _hoverIndex = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 1 || height <= 1) return;

            var textFaint = Palette.BrushOf(_dark
                ? Palette.TextFaintDark
                : Palette.TextFaintLight);

            if (_buckets.Count == 0)
            {
                var label = MakeText("暂无历史记录", 10, textFaint);
                dc.DrawText(label, new Point(0, Math.Max(0, (height - label.Height) / 2)));
                return;
            }

            double baseline = height - ScaleBand;
            double chartHeight = Math.Max(10, baseline - 2);

            // ---- 基线：低电量区间抬升，放大差异 ----
            int min = int.MaxValue, max = int.MinValue;
            foreach (var bucket in _buckets)
            {
                if (bucket.Percent <= 0) continue;
                if (bucket.Percent < min) min = bucket.Percent;
                if (bucket.Percent > max) max = bucket.Percent;
            }
            if (min == int.MaxValue) { min = 0; max = 0; }

            int baseLineValue = (max - min <= 25 && min >= 20) ? Math.Max(0, min - 15) : 0;
            double span = Math.Max(1, 100 - baseLineValue);

            // ---- 先画一条极淡的参考底线，让空网格不至于完全悬空 ----
            var guidePen = new Pen(Palette.BrushOf(Color.FromArgb(
                _dark ? (byte)0x22 : (byte)0x18, 0x80, 0x90, 0xA0)), 1);
            guidePen.Freeze();
            dc.DrawLine(guidePen, new Point(0, baseline), new Point(width, baseline));

            // ---- 柱子 ----
            // 柱宽按可用宽度自适应：24 根时用默认 6.5px，
            // 如果将来把柱数调大（或卡片变窄），按比例收窄而不是画到溢出。
            double barWidth = DefaultBarWidth;
            double gap = DefaultGap;
            if (_buckets.Count > 0)
            {
                double available = width - 4;
                double ideal = available / _buckets.Count - DefaultGap;
                if (ideal < DefaultBarWidth)
                {
                    barWidth = Math.Max(MinBarWidth, ideal);
                    gap = Math.Max(MinGap, (available / _buckets.Count) - barWidth);
                }
            }

            double totalWidth = _buckets.Count * (barWidth + gap) - gap;
            double x = Math.Max(0, (width - totalWidth) / 2);

            // 缓存给 MouseMove 命中用
            _barPitch = barWidth + gap;
            _firstBarX = x;
            _barWidth = barWidth;
            _plotBottom = baseline;

            foreach (var bucket in _buckets)
            {
                if (bucket.HasData)
                {
                    double norm = Math.Clamp((bucket.Percent - baseLineValue) / span, 0.10, 1.0);
                    double barHeight = Math.Clamp(chartHeight * norm, 3.0, chartHeight);

                    Color color = Palette.ForBattery(bucket.Percent, false);
                    double alpha = bucket.Sleeping ? 0.38 : 1.0;

                    var brush = new SolidColorBrush(Color.FromArgb(
                        (byte)(alpha * 255), color.R, color.G, color.B));
                    brush.Freeze();

                    // 顶部圆角、底部方角：用两个矩形拼出胶囊上半部分
                    double radius = Math.Min(barWidth / 2, barHeight / 2);
                    var rect = new Rect(x, baseline - barHeight, barWidth, barHeight);
                    if (barHeight > radius * 2)
                    {
                        dc.DrawRoundedRectangle(brush, null,
                            new Rect(rect.X, rect.Y, rect.Width, radius * 2), radius, radius);
                        dc.DrawRectangle(brush, null,
                            new Rect(rect.X, rect.Y + radius, rect.Width, rect.Height - radius));
                    }
                    else
                    {
                        dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
                    }
                }
                else
                {
                    // 无数据：画一条 2px 的横线占位
                    var empty = new SolidColorBrush(Color.FromArgb(
                        _dark ? (byte)0x40 : (byte)0x30, 0x80, 0x90, 0xA0));
                    empty.Freeze();
                    dc.DrawRectangle(empty, null,
                        new Rect(x, baseline - 2, barWidth, 2));
                }
                x += barWidth + gap;
            }

            // ---- 底部时间刻度 ----
            var left = MakeText(_leftLabel, 9, textFaint);
            dc.DrawText(left, new Point(0, baseline + 3));

            var right = MakeText(_rightLabel, 9, textFaint);
            dc.DrawText(right, new Point(width - right.Width, baseline + 3));

            // ---- 悬停提示：最后画，压在所有元素之上 ----
            DrawHover(dc, width, baseline, baseLineValue, span, chartHeight);
        }

        /// <summary>
        /// 画悬停提示：高亮该柱 + 数值气泡。
        ///
        /// 气泡会做边界翻转 —— 靠近右边缘时画在柱子左侧，
        /// 否则最右侧几根柱子的数值永远被裁掉。
        /// </summary>
        private void DrawHover(DrawingContext dc, double width, double baseline,
            int baseLineValue, double span, double chartHeight)
        {
            if (_hoverIndex < 0 || _hoverIndex >= _buckets.Count) return;
            var bucket = _buckets[_hoverIndex];
            if (!bucket.HasData) return;

            double barX = _firstBarX + _hoverIndex * _barPitch;
            double centerX = barX + _barWidth / 2;

            // 高亮：整列加一层极淡的底，比给柱子描边更不干扰配色
            var highlight = new SolidColorBrush(Color.FromArgb(
                _dark ? (byte)0x1E : (byte)0x14, 0x80, 0x90, 0xA0));
            highlight.Freeze();
            dc.DrawRectangle(highlight, null,
                new Rect(barX - _barPitch * 0.4, 2,
                    _barPitch * 0.8 + _barWidth * 0.4, baseline - 2));

            // 气泡内容：电量 + 时间刻度（桶的 Hour 就是该柱的时间）
            string caption = bucket.Percent + "%";
            if (!string.IsNullOrEmpty(bucket.Hour)) caption += "　" + bucket.Hour;

            var text = MakeText(caption, 9.5, Palette.BrushOf(_dark
                ? Palette.TextPrimaryDark : Palette.TextPrimaryLight));

            double padX = 5, padY = 2.5;
            double boxWidth = text.Width + padX * 2;
            double boxHeight = text.Height + padY * 2;

            double boxLeft = centerX - boxWidth / 2;
            if (boxLeft < 2) boxLeft = 2;
            if (boxLeft + boxWidth > width - 2) boxLeft = width - 2 - boxWidth;

            double boxTop = 2;

            var boxBrush = new SolidColorBrush(_dark
                ? Color.FromArgb(0xF0, 0x1C, 0x21, 0x28)
                : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            boxBrush.Freeze();
            var boxPen = new Pen(Palette.BrushOf(Color.FromArgb(
                _dark ? (byte)0x44 : (byte)0x2A, 0x5A, 0xC8, 0xFA)), 1);
            boxPen.Freeze();

            dc.DrawRoundedRectangle(boxBrush, boxPen,
                new Rect(boxLeft, boxTop, boxWidth, boxHeight), 4, 4);
            dc.DrawText(text, new Point(boxLeft + padX, boxTop + padY));
        }

        private static FormattedText MakeText(string text, double size, Brush brush)
        {
            return new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                size,
                brush,
                VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
        }
    }
}
