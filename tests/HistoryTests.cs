// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [与 Python 对齐] 断言与 tests/test_history.py 一一对应，
//    两边算出来的数必须相同（同一个 NOW 时间戳作为基准）。
// 2. [纯逻辑] 只测纯函数与文件读写，不碰硬件、不碰 UI。
// 3. [编码] UTF-8 with BOM。
//
// 算法对齐 yixing233/logi-tray 的 BatteryService。

using System;
using System.Collections.Generic;
using System.IO;
using PeripheralBattery.Models;
using PeripheralBattery.Services;

namespace PeripheralBattery.Tests
{
    public static class HistoryTests
    {
        private const double Now = 1_800_000_000.0;

        // ============================================================ 离线防抖
        //
        // 背景：2.4G 空口会丢帧。丢一帧就把界面标成「离线」时，
        // 用户看到的是「耳机明明戴着在用却显示掉线」。
        // 这里锁住「丢一帧不算离线、连续丢才不算」这个语义。

        [Test]
        public static void DebounceToleratesSingleMiss()
        {
            var debounce = new ReadingDebounce();
            Assert.False(debounce.Miss(), "丢一帧不应判离线");
            // 下一帧又读到了，计数必须清零 —— 否则偶尔丢帧会攒成误判
            debounce.Hit();
            Assert.Equal(0, debounce.Misses, "Hit 后计数归零");
            Assert.False(debounce.Miss(), "清零后再丢一帧仍不算离线");
        }

        [Test]
        public static void DebounceGoesOfflineAfterThreshold()
        {
            var debounce = new ReadingDebounce(3);
            Assert.False(debounce.Miss(), "第 1 次");
            Assert.False(debounce.Miss(), "第 2 次");
            Assert.True(debounce.Miss(), "第 3 次应判离线");
            Assert.True(debounce.Miss(), "后续仍为离线");
        }

        [Test]
        public static void DebounceRecoversAfterHit()
        {
            var debounce = new ReadingDebounce(2);
            debounce.Miss();
            Assert.True(debounce.Miss(), "连续 2 次应离线");
            debounce.Hit();
            Assert.Equal(0, debounce.Misses, "重新读到后必须复位");
            Assert.False(debounce.Miss(), "复位后重新计");
        }

        [Test]
        public static void DebounceClampsInvalidThreshold()
        {
            // 阈值 0/负数 会让判定永远不成立（设备掉线了界面还显示旧值），
            // 必须夹到至少 1
            var zero = new ReadingDebounce(0);
            Assert.True(zero.Miss(), "阈值 0 应生效为 1");
            var negative = new ReadingDebounce(-5);
            Assert.True(negative.Miss(), "负阈值应生效为 1");
        }

        // ============================================================ 预测
        [Test]
        public static void ForecastLinearDischarge()
        {
            // 4 小时从 90% 掉到 74%（每小时 4%），当前 74% → 18.5 小时
            var samples = new List<Sample>
            {
                new(Now - 4 * 3600, 90, false),
                new(Now - 3 * 3600, 86, false),
                new(Now - 2 * 3600, 81, false),
                new(Now - 1 * 3600, 78, false),
                new(Now, 74, false),
            };

            var forecast = HistoryAnalysis.ForecastOf(samples, 74, false, Now);
            Assert.Close(18.5, forecast.Hours ?? -1, 0.1, "剩余小时");
            Assert.Close(4.0, forecast.RatePerHour, 0.1, "每小时掉电");
            // 4 小时不算「大于 4 小时」，置信度是中
            Assert.Equal("中", forecast.Confidence, "置信度");
            Assert.Equal("18 小时", forecast.Text, "文案");
        }

        [Test]
        public static void ForecastHighConfidenceOverFourHours()
        {
            var samples = new List<Sample>();
            for (int h = 5; h >= 0; h--) samples.Add(new Sample(Now - h * 3600, 90 + h, false));

            var forecast = HistoryAnalysis.ForecastOf(samples, 90, false, Now);
            Assert.Equal("高", forecast.Confidence, "置信度");
            Assert.Close(90.0, forecast.Hours ?? -1, 0.5, "剩余小时");
        }

        [Test]
        public static void ForecastTooShortSpan()
        {
            var samples = new List<Sample>
            {
                new(Now - 1200, 80, false),
                new(Now, 79, false),
            };
            var forecast = HistoryAnalysis.ForecastOf(samples, 79, false, Now);
            Assert.Null(forecast.Hours, "跨度不足不应给结论");
            Assert.Equal("积累中", forecast.Confidence, "置信度");
            Assert.Equal("放电数据积累中", forecast.Text, "文案");
        }

        [Test]
        public static void ForecastChargingAndOffline()
        {
            var samples = new List<Sample>
            {
                new(Now - 3600, 80, false),
                new(Now, 74, false),
            };
            Assert.Equal("充电中", HistoryAnalysis.ForecastOf(samples, 74, true, Now).Text, "充电");
            Assert.Equal("设备离线", HistoryAnalysis.ForecastOf(samples, -1, false, Now).Text, "离线");
            Assert.Equal("设备离线", HistoryAnalysis.ForecastOf(samples, null, false, Now).Text, "null");
        }

        [Test]
        public static void ForecastExcludesChargingSamples()
        {
            // 充电中的样本涨幅不是耗电，必须排除
            var samples = new List<Sample>
            {
                new(Now - 5 * 3600, 40, false),
                new(Now - 4 * 3600, 60, true),
                new(Now - 3 * 3600, 80, true),
                new(Now - 2 * 3600, 38, false),
                new(Now - 1 * 3600, 36, false),
            };
            var forecast = HistoryAnalysis.ForecastOf(samples, 36, false, Now);
            // 跨度 4 小时（40% → 36%），每小时 1%
            Assert.Close(1.0, forecast.RatePerHour, 0.01, "排除充电样本后的斜率");
        }

        [Test]
        public static void ForecastTextFormats()
        {
            Assert.Equal("1 天 6 小时", new Forecast { Hours = 30.5 }.Text, "跨天");
            Assert.Equal("30 分钟", new Forecast { Hours = 0.5 }.Text, "不足一小时");
            Assert.Equal("5 小时", new Forecast { Hours = 5.4 }.Text, "整小时");
        }

        // ============================================================ 分桶
        [Test]
        public static void TwentyFourBuckets()
        {
            var samples = new List<Sample>();
            for (int h = 24; h >= 0; h--) samples.Add(new Sample(Now - h * 3600, 52 + h * 2, false));

            var buckets = HistoryAnalysis.BuildBuckets(samples, Now, 51, 51);
            Assert.Equal(24, buckets.Count, "桶数");
            Assert.True(buckets.Exists(b => b.Count > 0), "应有数据");
        }

        [Test]
        public static void SleepingBucketForwardFill()
        {
            var samples = new List<Sample>
            {
                new(Now - 2 * 3600, 70, false),
                new(Now - 3600, 68, false),
            };
            var buckets = HistoryAnalysis.BuildBuckets(samples, Now, null, 68);
            Assert.True(buckets.Exists(b => b.Sleeping), "应有休眠柱");
            foreach (var bucket in buckets)
            {
                if (bucket.Sleeping) Assert.True(bucket.Percent > 0, "休眠柱沿用上一个已知值");
            }
        }

        [Test]
        public static void EmptyBuckets()
        {
            var buckets = HistoryAnalysis.BuildBuckets(new List<Sample>(), Now, null, -1);
            Assert.Equal(24, buckets.Count, "桶数");
            Assert.False(buckets.Exists(b => b.Count > 0), "不应有数据");
        }

        // ============================================================ 裁剪
        [Test]
        public static void PruneKeepsNewestAlways()
        {
            var old = new List<Sample>
            {
                new(Now - 100 * 3600, 55, false),
                new(Now - 99 * 3600, 54, false),
            };
            var kept = HistoryAnalysis.Prune(old, Now);
            Assert.Equal(1, kept.Count, "只留最新一条");
            Assert.Equal(54, kept[0].Percent, "保留的是最新那条");
        }

        [Test]
        public static void PruneDropsExpiredKeepsRecent()
        {
            var samples = new List<Sample>
            {
                new(Now - 100 * 3600, 55, false),
                new(Now - 2 * 3600, 60, false),
                new(Now - 60, 59, false),
            };
            var kept = HistoryAnalysis.Prune(samples, Now);
            Assert.Equal(2, kept.Count, "只丢过期的");
        }

        // ============================================================ 分析
        [Test]
        public static void AnalyzeMinMaxAndLastKnown()
        {
            var samples = new List<Sample>
            {
                new(Now - 3 * 3600, 80, false),
                new(Now - 2 * 3600, 70, false),
                new(Now - 3600, 75, false),
            };
            var analysis = HistoryAnalysis.Analyze(samples, 74, false, Now, samples[^1]);
            Assert.Equal(70, analysis.MinPercent, "最小值");
            Assert.Equal(80, analysis.MaxPercent, "最大值");
            Assert.Equal(75, analysis.LastKnownPercent, "last_known");
            Assert.True(analysis.DescribeLastKnown(Now + 60).Contains("上次 75%"), "小字文案");
        }

        [Test]
        public static void AnalyzeOfflineKeepsCurrentNull()
        {
            var samples = new List<Sample> { new(Now - 3600, 75, false) };
            var analysis = HistoryAnalysis.Analyze(samples, null, false, Now, samples[^1]);
            // 当前读数为 null 时 forecast 必须说离线，绝不拿历史值冒充
            Assert.Equal("设备离线", analysis.Forecast.Text, "预测文案");
            Assert.Equal(75, analysis.LastKnownPercent, "历史值只进 last_known");
        }

        // ============================================================ 时长文案
        [Test]
        public static void FormatAgeBuckets()
        {
            Assert.Equal("刚刚", TimeText.FormatAge(Now - 30, Now), "30 秒");
            Assert.Equal("5 分钟前", TimeText.FormatAge(Now - 300, Now), "5 分钟");
            Assert.Equal("2 小时前", TimeText.FormatAge(Now - 7200, Now), "2 小时");
            Assert.Equal("2 天前", TimeText.FormatAge(Now - 200000, Now), "2 天");
        }

        [Test]
        public static void FormatAgeClampsRollback()
        {
            // 时钟回拨会算出负数，必须夹到「刚刚」而不是「-1 分钟前」
            Assert.Equal("刚刚", TimeText.FormatAge(Now + 5000, Now), "时钟回拨");
        }

        // ============================================================ 存储
        [Test]
        public static void PersistAndReload()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("logitech:PRO X 2", dir);
                for (int i = 0; i < 5; i++) history.Add(80 - i, false, Now + i * 25);
                history.Flush(Now);

                var reloaded = new HistoryStore("logitech:PRO X 2", dir);
                var analysis = reloaded.Analyze(76, false, Now + 200);
                Assert.Equal(5, analysis.SampleCount, "样本数");
                Assert.Equal(76, analysis.LastKnownPercent, "last_known");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void DedupeSameValue()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("dup", dir);
                for (int i = 0; i < 10; i++) history.Add(50, false, Now + i * 2);
                Assert.Equal(1, history.Analyze(50, false, Now + 30).SampleCount,
                    "20 秒内同电量只记一次");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void AddIgnoresInvalid()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("bad", dir);
                history.Add(null, false, Now);
                history.Add(0, false, Now);
                history.Add(101, false, Now);
                Assert.Null(history.LastSample(), "无效值不应写入");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void CorruptFileRecovered()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(System.IO.Path.Combine(dir, "history_broken.json"), "{not json");
                var history = new HistoryStore("broken", dir);
                Assert.Null(history.LastSample(), "坏文件应返回空");
                history.Add(60, false, Now);
                Assert.Equal(60, history.LastSample()?.Percent ?? -1, "之后仍能记新样本");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void NoBomInFile()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("bom", dir);
                history.Add(70, false, Now);
                history.Flush(Now);
                // 带 BOM 的 JSON 会让 Python 等解析器报错
                var head = new byte[3];
                using (var stream = File.OpenRead(history.Path))
                {
                    int read = stream.Read(head, 0, 3);
                    Assert.Equal(3, read, "应读到 3 字节");
                }
                Assert.False(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
                    "文件不能带 BOM");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 唤醒瞬间的假读数必须挡掉。
        /// 本机实测：51% → 59%（700ms 后）→ 51%，写进历史会把预测斜率带歪。
        /// </summary>
        [Test]
        public static void RejectsImpossibleJump()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("jump", dir);
                history.Add(60, false, Now);
                // 1 秒内掉 20 个百分点 = 1200 点/分钟，远超 3.0 的判据
                history.Add(40, false, Now + 1);
                Assert.Equal(1, history.Analyze(60, false, Now + 2).SampleCount,
                    "跳变样本应被丢弃");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public static void AcceptsPlausibleDrop()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pbtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var history = new HistoryStore("slow", dir);
                history.Add(60, false, Now);
                // 10 分钟掉 1 个百分点 = 0.1/分钟，正常放电
                history.Add(59, false, Now + 600);
                Assert.Equal(2, history.Analyze(59, false, Now + 601).SampleCount,
                    "正常放电应记录");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }

    public static class SettingsTests
    {
        [Test]
        public static void ClampsOutOfRange()
        {
            Assert.Equal(Settings.IntervalMin, Settings.ClampInterval(1), "间隔下限");
            Assert.Equal(Settings.IntervalMax, Settings.ClampInterval(99999), "间隔上限");
            Assert.Equal(Settings.WidthMin, Settings.ClampWidth(10), "宽度下限");
            Assert.Equal(Settings.WidthMax, Settings.ClampWidth(9999), "宽度上限");
            Assert.Equal(Settings.ChartHeightMin, Settings.ClampChartHeight(1), "图表高度下限");
            Assert.Equal(Settings.ChartHeightMax, Settings.ClampChartHeight(9999), "图表高度上限");
        }

        [Test]
        public static void DefaultsAreInRange()
        {
            var settings = new Settings();
            Assert.True(settings.Interval >= Settings.IntervalMin
                     && settings.Interval <= Settings.IntervalMax, "默认间隔合法");
            Assert.True(settings.CardWidth >= Settings.WidthMin
                     && settings.CardWidth <= Settings.WidthMax, "默认宽度合法");
            Assert.True(settings.ChartHeight >= Settings.ChartHeightMin
                     && settings.ChartHeight <= Settings.ChartHeightMax, "默认图表高度合法");
        }

        [Test]
        public static void SanitizeFixesAll()
        {
            var settings = new Settings
            {
                Interval = 0,
                CardWidth = 0,
                ChartHeight = 0,
            };
            settings.Sanitize();
            Assert.Equal(Settings.IntervalMin, settings.Interval, "间隔被拉回");
            Assert.Equal(Settings.WidthMin, settings.CardWidth, "宽度被拉回");
            Assert.Equal(Settings.ChartHeightMin, settings.ChartHeight, "图表高度被拉回");
        }

        [Test]
        public static void CardHeightZeroMeansAuto()
        {
            // 0 是合法值：表示「高度随内容自动」。不能把它当成越界拉回 240
            Assert.Equal(0.0, Settings.ClampCardHeight(0), "0 = 自动");
            Assert.Equal(0.0, Settings.ClampCardHeight(-5), "负数也当自动");
        }

        [Test]
        public static void CardHeightClamped()
        {
            Assert.Equal(Settings.CardHeightMin, Settings.ClampCardHeight(10), "下限");
            Assert.Equal(Settings.CardHeightMax, Settings.ClampCardHeight(99999), "上限");
        }

        [Test]
        public static void ThresholdsKeptStrictlyIncreasing()
        {
            var settings = new Settings
            {
                ThresholdCritical = 80,
                ThresholdLow = 20,
                ThresholdMid = 10,
            };
            settings.Sanitize();
            Assert.True(settings.ThresholdCritical < settings.ThresholdLow,
                "critical < low");
            Assert.True(settings.ThresholdLow < settings.ThresholdMid,
                "low < mid");
        }

        [Test]
        public static void ColorFieldsDefaultToEmpty()
        {
            // 空串 = 用内置配色。升级后新增颜色项能自动生效
            var settings = new Settings();
            Assert.Equal("", settings.ColorHigh, "color_high");
            Assert.Equal("", settings.ColorCharging, "color_charging");
        }

        // ============================================================ 提示降级
        //
        // NotifyIcon.Text 上限 63 字符。三台设备的完整行有 140+ 字符，
        // 必须降级；降级绝不能把标题也删掉（旧实现就是这样，
        // 悬浮后只显示「外设电量」4 个字符）。

        [Test]
        public static void TooltipFitsThreeDevices()
        {
            var entries = new List<TooltipText.Entry>
            {
                new()
                {
                    Label = "鼠标",
                    Full = "G304 Lightspeed Wireless Gaming Mouse  90%  放电中  · 预计剩余 1 天 8 小时",
                    Compact = TooltipText.CompactOf("鼠标", 90, true, false),
                },
                new()
                {
                    Label = "耳机",
                    Full = "BlackShark V2 Pro  38%  放电中  · 预计剩余 12 小时",
                    Compact = TooltipText.CompactOf("耳机", 38, true, false),
                },
                new()
                {
                    Label = "键盘",
                    Full = "维咖 MAX102  100%  已连接  · 预计剩余 5 天",
                    Compact = TooltipText.CompactOf("键盘", 100, true, false),
                },
            };

            string text = TooltipText.Build(entries);
            Assert.True(text.Length <= TooltipText.MaxLength,
                "必须不超 63 字符，实际 " + text.Length);
            // 关键：三台都要在，不能被裁成只剩标题
            Assert.True(text.Contains("鼠标"), "缺鼠标行：" + text);
            Assert.True(text.Contains("耳机"), "缺耳机行：" + text);
            Assert.True(text.Contains("键盘"), "缺键盘行：" + text);
            Assert.True(text.Contains("90%") && text.Contains("38%") && text.Contains("100%"),
                "电量数字都要在：" + text);
        }

        [Test]
        public static void TooltipKeepsFullLineWhenItFits()
        {
            var entries = new List<TooltipText.Entry>
            {
                new()
                {
                    Label = "鼠标",
                    Full = "PRO X 2  51%  放电中",
                    Compact = "鼠标  51%",
                },
            };
            // 一行装得下时应保留信息量更大的完整行
            Assert.Equal("外设电量\nPRO X 2  51%  放电中",
                TooltipText.Build(entries), "能放下就用完整行");
        }

        [Test]
        public static void TooltipAlwaysKeepsTitle()
        {
            // 极端输入：单台设备的 compact 都放不下
            var entries = new List<TooltipText.Entry>
            {
                new() { Label = "非常非常非常非常非常长的设备标签", Compact = new string('x', 200) },
            };
            string text = TooltipText.Build(entries);
            Assert.True(text.Length <= TooltipText.MaxLength, "必须守 63 字符上限");
            Assert.True(text.StartsWith("外设电量"), "标题不能被裁掉：" + text);
        }

        [Test]
        public static void TooltipCompactLineMarksOffline()
        {
            Assert.Equal("耳机  38%  (离线)", TooltipText.CompactOf("耳机", 38, false, false),
                "离线标记");
            Assert.Equal("耳机  38%  充电中", TooltipText.CompactOf("耳机", 38, true, true),
                "充电标记");
            Assert.Equal("耳机  --", TooltipText.CompactOf("耳机", null, true, false),
                "无读数");
        }
    }
}
