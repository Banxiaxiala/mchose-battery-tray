// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [自绘] 用 OnRender 直接画，不用 Polyline + 几十个 Ellipse。
//    折线点默认 60 个，多台设备时会明显拖慢首帧。
// 2. [空段必须断线] HasData=false 的点不连前后 —— 设备关机 3 天再开机，
//    连成一条直线会让用户以为「电量一直稳稳的」。这是本控件与柱状图
//    最大的语义差异，改之前先想清楚。
// 3. [充电段单独上色] 充电时电量会飙升，那一段不是放电趋势。混成一条线
//    会让「24 个月趋势」出现看不懂的尖峰，必须用充电色标出来。
// 4. [坐标域跟随数据] 不用固定 0–100：长期趋势里电量经常只在 40–90 徘徊，
//    从 0 起算会把曲线压成一条平线，什么也看不出。但也不能无限放大 ——
//    至少留 Margin 个百分点，否则轻微波动被放大成剧烈起伏。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PeripheralBattery.Models;

namespace PeripheralBattery.UI
{
    /// <summary>折线图：看趋势，与 SparkChart 的「每小时分布」互补</summary>
    public sealed class LineChart : FrameworkElement
    {
        /// <summary>电量轴的上下留白（百分点）</summary>
        private const double MinAxisMargin = 4;

        /// <summary>坐标域至少这么多（百分点），否则轻微波动会被放大成锯齿</summary>
        private const double MinAxisSpan = 20;

        /// <summary>
        /// 相邻两点的时间间隔超过「典型间隔 × 这个倍数」就断线。
        ///
        /// 为什么需要它：SamplePoints 现在不再插空点（插空点会伪造采样密度），
        /// 所以「设备关机 3 天」在数据里就是相邻两点隔了 3 天。
        /// 不断线就会画出一条跨 3 天的斜线，看着像电量稳步下降，全是假的。
        ///
        /// 取 3 倍：小时分辨率下正常间隔 1 小时，允许 2 小时内的抖动；
        /// 天分辨率下同理。倍数太小会把正常抖动误判成断点，线条碎掉。
        /// </summary>
        private const double GapBreakRatio = 3.0;

        private List<ChartPoint> _points = new();
        private bool _dark = true;
        private string _fromLabel = "";
        private string _toLabel = "";
        private double _fromT;
        private double _toT;

        /// <summary>
        /// 设置数据。fromT / toT 是横轴的绝对时间范围（Unix 秒）。
        ///
        /// ⚠️ **必须传范围**，不能只按数组索引均匀摆点。
        /// 只按索引画时，「2 年窗口里只有 24 小时数据」会被拉伸铺满整条横轴，
        /// 用户看到的是「两年的趋势」，实际只有一天 —— 预览截图里验证过这个 bug。
        /// </summary>
        public void SetData(List<ChartPoint> points, bool dark,
            string fromLabel = "", string toLabel = "",
            double fromT = 0, double toT = 0)
        {
            _points = points ?? new List<ChartPoint>();
            _dark = dark;
            _fromLabel = fromLabel;
            _toLabel = toLabel;
            _fromT = fromT;
            _toT = toT;
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

            bool hasAny = false;
            foreach (var point in _points)
            {
                if (point.HasData) { hasAny = true; break; }
            }
            if (!hasAny)
            {
                var empty = MakeText("暂无历史数据", 10, textFaint);
                dc.DrawText(empty, new Point(0, Math.Max(0, (height - empty.Height) / 2)));
                return;
            }

            // ---- 坐标域 ----
            const double padTop = 3;
            const double labelBand = 12;      // 底部时间标签
            const double axisBand = 18;       // 左侧电量刻度
            double plotLeft = axisBand;
            double plotRight = Math.Max(plotLeft + 10, width - 2);
            double plotTop = padTop;
            double plotBottom = Math.Max(plotTop + 10, height - labelBand);

            int min = 101, max = -1;
            foreach (var point in _points)
            {
                if (!point.HasData) continue;
                if (point.Percent < min) min = point.Percent;
                if (point.Percent > max) max = point.Percent;
            }
            if (max < 0) { min = 0; max = 100; }

            double lo = Math.Max(0, min - MinAxisMargin);
            double hi = Math.Min(100, max + MinAxisMargin);
            if (hi - lo < MinAxisSpan)
            {
                double mid = (hi + lo) / 2;
                lo = Math.Max(0, mid - MinAxisSpan / 2);
                hi = Math.Min(100, lo + MinAxisSpan);
                if (hi - lo < MinAxisSpan) lo = Math.Max(0, hi - MinAxisSpan);
            }
            double span = Math.Max(1, hi - lo);

            double ValueToY(double value)
            {
                double norm = (value - lo) / span;
                return plotBottom - Math.Clamp(norm, 0, 1) * (plotBottom - plotTop);
            }

            // ---- 参考线：上下界各一条极淡的水平线，给一点纵深 ----
            var guidePen = new Pen(Palette.BrushOf(Color.FromArgb(
                _dark ? (byte)0x20 : (byte)0x16, 0x80, 0x90, 0xA0)), 1);
            guidePen.Freeze();
            dc.DrawLine(guidePen, new Point(plotLeft, plotTop),
                new Point(plotRight, plotTop));
            dc.DrawLine(guidePen, new Point(plotLeft, plotBottom),
                new Point(plotRight, plotBottom));

            // ---- 左侧刻度：只标上下界，字数最少但足够读数 ----
            var topText = MakeText(((int)Math.Round(hi)) + "%", 8.5, textFaint);
            dc.DrawText(topText, new Point(0, plotTop - topText.Height / 2));
            var bottomText = MakeText(((int)Math.Round(lo)) + "%", 8.5, textFaint);
            dc.DrawText(bottomText, new Point(0, plotBottom - bottomText.Height / 2));

            // ---- 折线 ----
            // 逐段判断：相邻两点都有数据才连线。这样空段自然断开。
            //
            // 横坐标按**时间**映射，不是按数组索引：
            //   2 年窗口里只有 24 小时数据时，那条线应该只占右端一小段，
            //   左边是大片空白。按索引摆点会把它拉伸成「两年的趋势」，说谎。
            double xFrom = _fromT;
            double xTo = _toT;
            bool timeAxis = xTo > xFrom;
            if (!timeAxis)
            {
                // 没给范围时退回索引轴（调用方漏传时仍能出图，但会失真）
                xFrom = 0;
                xTo = Math.Max(1, _points.Count - 1);
            }

            double XOfAt(int index, ChartPoint point)
            {
                if (!timeAxis)
                {
                    return _points.Count > 1
                        ? plotLeft + (plotRight - plotLeft) * index / (_points.Count - 1)
                        : plotLeft;
                }
                double norm = (point.T - xFrom) / (xTo - xFrom);
                return plotLeft + Math.Clamp(norm, 0, 1) * (plotRight - plotLeft);
            }

            // 配色：要用与「充电绿」区分得开的颜色。
            //
            // ⚠️ 不能用 Palette.ForBattery(高电量) —— BatteryHigh 与 Charging
            // 是同一个绿 0x22C55E，高电量折线会整条看起来像充电段，
            // 实际预览截图里就是这么误导读的。折线用中性强调蓝。
            Color lineColor = _dark
                ? Color.FromRgb(0x5A, 0xC8, 0xFA)
                : Color.FromRgb(0x02, 0x84, 0xC7);
            var chargingBrush = Palette.BrushOf(Palette.Charging);
            var normalPen = new Pen(Palette.BrushOf(lineColor), 1.6)
            { LineJoin = PenLineJoin.Round };
            normalPen.Freeze();
            var chargingPen = new Pen(chargingBrush, 1.6)
            { LineJoin = PenLineJoin.Round };
            chargingPen.Freeze();

            var geometryNormal = new StreamGeometry();
            var geometryCharging = new StreamGeometry();
            using (var ctx = geometryNormal.Open())
            using (var ctxCharging = geometryCharging.Open())
            {
                bool startedNormal = false;
                bool startedCharging = false;
                Point? previous = null;
                double previousT = 0;
                bool previousCharging = false;

                // 典型间隔：取所有相邻真实点的中位间隔，避开断点本身影响
                double typicalGap = 0;
                var gaps = new List<double>();
                List<ChartPoint> real = new();
                foreach (var item in _points)
                    if (item.HasData) real.Add(item);
                for (int i = 1; i < real.Count; i++)
                {
                    double g = real[i].T - real[i - 1].T;
                    if (g > 0) gaps.Add(g);
                }
                if (gaps.Count > 0)
                {
                    gaps.Sort();
                    typicalGap = gaps[gaps.Count / 2];
                }
                double breakGap = typicalGap > 0 ? typicalGap * GapBreakRatio : double.MaxValue;

                for (int i = 0; i < _points.Count; i++)
                {
                    var point = _points[i];
                    double x = XOfAt(i, point);

                    if (!point.HasData)
                    {
                        // 断点：结束当前两段路径，下一段重新 BeginFigure
                        startedNormal = false;
                        startedCharging = false;
                        previous = null;
                        continue;
                    }

                    var current = new Point(x, ValueToY(point.Percent));
                    bool charging = point.Charging;

                    // 时间跨度太大（设备离线/关机）→ 也当作断点
                    if (previous.HasValue && previousT > 0 &&
                        point.T - previousT > breakGap)
                    {
                        startedNormal = false;
                        startedCharging = false;
                        previous = null;
                    }

                    if (previous.HasValue)
                    {
                        // 上一段是不是充电，决定这条线画到哪个几何里。
                        // 用「当前点」的充电标志会漏掉从放电转入充电那一小段，
                        // 所以段属性取两端任一为充电（充电影响更值得标出）。
                        // ⚠️ 不能读 _points[i-1]：那个点可能已经被断点跳过了，
                        // 取到的充电标志属于另一段。
                        bool segmentCharging = charging || previousCharging;
                        var ctx2 = segmentCharging ? ctxCharging : ctx;
                        bool started = segmentCharging ? startedCharging : startedNormal;

                        if (!started)
                        {
                            ctx2.BeginFigure(previous.Value, false, false);
                            if (segmentCharging) startedCharging = true;
                            else startedNormal = true;
                        }
                        ctx2.LineTo(current, true, false);

                        // 另一条路径必须在这里断开，否则后面会用一条长线
                        // 跨过这段颜色不同的区间
                        if (segmentCharging) startedNormal = false;
                        else startedCharging = false;
                    }

                    previous = current;
                    previousT = point.T;
                    previousCharging = charging;
                }
            }
            geometryNormal.Freeze();
            geometryCharging.Freeze();

            dc.DrawGeometry(null, normalPen, geometryNormal);
            dc.DrawGeometry(null, chargingPen, geometryCharging);

            // ---- 底部时间标签 ----
            if (_fromLabel.Length > 0)
            {
                var left = MakeText(_fromLabel, 8.5, textFaint);
                dc.DrawText(left, new Point(plotLeft, plotBottom + 2));
            }
            if (_toLabel.Length > 0)
            {
                var right = MakeText(_toLabel, 8.5, textFaint);
                dc.DrawText(right, new Point(plotRight - right.Width, plotBottom + 2));
            }
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
