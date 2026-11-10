// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯 WPF 绘制，零第三方依赖] 不许引入任何图标字体包或图片资源。
//    所有图形用 Geometry / Path 现画，保证单文件 exe 体积。
// 2. [矢量优先] 电池图标、闪电、箭头全部按 24x24 逻辑格设计，
//    外层用 Viewbox 缩放，这样在 150%/200% DPI 下依然锐利。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Windows;
using System.Windows.Media;

namespace PeripheralBattery.UI
{
    /// <summary>一套小而精的 Fluent 风格图形</summary>
    public static class Icons
    {
        public static readonly Color DefaultStroke = Colors.Transparent;

        /// <summary>
        /// 横向电池外壳（圆角矩形 + 右侧凸起）。
        /// 返回 Path，配合 Viewbox 使用。
        /// </summary>
        public static Geometry BatteryShell(double width = 26, double height = 14)
        {
            double r = height / 2.0;
            double bodyW = width - 3;
            var group = new GeometryGroup();
            // 外壳：圆角矩形
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, bodyW, height), r - 1, r - 1));
            // 右侧凸起
            double capH = height * 0.42;
            double capY = (height - capH) / 2.0;
            group.Children.Add(new RectangleGeometry(
                new Rect(bodyW + 1, capY, 2, capH), 1, 1));
            return group;
        }

        /// <summary>
        /// 电池填充区（按百分比裁切后由调用方画成实心）。
        ///
        /// 坐标系约定：几何从 **(0,0)** 开始，且左上角就是**外壳描边内侧**的
        /// 左上角。调用方把它放在 (2,2) 即可（描边宽 1.4 取整 2）。
        ///
        /// ⚠️ 为什么不能在外壳坐标系里画：WPF 的 Path 会把【几何包围盒左上角】
        /// 对齐到元素原点。如果几何从 (2,2) 开始，包围盒就是 y∈[2,12]，
        /// 与外壳的 y∈[0,14] 分别用 VerticalAlignment.Center 居中时，
        /// 上边距算出来是 3 和 1，填充整体下沉 2px ——
        /// 顶部一条白缝、底部贴死内壁。100% 电量时最明显。
        /// </summary>
        public static Geometry BatteryFill(double width, double height, int percent)
        {
            // 左右各留 2px、右侧再让出 3px 的凸起区，得到可用宽度
            double inset = 2.0;
            double innerHeight = height - inset * 2;
            double bodyW = width - 3 - inset * 2;
            double fillW = Math.Max(0, bodyW * Math.Clamp(percent / 100.0, 0, 1));

            // 电量极低时也要能看见一点点，所以给一个最小可见宽度
            double r = innerHeight / 2.0;
            if (percent > 0 && fillW < r * 2) fillW = r * 2;
            if (fillW <= 0 || innerHeight <= 0) return Geometry.Empty;

            // 满电时填满可用宽度，不留右侧缝隙
            if (percent >= 100) fillW = bodyW;

            return new RectangleGeometry(new Rect(0, 0, fillW, innerHeight), r, r);
        }

        /// <summary>闪电（充电标记），24x24 逻辑格</summary>
        public static Geometry Bolt()
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(13, 2),
                IsClosed = true,
                IsFilled = true,
            };
            figure.Segments.Add(new LineSegment(new Point(5, 13.5), true));
            figure.Segments.Add(new LineSegment(new Point(10.5, 13.5), true));
            figure.Segments.Add(new LineSegment(new Point(9.5, 22), true));
            figure.Segments.Add(new LineSegment(new Point(18, 10), true));
            figure.Segments.Add(new LineSegment(new Point(12.5, 10), true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>刷新箭头（顺时针循环），24x24 逻辑格</summary>
        public static Geometry Refresh()
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            // 圆弧：从 -50° 到 250°，留出缺口
            var arc = new ArcSegment
            {
                Point = Polar(12, 12, 8, 250),
                Size = new Size(8, 8),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = true,
            };
            var figure = new PathFigure { StartPoint = Polar(12, 12, 8, -50), IsClosed = false };
            figure.Segments.Add(arc);
            var path = new PathGeometry();
            path.Figures.Add(figure);
            group.Children.Add(path);

            // 箭头
            var arrow = new PathFigure
            {
                StartPoint = Polar(12, 12, 8, 250),
                IsClosed = true,
                IsFilled = true,
            };
            arrow.Segments.Add(new LineSegment(new Point(
                Polar(12, 12, 8, 250).X - 3.4,
                Polar(12, 12, 8, 250).Y - 1.2), true));
            arrow.Segments.Add(new LineSegment(new Point(
                Polar(12, 12, 8, 250).X + 0.8,
                Polar(12, 12, 8, 250).Y + 3.2), true));
            var arrowGeo = new PathGeometry();
            arrowGeo.Figures.Add(arrow);
            group.Children.Add(arrowGeo);

            group.Freeze();
            return group;
        }

        /// <summary>齿轮（设置），24x24 逻辑格，用简化 8 齿造型</summary>
        public static Geometry Gear()
        {
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            const double cx = 12, cy = 12;
            var outer = new StreamGeometry();
            using (var ctx = outer.Open())
            {
                int teeth = 8;
                double rOuter = 9.6, rInner = 7.4;
                for (int i = 0; i < teeth * 2; i++)
                {
                    double angle = i * Math.PI / teeth - Math.PI / 2;
                    double r = (i % 2 == 0) ? rOuter : rInner;
                    var p = new Point(cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r);
                    if (i == 0) ctx.BeginFigure(p, true, true);
                    else ctx.LineTo(p, true, false);
                }
            }
            outer.Freeze();
            group.Children.Add(outer);

            // 中心孔（EvenOdd 挖空）
            group.Children.Add(new EllipseGeometry(new Point(cx, cy), 3.1, 3.1));
            group.Freeze();
            return group;
        }

        /// <summary>关闭 X，24x24 逻辑格</summary>
        public static Geometry Close()
        {
            var group = new GeometryGroup();
            group.Children.Add(new LineGeometry(new Point(6, 6), new Point(18, 18)));
            group.Children.Add(new LineGeometry(new Point(18, 6), new Point(6, 18)));
            group.Freeze();
            return group;
        }

        private static Point Polar(double cx, double cy, double r, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            return new Point(cx + Math.Cos(rad) * r, cy + Math.Sin(rad) * r);
        }
    }
}
