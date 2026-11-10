// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯逻辑与存储分离] 分桶、斜率、文案全部是纯函数，必须能脱机单测；
//    只有 History 类碰文件。行为基准是 Python 版 tests/test_history.py。
// 2. [样本不可篡改当前读数] 读不到时只回填 LastKnown，绝不许把历史值写回
//    Reading.Percent —— 那会让人以为设备还活着。
// 3. [编码] UTF-8 with BOM。
//
// 算法对齐 yixing233/logi-tray 的 BatteryService：
//   * 样本 {t, percent, charging}，保留 48 小时
//   * 但永远保留最新那一条 —— 它是「睡前还剩多少」的唯一来源
//   * 落盘节流 60 秒；电量没变且 20 秒内刚记过就不重复记
//   * 24 小时按小时分桶，无样本的时段沿用上一个已知值并标记休眠柱
//   * 预测用 24h 内放电样本的首尾斜率：剩余时间 = 当前电量 / rate

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PeripheralBattery.Models;

namespace PeripheralBattery.Services
{
    public static class HistoryAnalysis
    {
        // 保留窗口：只用于图表与预测，48 小时足够，也能防止文件无限增长
        public const double RetainSeconds = 48 * 3600;
        // 落盘节流。文件在 48 小时窗口下最大约 170 KB，一分钟一写无感，
        // 好处是进程被强杀时最多只丢 1 分钟样本。
        public const double PersistInterval = 60;
        // 两个样本之间的最小间隔，避免密集轮询把文件撑大
        public const double MinSampleGap = 20;
        // 预测窗口
        public const double PredictWindow = 24 * 3600;
        // 斜率至少要跨这么久才算可信
        public const double MinPredictSpan = 0.5 * 3600;
        // 跨过这么久标「高置信度」
        public const double HighConfidenceSpan = 4 * 3600;

        /// <summary>丢掉窗口外的旧样本，但永远保留最新那一条</summary>
        public static List<Sample> Prune(List<Sample> samples, double now)
        {
            double cutoff = now - RetainSeconds;
            var kept = samples.Where(s => s.T >= cutoff).ToList();
            if (kept.Count == 0 && samples.Count > 0)
            {
                return new List<Sample> { samples[^1] };
            }
            if (samples.Count > 0 && kept[kept.Count - 1].T != samples[^1].T)
            {
                kept.Add(samples[^1]);
            }
            return kept;
        }

        /// <summary>24 根柱子，从「23 小时前」到「当前小时」</summary>
        public static List<Bucket> BuildBuckets(List<Sample> samples, double now,
            int? currentPercent = null, int lastKnown = -1)
        {
            var ordered = samples.OrderBy(s => s.T).ToList();
            long thisHour = (long)(now / 3600) * 3600;
            long windowStart = thisHour - 23 * 3600;

            // 窗口起点之前的最后一个样本，作为第一根柱的延续基线
            int baseline = -1;
            foreach (var s in ordered)
            {
                if (s.T >= windowStart) break;
                baseline = s.Percent;
            }
            if (baseline <= 0)
            {
                baseline = lastKnown > 0 ? lastKnown : (currentPercent ?? -1);
            }

            var buckets = new List<Bucket>(24);
            int idx = 0;
            int running = baseline;
            for (int i = 0; i < 24; i++)
            {
                long start = thisHour - (23 - i) * 3600;
                long end = start + 3600;
                int count = 0;
                int value = running;
                while (idx < ordered.Count && ordered[idx].T < start) idx++;
                int j = idx;
                while (j < ordered.Count && ordered[j].T < end)
                {
                    count++;
                    value = ordered[j].Percent;
                    j++;
                }
                if (count > 0) running = value;

                buckets.Add(new Bucket
                {
                    Hour = TimeText.FromUnix(start).ToString("HH:mm"),
                    Percent = value,
                    Count = count,
                    Sleeping = count == 0 && value > 0,
                });
            }
            return buckets;
        }

        /// <summary>用窗口内放电样本的首尾斜率算剩余时间</summary>
        public static Forecast ForecastOf(List<Sample> samples, int? currentPercent,
            bool charging, double now)
        {
            if (!currentPercent.HasValue || currentPercent.Value <= 0)
            {
                return new Forecast { Reason = "设备离线" };
            }
            if (charging)
            {
                return new Forecast { Reason = "充电中" };
            }

            var window = samples
                .Where(s => s.T >= now - PredictWindow && !s.Charging && s.Percent > 0)
                .OrderBy(s => s.T)
                .ToList();

            if (window.Count < 2)
            {
                return new Forecast { Reason = "放电数据积累中", Confidence = "积累中" };
            }

            var first = window[0];
            var last = window[^1];
            double span = last.T - first.T;
            int drop = first.Percent - last.Percent;
            double spanHours = span / 3600.0;

            if (span < MinPredictSpan || drop <= 0)
            {
                return new Forecast
                {
                    Reason = "放电数据积累中",
                    Confidence = "积累中",
                    SpanHours = spanHours,
                };
            }

            double rate = drop / spanHours;
            if (rate <= 0)
            {
                return new Forecast
                {
                    Reason = "放电数据积累中",
                    Confidence = "积累中",
                    SpanHours = spanHours,
                };
            }

            return new Forecast
            {
                Hours = currentPercent.Value / rate,
                RatePerHour = rate,
                Confidence = span > HighConfidenceSpan ? "高" : "中",
                SpanHours = spanHours,
            };
        }

        /// <summary>一次算完图表 + 预测 + 上下界</summary>
        public static Analysis Analyze(List<Sample> samples, int? currentPercent,
            bool charging, double? now = null, Sample? lastSample = null)
        {
            double current = now ?? TimeText.UnixNow();
            var ordered = samples.OrderBy(s => s.T).ToList();

            var result = new Analysis
            {
                SampleCount = ordered.Count,
                LastKnownPercent = lastSample.HasValue ? lastSample.Value.Percent : -1,
                LastKnownAt = lastSample.HasValue ? lastSample.Value.T : 0,
            };

            var recent = ordered
                .Where(s => s.T >= current - PredictWindow && s.Percent > 0)
                .ToList();
            if (recent.Count > 0)
            {
                result.MinPercent = recent.Min(s => s.Percent);
                result.MaxPercent = recent.Max(s => s.Percent);
            }

            result.Buckets = BuildBuckets(ordered, current, currentPercent,
                                          result.LastKnownPercent);
            result.ActiveHours = result.Buckets.Count(b => b.Count > 0);
            result.Forecast = ForecastOf(ordered, currentPercent, charging, current);
            return result;
        }
    }

    /// <summary>
    /// 一台设备一条历史文件；读写都带节流，失败不影响读数。
    ///
    /// 路径：%LOCALAPPDATA%\外设电量\history_&lt;key&gt;.json
    /// 与 Python 版共用同一份数据，方便两边互相验证。
    /// </summary>
    public sealed class HistoryStore
    {
        private readonly object _gate = new();
        private List<Sample>? _samples;
        private double _lastPersist;

        public string Path { get; }

        public static string DefaultDirectory
        {
            // 复用 Settings 的目录解析：那里包含改名搬家与失败回退逻辑
            get { return Settings.Directory; }
        }

        public HistoryStore(string deviceKey, string? directory = null)
        {
            string safe = new string(deviceKey.Select(
                c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            string dir = directory ?? DefaultDirectory;
            Path = System.IO.Path.Combine(dir, "history_" + safe + ".json");
        }

        private List<Sample> Load()
        {
            if (_samples != null) return _samples;

            var samples = new List<Sample>();
            try
            {
                if (File.Exists(Path))
                {
                    using var stream = File.OpenRead(Path);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("samples", out var array))
                    {
                        foreach (var item in array.EnumerateArray())
                        {
                            try
                            {
                                int percent = item.GetProperty("percent").GetInt32();
                                if (percent <= 0 || percent > 100) continue;
                                double t = item.GetProperty("t").GetDouble();
                                bool charging = item.TryGetProperty("charging", out var c)
                                    && c.GetBoolean();
                                samples.Add(new Sample(t, percent, charging));
                            }
                            catch (Exception) { }
                        }
                    }
                    samples.Sort((a, b) => a.T.CompareTo(b.T));
                }
            }
            catch (Exception)
            {
                samples.Clear();   // 文件坏了不该影响读数，重新开始
            }
            _samples = samples;
            return samples;
        }

        /// <summary>
        /// 记一笔；读不到电量时什么也不做。
        ///
        /// 会过滤「不可能的跳变」：鼠标唤醒瞬间偶尔会报一个明显偏高的假读数
        /// （实测见过 51 → 59 → 51 这种一秒内的来回），写进历史会把续航预测
        /// 的斜率算歪。判据见 IsPlausible。
        /// </summary>
        public void Add(int? percent, bool charging, double? now = null)
        {
            if (!percent.HasValue || percent.Value <= 0 || percent.Value > 100) return;
            double current = now ?? TimeText.UnixNow();
            lock (_gate)
            {
                var samples = Load();
                if (samples.Count > 0)
                {
                    var last = samples[^1];
                    if (last.Percent == percent.Value && current - last.T < HistoryAnalysis.MinSampleGap)
                    {
                        return;
                    }
                }
                if (!IsPlausible(samples, percent.Value, charging, current)) return;

                samples.Add(new Sample(current, percent.Value, charging));
                _samples = HistoryAnalysis.Prune(samples, current);
                if (current - _lastPersist >= HistoryAnalysis.PersistInterval)
                {
                    Persist(current);
                }
            }
        }

        /// <summary>
        /// 这个读数是否值得写进历史。
        ///
        /// 无线设备在「唤醒瞬间」偶尔会回一个明显偏离的假值（本机实测到
        /// 51% 突然变 59%，700ms 后又变回 51%）。这类样本会把续航预测
        /// 的首尾斜率直接带偏，必须挡掉。
        ///
        /// 判据刻意保守 —— 宁可少记一个真样本，也不要把假数据写进去：
        ///   * 充电中的读数一律接受（充电曲线本来就不单调）
        ///   * 电量上升一律接受（换电池 / 长时间停放后校准）
        ///   * 下降幅度超过「3 个百分点/分钟」视为跳变，丢弃
        ///     实测数据：长时间平均 0.1~0.3 点/分钟，高强度使用峰值约
        ///     1.5~2.5 点/分钟（电池快耗尽时掉得尤其快）。取 3.0 既能挡住
        ///     「1 秒掉 20 点」这种明显假值，又不会误伤真实快放电。
        /// </summary>
        private static bool IsPlausible(List<Sample> samples, int percent,
            bool charging, double now)
        {
            if (charging || samples.Count == 0) return true;

            var last = samples[^1];
            double minutes = (now - last.T) / 60.0;
            if (minutes <= 0) return true;
            if (percent >= last.Percent) return true;

            double drop = last.Percent - percent;
            return drop / minutes <= 3.0;
        }

        /// <summary>强制落盘（退出前调用）</summary>
        public void Flush(double? now = null)
        {
            double current = now ?? TimeText.UnixNow();
            lock (_gate) Persist(current);
        }

        public Sample? LastSample()
        {
            lock (_gate)
            {
                var samples = Load();
                return samples.Count > 0 ? samples[^1] : null;
            }
        }

        public Analysis Analyze(int? currentPercent, bool charging, double? now = null)
        {
            double current = now ?? TimeText.UnixNow();
            lock (_gate)
            {
                var samples = Load();
                return HistoryAnalysis.Analyze(samples, currentPercent, charging, current,
                    samples.Count > 0 ? samples[^1] : null);
            }
        }

        private void Persist(double now)
        {
            _lastPersist = now;
            var samples = _samples ?? new List<Sample>();
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new
                {
                    version = 1,
                    samples = samples.Select(s => new
                    {
                        t = Math.Round(s.T, 3),
                        percent = s.Percent,
                        charging = s.Charging,
                    }).ToList(),
                };
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                // 不写 BOM：带 BOM 的 JSON 会让 Python 等解析器直接报错
                string temp = Path + ".tmp";
                File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception)
            {
                // 写不进去只损失记忆，不影响读数
            }
        }
    }
}
