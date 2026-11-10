// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [配色与阈值属于可配置行为] 用户能在 settings.json 里改颜色和档位阈值，
//    这里锁住「解析失败必须回退默认」与「阈值必须严格递增」两条底线。
// 2. [不碰 UI 线程] 只调用纯函数，不创建任何 Window。
// 3. [编码] UTF-8 with BOM。

using PeripheralBattery.UI;

namespace PeripheralBattery.Tests
{
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
