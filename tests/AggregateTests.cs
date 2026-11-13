// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯函数优先] 这里只测聚合 / 重采样这类纯算法，不碰文件、不碰 UI。
//    文件读写的行为由 HistoryTests 里的 FileRoundTrip 类断言覆盖。
// 2. [边界比正常值更重要] 空输入、单点、全空段、跨界窗口 —— 这些才是
//    实际会出事的地方（用户关机一周再打开就是「大量空段」）。
// 3. [时间必须是显式参数] 所有函数都吃 now，测试里绝不调用 DateTime.Now，
//    否则断言会在跨整点/跨天时随机失败。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using PeripheralBattery.Models;
using PeripheralBattery.Services;

namespace PeripheralBattery.Tests
{
    public static class AggregateTests
    {
        /// <summary>造样本：从 start 开始，每 stepSeconds 一个点，电量按 list 顺序</summary>
        private static List<Sample> MakeSamples(double start, double stepSeconds,
            params int[] percents)
        {
            var samples = new List<Sample>();
            for (int i = 0; i < percents.Length; i++)
            {
                samples.Add(new Sample(start + stepSeconds * i, percents[i], false));
            }
            return samples;
        }

        // ============================================================ 折叠

        [Test]
        public static void FoldAveragesWithinBucket()
        {
            // 三个点落在同一小时内（起点对齐到整点，避免跨桶）
            double start = 1_800_000_000.0;
            start -= start % 3600;
            var samples = MakeSamples(start + 60, 600, 80, 60, 70);

            var folded = HistoryAnalysis.Fold(samples, HistoryAnalysis.HourSpan);
            Assert.Equal(1, folded.Count, "三点同桶应折叠成一条");
            Assert.Equal(70, folded[0].Percent, "(80+60+70)/3 = 70");
            Assert.Equal(60, folded[0].MinPercent, "保留最小值");
            Assert.Equal(80, folded[0].MaxPercent, "保留最大值");
            Assert.Equal(3, folded[0].Samples, "样本数");
        }

        [Test]
        public static void FoldSkipsEmptyBuckets()
        {
            // 两个点相隔 5 小时 → 不能补出中间 4 个空桶
            double start = 1_800_000_000.0;
            start -= start % 3600;
            var samples = new List<Sample>
            {
                new(start + 60, 80, false),
                new(start + 5 * 3600 + 60, 50, false),
            };

            var folded = HistoryAnalysis.Fold(samples, HistoryAnalysis.HourSpan);
            Assert.Equal(2, folded.Count, "只产出有数据的两桶，不补空段");
            Assert.Equal(80, folded[0].Percent, "第一桶");
            Assert.Equal(50, folded[1].Percent, "第二桶");
        }

        [Test]
        public static void FoldRejectsImpossiblePercents()
        {
            var samples = new List<Sample>
            {
                new(1_800_000_000, 0, false),     // 0 不是合法电量
                new(1_800_000_001, 101, false),   // >100 丢弃
                new(1_800_000_002, 55, false),
            };
            var folded = HistoryAnalysis.Fold(samples, HistoryAnalysis.HourSpan);
            Assert.Equal(1, folded.Count, "只剩一个合法样本");
            Assert.Equal(55, folded[0].Percent, "0 与 101 都要被剔除");
            Assert.Equal(1, folded[0].Samples, "只有一条参与统计");
        }

        [Test]
        public static void FoldEmptyInput()
        {
            Assert.Equal(0, HistoryAnalysis.Fold(new List<Sample>(),
                HistoryAnalysis.HourSpan).Count, "空输入");
            Assert.Equal(0, HistoryAnalysis.Fold(null!,
                HistoryAnalysis.HourSpan).Count, "null 不崩");
            Assert.Equal(0, HistoryAnalysis.Fold(
                MakeSamples(1_800_000_000, 60, 50), 0).Count, "span<=0 直接返回");
        }

        [Test]
        public static void FoldMarksChargingBucket()
        {
            double start = 1_800_000_000.0;
            start -= start % 3600;
            var samples = new List<Sample>
            {
                new(start + 60, 40, false),
                new(start + 120, 45, true),   // 桶内任一点充电 → 整桶标充电
            };
            var folded = HistoryAnalysis.Fold(samples, HistoryAnalysis.HourSpan);
            Assert.True(folded[0].Charging, "桶内有充电样本就要标出来");
        }

        [Test]
        public static void FoldDayBuckets()
        {
            // 两天各一个点（用天桶）
            double start = 1_800_000_000.0;
            double dayStart = start - (start % 86400);
            var samples = new List<Sample>
            {
                new(dayStart + 3600, 90, false),
                new(dayStart + 86400 + 3600, 70, false),
            };
            var folded = HistoryAnalysis.Fold(samples, HistoryAnalysis.DaySpan);
            Assert.Equal(2, folded.Count, "两天两桶");
            Assert.Equal(90, folded[0].Percent, "第一天");
            Assert.Equal(70, folded[1].Percent, "第二天");
        }

        // ============================================================ 合并

        [Test]
        public static void MergeIntoWeightsBySampleCount()
        {
            // 已有桶：均值 80，来自 3 个样本；新增桶：均值 40，来自 1 个样本
            // 正确结果是 70（加权），不是 60（简单平均）
            var existing = new List<Aggregate>
            {
                new() { Start = 1_800_000_000, Span = 3600, Percent = 80,
                        MinPercent = 80, MaxPercent = 80, Samples = 3 },
            };
            var additions = new List<Aggregate>
            {
                new() { Start = 1_800_000_000, Span = 3600, Percent = 40,
                        MinPercent = 40, MaxPercent = 40, Samples = 1 },
            };

            var merged = HistoryAnalysis.MergeInto(existing, additions,
                HistoryAnalysis.HourSpan, 1_800_000_100,
                HistoryAnalysis.HourAggregateRetain);

            Assert.Equal(1, merged.Count, "同一个桶要合并而不是并列");
            Assert.Equal(70, merged[0].Percent, "(80*3 + 40*1)/4 = 70");
            Assert.Equal(4, merged[0].Samples, "样本数累加");
            Assert.Equal(40, merged[0].MinPercent, "min 取更小");
            Assert.Equal(80, merged[0].MaxPercent, "max 取更大");
        }

        [Test]
        public static void MergeIntoDropsExpiredButKeepsLast()
        {
            double now = 1_800_000_000.0;
            double retain = 3600.0;

            var existing = new List<Aggregate>
            {
                new() { Start = now - 10 * 3600, Span = 3600, Percent = 90,
                        MinPercent = 90, MaxPercent = 90, Samples = 1 },
                new() { Start = now - 5 * 3600, Span = 3600, Percent = 80,
                        MinPercent = 80, MaxPercent = 80, Samples = 1 },
                new() { Start = now - 1800, Span = 3600, Percent = 60,
                        MinPercent = 60, MaxPercent = 60, Samples = 1 },
            };

            var merged = HistoryAnalysis.MergeInto(existing, null,
                HistoryAnalysis.HourSpan, now, retain);

            // 窗口内：只有 now-1800 那条（now-5h 已过期）
            // 另外必须保留「窗口外最新的一条」即 now-5h，作为长期基线
            Assert.Equal(2, merged.Count, "窗口内 1 条 + 窗口外最新 1 条");
            Assert.Equal(now - 5 * 3600, merged[0].Start, "保留的是最新的过期条目");
            Assert.Equal(now - 1800, merged[1].Start, "窗口内的条目");
            // 更早那条（now-10h）应被彻底丢弃
            foreach (var item in merged)
            {
                Assert.True(item.Start != now - 10 * 3600, "最老的过期条目应丢弃");
            }
        }

        [Test]
        public static void MergeIntoKeepsSorted()
        {
            double now = 1_800_000_000.0;
            var existing = new List<Aggregate>
            {
                new() { Start = now - 7200, Span = 3600, Percent = 70,
                        MinPercent = 70, MaxPercent = 70, Samples = 1 },
                new() { Start = now - 3600, Span = 3600, Percent = 60,
                        MinPercent = 60, MaxPercent = 60, Samples = 1 },
            };
            var additions = new List<Aggregate>
            {
                // 故意乱序插入
                new() { Start = now - 100, Span = 3600, Percent = 50,
                        MinPercent = 50, MaxPercent = 50, Samples = 1 },
            };

            var merged = HistoryAnalysis.MergeInto(existing, additions,
                HistoryAnalysis.HourSpan, now, HistoryAnalysis.HourAggregateRetain);

            Assert.Equal(3, merged.Count, "三条都在窗口内");
            for (int i = 1; i < merged.Count; i++)
            {
                Assert.True(merged[i].Start > merged[i - 1].Start,
                    "必须按时间升序，图表直接按顺序连线");
            }
        }

        // ============================================================ 重采样

        [Test]
        public static void SamplePointsResamplesToTargetCount()
        {
            double now = 1_800_000_000.0;
            var aggregates = new List<Aggregate>();
            // 30 个天桶，横跨 30 天
            for (int i = 0; i < 30; i++)
            {
                aggregates.Add(new Aggregate
                {
                    Start = now - (30 - i) * 86400,
                    Span = 86400,
                    Percent = 80 - i,
                    MinPercent = 80 - i,
                    MaxPercent = 80 - i,
                    Samples = 1,
                });
            }

            var points = HistoryAnalysis.SamplePoints(aggregates,
                now - 30 * 86400, now, targetPoints: 12);

            Assert.Equal(12, points.Count, "必须精确产出 targetPoints 个点");
            foreach (var point in points)
            {
                Assert.True(point.HasData, "这段区间都有数据");
            }
        }

        [Test]
        public static void SamplePointsKeepsRealPointsWithoutGaps()
        {
            // 设计决策：SamplePoints 不再插空点。
            //
            // 空段改由 LineChart 按「时间间隔超过典型间隔 3 倍」判断断线 ——
            // 插空点会伪造出比实际更高的采样密度（屏幕上出现几十个点，
            // 实际只有 2 个样本），而时间间隔是客观事实。
            //
            // 这里锁住新的契约：稀疏数据只产出真实点，不产出占位点。
            double now = 1_800_000_000.0;
            var aggregates = new List<Aggregate>
            {
                new() { Start = now - 20 * 86400, Span = 86400, Percent = 90,
                        MinPercent = 90, MaxPercent = 90, Samples = 1 },
                new() { Start = now - 86400, Span = 86400, Percent = 50,
                        MinPercent = 50, MaxPercent = 50, Samples = 1 },
            };

            var points = HistoryAnalysis.SamplePoints(aggregates,
                now - 20 * 86400, now, targetPoints: 20);

            Assert.Equal(2, points.Count, "点不够密时直接返回真实点");
            int withData = 0;
            foreach (var point in points) if (point.HasData) withData++;
            Assert.Equal(2, withData, "跳过的 18 天不能被伪造成点");

            // 两个点的实际间隔远大于典型值 → 图表层据此断线
            double gap = points[1].T - points[0].T;
            Assert.True(gap > 10 * 86400, "间隔本身就是断点的依据：" + gap);
        }

        [Test]
        public static void SamplePointsEmptyAndDegenerateInput()
        {
            double now = 1_800_000_000.0;
            Assert.Equal(0, HistoryAnalysis.SamplePoints(null!, now - 100, now).Count,
                "null 输入");
            Assert.Equal(0, HistoryAnalysis.SamplePoints(new List<Aggregate>(),
                now - 100, now).Count, "空列表");
            Assert.Equal(0, HistoryAnalysis.SamplePoints(new List<Aggregate>(),
                now, now).Count, "区间为空");
            Assert.Equal(0, HistoryAnalysis.SamplePoints(new List<Aggregate>(),
                now - 100, now, targetPoints: 1).Count, "targetPoints<2 无意义");
        }

        [Test]
        public static void SamplePointsClipsOutsideWindow()
        {
            double now = 1_800_000_000.0;
            // 只有窗口外的数据
            var aggregates = new List<Aggregate>
            {
                new() { Start = now - 100 * 86400, Span = 86400, Percent = 90,
                        MinPercent = 90, MaxPercent = 90, Samples = 1 },
            };
            var points = HistoryAnalysis.SamplePoints(aggregates, now - 86400, now, 10);
            int withData = 0;
            foreach (var point in points) if (point.HasData) withData++;
            Assert.Equal(0, withData, "窗口外的数据不能被画进来");
        }

        // ============================================================ 保留常量

        [Test]
        public static void RetainWindowsAreLayeredCorrectly()
        {
            // 分层必须是递增的：原始 < 小时 < 天，否则折叠逻辑会自相矛盾
            Assert.True(HistoryAnalysis.RetainSeconds < HistoryAnalysis.HourAggregateRetain,
                "原始窗口必须短于小时聚合窗口");
            Assert.True(HistoryAnalysis.HourAggregateRetain < HistoryAnalysis.DayAggregateRetain,
                "小时窗口必须短于天聚合窗口");
            Assert.Equal(730 * 24 * 3600.0, HistoryAnalysis.DayAggregateRetain,
                "天聚合保留 24 个月（按 730 天算）");
        }
    }
}
