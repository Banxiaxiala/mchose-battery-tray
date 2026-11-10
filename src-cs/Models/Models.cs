// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯数据] 本文件只放数据结构与纯换算，禁止出现任何 Win32 / HID / WPF 调用。
//    它必须能被单元测试无硬件导入。
// 2. [契约同步] 改动字段时同步更新 src/services 与 src/ui 的用法，以及
//    tests/ 里的断言（Python 侧 tests/test_history.py 是行为基准）。
// 3. [编码] UTF-8 with BOM。

using System;

namespace PeripheralBattery.Models
{
    /// <summary>
    /// 一台设备的一次读数。
    ///
    /// Percent 为 null 表示「本轮没读到」，绝不拿历史值顶替 ——
    /// 「现在还有 77%」和「睡前还剩 77%」是两件事，混在一起会让人以为设备还活着。
    /// </summary>
    public sealed class Reading
    {
        public string Name { get; set; } = "";
        public int? Percent { get; set; }
        public bool Charging { get; set; }
        public bool Connected { get; set; } = true;
        public string Status { get; set; } = "";
        public string Transport { get; set; } = "";
        public string Forecast { get; set; } = "";

        public bool Known { get { return Percent.HasValue; } }

        /// <summary>托盘悬停提示里的一行</summary>
        public string Line(string label)
        {
            string pct = Percent.HasValue ? Percent.Value + "%" : "--";
            string text = label + "  " + pct;
            if (!Connected)
            {
                text += "  (离线)";
            }
            else if (Charging)
            {
                text += "  充电中";
            }
            else if (!string.IsNullOrEmpty(Status))
            {
                text += "  " + Status;
            }
            if (!string.IsNullOrEmpty(Forecast) && Connected && !Charging)
            {
                text += "  · " + Forecast;
            }
            return text;
        }
    }

    /// <summary>一个历史样本</summary>
    public struct Sample
    {
        public double T;          // Unix 时间戳（秒）
        public int Percent;
        public bool Charging;

        public Sample(double t, int percent, bool charging)
        {
            T = t;
            Percent = percent;
            Charging = charging;
        }
    }

    /// <summary>24 小时柱状图的一根柱</summary>
    public sealed class Bucket
    {
        public string Hour { get; set; } = "";
        public int Percent { get; set; }
        public int Count { get; set; }
        public bool Sleeping { get; set; }

        public bool HasData { get { return Percent > 0 && !Sleeping; } }
    }

    /// <summary>续航预测</summary>
    public sealed class Forecast
    {
        public double? Hours { get; set; }
        public double RatePerHour { get; set; }
        public string Confidence { get; set; } = "";
        public double SpanHours { get; set; }
        public string Reason { get; set; } = "";

        public string Text
        {
            get
            {
                if (!Hours.HasValue)
                {
                    return string.IsNullOrEmpty(Reason) ? "放电数据积累中" : Reason;
                }
                double h = Hours.Value;
                if (h >= 24)
                {
                    return ((int)(h / 24)) + " 天 " + ((int)(h % 24)) + " 小时";
                }
                if (h < 1)
                {
                    return ((int)(h * 60)) + " 分钟";
                }
                return ((int)h) + " 小时";
            }
        }
    }

    /// <summary>一次完整的分析结果</summary>
    public sealed class Analysis
    {
        public System.Collections.Generic.List<Bucket> Buckets = new();
        public Forecast Forecast = new();
        public int MinPercent = -1;
        public int MaxPercent = -1;
        public int ActiveHours;
        public int LastKnownPercent = -1;
        public double LastKnownAt;
        public int SampleCount;

        /// <summary>「上次 77% · 2 小时前」；无记忆时返回空串（绝不编数字）</summary>
        public string DescribeLastKnown(double? now = null)
        {
            if (LastKnownPercent < 0 || LastKnownAt <= 0)
            {
                return "";
            }
            string age = TimeText.FormatAge(LastKnownAt, now);
            return age.Length > 0
                ? "上次 " + LastKnownPercent + "% · " + age
                : "上次 " + LastKnownPercent + "%";
        }
    }

    /// <summary>
    /// 把「这一轮没读到」和「设备真的离线」分开。
    ///
    /// 2.4G 空口会丢帧：丢一帧就让界面显示「离线」，用户看到的是
    /// 「明明戴着耳机在听歌，图标却说掉线」。默认 30 秒刷新一次，
    /// 一帧丢失就是 30 秒的错误展示。
    ///
    /// 所以“没读到”必须连续发生若干次才算离线。阈值取 3 的依据：
    /// 实测单帧丢失率偶发但连续三帧都丢的概率极低，而设备真正休眠/关机时
    /// 是每一帧都应答不了 —— 三帧正好把两者分开，最多延迟 3 个刷新周期
    /// 才显示掉线（约 90 秒），可接受。
    /// </summary>
    public sealed class ReadingDebounce
    {
        /// <summary>连续失败多少次才判定离线</summary>
        public const int DefaultOfflineAfter = 3;

        private readonly int _offlineAfter;
        private int _misses;

        public ReadingDebounce(int offlineAfter = DefaultOfflineAfter)
        {
            // 允许 1（= 一次就判离线，等同不做防抖），但不允许 0/负数 ——
            // 那会让判定永远不成立，设备掉线了界面还一直显示旧值。
            _offlineAfter = Math.Max(1, offlineAfter);
        }

        /// <summary>连续失败次数</summary>
        public int Misses { get { return _misses; } }

        /// <summary>这一轮读到了：清零</summary>
        public void Hit()
        {
            _misses = 0;
        }

        /// <summary>
        /// 这一轮没读到。返回 true 表示可以判定离线了。
        /// </summary>
        public bool Miss()
        {
            _misses++;
            return _misses >= _offlineAfter;
        }

        /// <summary>清空状态（例如用户手动刷新后想重新开始计）</summary>
        public void Reset()
        {
            _misses = 0;
        }
    }

    public static class TimeText
    {
        /// <summary>相对时长。时钟回拨会算出负值，夹到 0 就是「刚刚」</summary>
        public static string FormatAge(double at, double? now = null)
        {
            double current = now.HasValue ? now.Value : UnixNow();
            double delta = Math.Max(0.0, current - at);
            if (delta < 60) return "刚刚";
            if (delta < 3600) return ((int)(delta / 60)) + " 分钟前";
            if (delta < 86400) return ((int)(delta / 3600)) + " 小时前";
            return ((int)(delta / 86400)) + " 天前";
        }

        public static double UnixNow()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalSeconds;
        }

        public static DateTime FromUnix(double seconds)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(seconds).ToLocalTime();
        }
    }
}
