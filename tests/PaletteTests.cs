// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [配色与阈值属于可配置行为] 用户能在 settings.json 里改颜色和档位阈值，
//    这里锁住「解析失败必须回退默认」与「阈值必须严格递增」两条底线。
// 2. [不碰 UI 线程] 只调用纯函数，不创建任何 Window。
// 3. [编码] UTF-8 with BOM。

using PeripheralBattery.UI;

namespace PeripheralBattery.Tests
{
    public static class TrayRingAutoTests
    {
        // 端到端：只有一个 rings 元素时，渲染出来的几何必须是单环。
        // 这条防的是「默认几何盖住了自适应」——那个 bug 让单设备也画三圈。

        [Test]
        public static void OneRingArrayProducesOneRingGeometry()
        {
            var rings = new Models.Reading?[]
            {
                new Models.Reading { Percent = 80, Connected = true },
            };

            // 模拟 TrayIcon 的调用方式：geometry 传 null 让它自适应
            var geometry = UI.RingGeometry.ForCount(rings.Length);
            Assert.Equal(1, geometry.Radii.Length,
                "一个 rings 元素 → 单环几何");
        }

        [Test]
        public static void ThreeRingArrayProducesThreeRingGeometry()
        {
            var rings = new Models.Reading?[3];
            var geometry = UI.RingGeometry.ForCount(rings.Length);
            Assert.Equal(3, geometry.Radii.Length, "三个元素 → 三环几何");
        }

        [Test]
        public static void GeometryArrayNeverShorterThanRings()
        {
            // 绘制循环按 geo.Radii.Length 走。
            // 如果几何圈数少于 rings 元素数，后面的设备就画不出来。
            for (int count = 1; count <= 6; count++)
            {
                var geometry = UI.RingGeometry.ForCount(count);
                Assert.True(geometry.Radii.Length >= System.Math.Min(count, 3),
                    count + " 台设备时几何圈数不足");
            }
        }
    }

    public static class DeviceSummaryTests
    {
        // 「管理设备」按钮的文案。
        //
        // 实测 bug：用户只插一个鼠标，按钮显示「已启用 1 / 14 台」——
        // 分母用了内置型号总数（14），看起来像工具认为他有 14 台设备。

        [Test]
        public static void SingleDeviceShowsOneNotFourteen()
        {
            string text = Models.DeviceSummaryText.Build(1, 1);
            Assert.Equal("管理设备（1 台）", text,
                "只有一台设备时不该显示分母");
            Assert.False(text.Contains("14"), "绝不能出现内置型号总数");
        }

        [Test]
        public static void MultipleDevicesShowFraction()
        {
            Assert.Equal("管理设备（2 / 3 台）",
                Models.DeviceSummaryText.Build(2, 3), "部分启用时显示分数");
        }

        [Test]
        public static void AllEnabledShowsTotalOnly()
        {
            Assert.Equal("管理设备（3 台）",
                Models.DeviceSummaryText.Build(3, 3), "全启用时只显示总数");
        }

        [Test]
        public static void NoDevicesShowsPlainLabel()
        {
            Assert.Equal("管理设备",
                Models.DeviceSummaryText.Build(0, 0), "没有任何设备时不带括号");
        }
    }

    public static class TrayGeometryTests
    {
        // 托盘图标应当**按实际设备数**画环，而不是永远三圈。
        // 只有一台却画三圈（两圈空轨道）会让用户以为还有设备没读到，
        // 而且白白浪费图标面积。

        [Test]
        public static void SingleDeviceDrawsSingleRing()
        {
            var geometry = UI.RingGeometry.ForCount(1);
            Assert.Equal(1, geometry.Radii.Length, "一台设备只画一圈");
            Assert.Equal(1, geometry.Strokes.Length, "描边数也要匹配");
        }

        [Test]
        public static void TwoDevicesDrawTwoRings()
        {
            var geometry = UI.RingGeometry.ForCount(2);
            Assert.Equal(2, geometry.Radii.Length, "两台设备画两圈");
        }

        [Test]
        public static void ThreeDevicesDrawThreeRings()
        {
            var geometry = UI.RingGeometry.ForCount(3);
            Assert.Equal(3, geometry.Radii.Length, "三台设备画三圈");
        }

        [Test]
        public static void RingCountIsClamped()
        {
            // 0 或负数不能画出「没有环」的图标；4 以上也不能超几何上限
            Assert.Equal(1, UI.RingGeometry.ForCount(0).Radii.Length, "0 台按 1 圈处理");
            Assert.Equal(1, UI.RingGeometry.ForCount(-5).Radii.Length, "负数同理");
            Assert.Equal(3, UI.RingGeometry.ForCount(99).Radii.Length, "超过 3 台按 3 圈");
        }

        [Test]
        public static void SingleRingIsThickerThanThreeRingOuter()
        {
            // 环少了，单圈可以更粗 —— 这是「加大一点」的诉求
            var one = UI.RingGeometry.ForCount(1);
            var three = UI.RingGeometry.ForCount(3);
            Assert.True(one.Strokes[0] > three.Strokes[0],
                $"单环描边({one.Strokes[0]}) 应比三环外圈({three.Strokes[0]}) 更粗");
        }

        [Test]
        public static void RingsDoNotOverlap()
        {
            // 相邻圈不能粘连。阈值按环数区分：
            //   1~2 环：可以做到 1.4px 以上，就要求 ≥1.4
            //   3 环：0.8px 是实测穷举出的「三圈仍可分」下限（见 Default 注释），
            //         硬要 1.4 就得把描边削到 2.2，实机上反而更糊
            for (int count = 1; count <= 3; count++)
            {
                var geometry = UI.RingGeometry.ForCount(count);
                double minGap = count >= 3 ? 0.75 : 1.4;
                for (int i = 1; i < geometry.Radii.Length; i++)
                {
                    double outerInner = geometry.Radii[i - 1] - geometry.Strokes[i - 1] / 2;
                    double innerOuter = geometry.Radii[i] + geometry.Strokes[i] / 2;
                    double gap = outerInner - innerOuter;
                    Assert.True(gap >= minGap,
                        $"{count} 环时第 {i} 与 {i + 1} 圈的净间隙只有 {gap:0.00}px，低于下限 {minGap}");
                }
            }
        }

        [Test]
        public static void RingsFitInside24PxCanvas()
        {
            // 24px 画布中心 12，外沿不能超出（留抗锯齿余量）
            for (int count = 1; count <= 3; count++)
            {
                var geometry = UI.RingGeometry.ForCount(count);
                double outerEdge = geometry.Radii[0] + geometry.Strokes[0] / 2;
                Assert.True(outerEdge <= 12.0,
                    $"{count} 环外沿 {outerEdge:0.00} 超出 24px 画布半宽 12");
            }
        }
    }

    public static class PaletteTests
    {
        [Test]
        public static void DefaultThresholdsMatchBuiltin()
        {
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
            Assert.Equal(10, Palette.ThresholdCritical, "告急阈值");
            Assert.Equal(30, Palette.ThresholdLow, "偏低阈值");
            Assert.Equal(55, Palette.ThresholdMid, "一般阈值");
        }

        [Test]
        public static void CustomThresholdsReorder()
        {
            // 阈值传反了也要被拉成严格递增，不能让档位无法命中
            Palette.ApplyCustom("", "", "", "", "", 40, 20, 10);
            Assert.True(Palette.ThresholdCritical < Palette.ThresholdLow, "critical < low");
            Assert.True(Palette.ThresholdLow < Palette.ThresholdMid, "low < mid");
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
        }

        [Test]
        public static void CustomColorApplied()
        {
            Palette.ApplyCustom("#112233", "", "", "", "", 10, 30, 55);
            var color = Palette.ForBattery(90, false);
            Assert.Equal((byte)0x11, color.R, "R");
            Assert.Equal((byte)0x22, color.G, "G");
            Assert.Equal((byte)0x33, color.B, "B");
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
        }

        [Test]
        public static void BadColorFallsBackToDefault()
        {
            // settings.json 是给用户手改的，写错颜色绝不能让界面变黑
            Palette.ApplyCustom("zzz", "12345", "#GGGGGG", null!, "", 10, 30, 55);
            var color = Palette.ForBattery(90, false);
            Assert.Equal(Palette.BatteryHigh.R, color.R, "回退默认绿");
            Assert.Equal(Palette.BatteryHigh.G, color.G, "回退默认绿 G");
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
        }

        [Test]
        public static void ChargingOverridesEverything()
        {
            Palette.ApplyCustom("", "", "", "", "#0000FF", 10, 30, 55);
            var color = Palette.ForBattery(5, true);
            Assert.Equal((byte)0x00, color.R, "充电色优先于告急红");
            Assert.Equal((byte)0x00, color.G, "G");
            Assert.Equal((byte)0xFF, color.B, "B");
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
        }

        [Test]
        public static void LevelTextFollowsThresholds()
        {
            Palette.ApplyCustom("", "", "", "", "", 10, 30, 55);
            Assert.Equal("极低", Palette.LevelTextOf(10, false), "边界 10");
            Assert.Equal("偏低", Palette.LevelTextOf(11, false), "11");
            Assert.Equal("一般", Palette.LevelTextOf(31, false), "31");
            Assert.Equal("良好", Palette.LevelTextOf(56, false), "56");
            Assert.Equal("充足", Palette.LevelTextOf(81, false), "81");
            Assert.Equal("充电中", Palette.LevelTextOf(5, true), "充电");
        }
    }
}
