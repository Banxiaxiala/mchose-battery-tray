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

        // ==================================================== 分层保留
        //
        // 要做到「看 24 个月趋势」又不能让文件无限长大，只能分层：
        //
        //   层级     保留时长      每条间隔   每设备条数   文件大小
        //   原始     48 小时       20~30 秒   ~6000        ~150 KB
        //   小时聚合 30 天         1 小时     720           ~30 KB
        //   天聚合   24 个月（730） 1 天       730           ~30 KB
        //
        // 合计约 200 KB/设备，完全可以接受。若改成「24 个月全存原始样本」，
        // 按 30 秒一条算是 630 万条 ≈ 150 MB —— 这就是必须聚合的理由。

        /// <summary>小时级聚合保留时长：30 天</summary>
        public const double HourAggregateRetain = 30 * 24 * 3600;
        /// <summary>天级聚合保留时长：24 个月（按 730 天算，不追平均月长）</summary>
        public const double DayAggregateRetain = 730 * 24 * 3600;
        /// <summary>小时聚合的桶宽</summary>
        public const int HourSpan = 3600;
        /// <summary>天聚合的桶宽</summary>
        public const int DaySpan = 86400;

        /// <summary>
        /// 把样本折叠成指定桶宽的聚合序列。
        ///
        /// 规矩：
        ///   * 桶按**本地时区**对齐（天桶从本地 00:00 起），否则用户看到的
        ///     「这天」会从 UTC 凌晨划开，跟其他软件对不上。
        ///   * 只产出**有样本**的桶；空段不补 —— 补出来的点会被折线图当成
        ///     真实数据连起来，把「关机了 3 天」画成电量平稳的直线。
        ///     断点的表达交给图表层（HasData=false 就不画）。
        ///   * 电量取平均（见 Aggregate 注释），同时保留 min/max 供以后统计。
        /// </summary>
        public static List<Aggregate> Fold(List<Sample> samples, int span)
        {
            var result = new List<Aggregate>();
            if (samples == null || samples.Count == 0 || span <= 0) return result;

            // 本地时区偏移：用固定偏移近似（不做夏令时回溯）。
            // 本机东八区无夏令时；对夏令时地区轻微偏移可接受 ——
            // 天桶最多偏 1 小时，不会把趋势画错。
            double offset = TimeZoneInfo.Local.GetUtcOffset(
                DateTime.UtcNow).TotalSeconds;

            var ordered = samples.OrderBy(s => s.T).ToList();
            double currentStart = double.NaN;
            long sum = 0;
            int count = 0;
            int min = int.MaxValue, max = int.MinValue;
            bool charging = false;

            void Flush()
            {
                if (count == 0) return;
                result.Add(new Aggregate
                {
                    Start = currentStart,
                    Span = span,
                    Percent = (int)Math.Round((double)sum / count),
                    MinPercent = min,
                    MaxPercent = max,
                    Samples = count,
                    Charging = charging,
                });
                sum = 0; count = 0; min = int.MaxValue; max = int.MinValue;
                charging = false;
            }

            foreach (var sample in ordered)
            {
                if (sample.Percent <= 0 || sample.Percent > 100) continue;

                double shifted = sample.T + offset;
                double slot = Math.Floor(shifted / span) * span - offset;
                if (double.IsNaN(currentStart)) currentStart = slot;
                else if (slot > currentStart) { Flush(); currentStart = slot; }

                sum += sample.Percent;
                count++;
                if (sample.Percent < min) min = sample.Percent;
                if (sample.Percent > max) max = sample.Percent;
                if (sample.Charging) charging = true;
            }
            Flush();
            return result;
        }

        /// <summary>
        /// 把旧样本折叠追加到已有聚合序列里，并丢掉超期的。
        ///
        /// 必须能**增量**做：每轮刷新都重新折叠全部历史会很慢，而且
        /// 一旦原始样本被裁掉（48 小时之外），旧的那段就再也算不出来了。
        /// 所以流程是：先把即将被裁掉的原始样本折叠进小时层，
        /// 再把小时层超期的折叠进天层。
        ///
        /// 同一个桶如果已经存在，用「按样本数加权」重新平均，
        /// 而不是直接覆盖 —— 否则后一次折叠会把前一次的数据顶掉。
        /// </summary>
        public static List<Aggregate> MergeInto(List<Aggregate> existing,
            List<Aggregate> additions, int span, double now, double retainSeconds)
        {
            var map = new Dictionary<long, Aggregate>();

            void Put(Aggregate item, bool merge)
            {
                long key = (long)Math.Round(item.Start);
                if (merge && map.TryGetValue(key, out var known))
                {
                    long total = (long)known.Samples + item.Samples;
                    if (total <= 0) return;
                    known.Percent = (int)Math.Round(
                        ((double)known.Percent * known.Samples + (double)item.Percent * item.Samples)
                        / total);
                    known.Samples = (int)total;
                    known.MinPercent = Math.Min(known.MinPercent, item.MinPercent);
                    known.MaxPercent = Math.Max(known.MaxPercent, item.MaxPercent);
                    known.Charging |= item.Charging;
                }
                else
                {
                    map[key] = new Aggregate
                    {
                        Start = item.Start,
                        Span = span,
                        Percent = item.Percent,
                        MinPercent = item.MinPercent,
                        MaxPercent = item.MaxPercent,
                        Samples = item.Samples,
                        Charging = item.Charging,
                    };
                }
            }

            if (existing != null)
                foreach (var item in existing) Put(item, merge: false);
            if (additions != null)
                foreach (var item in additions) Put(item, merge: true);

            double cutoff = now - retainSeconds;
            var kept = map.Values
                .Where(a => a.End > cutoff)
                .OrderBy(a => a.Start)
                .ToList();

            // 窗口外的最后一条要保留：它是「很久以前还剩多少」的唯一来源，
            // 跟原始样本 Prune 的规则一致。
            var expired = map.Values.Where(a => a.End <= cutoff)
                .OrderByDescending(a => a.Start).FirstOrDefault();
            if (expired != null && (kept.Count == 0 || expired.Start < kept[0].Start))
            {
                kept.Insert(0, expired);
            }
            return kept;
        }

        /// <summary>
        /// 把指定时间范围的聚合序列转成图表点。
        ///
        /// 两条规则：
        ///   1. **数据点少于目标数时，直接用真实点**，不做重采样。
        ///      否则 24 小时只有 25 个小时桶却被拆成 60 个槽位，
        ///      每个值被复制 2~3 次，折线就变成阶梯形锯齿。
        ///      （这是实机预览截图里发现的问题，不是理论推演。）
        ///   2. 数据点更多时才重采样，取每个窗口的**加权平均**而不是抽样，
        ///      否则会略过真正的峰谷。
        ///
        /// 注意：本函数不再产出「空点」。空段由图表层按**时间间隔**判断
        /// （见 LineChart 的 MaxGapRatio）—— 用真实的空点去插值会伪造出
        /// 比实际更多的采样密度，而时间间隔是客观事实。
        /// </summary>
        public static List<ChartPoint> SamplePoints(List<Aggregate> aggregates,
            double from, double to, int targetPoints = 60)
        {
            var points = new List<ChartPoint>();
            if (to <= from || targetPoints < 2) return points;

            var items = (aggregates ?? new List<Aggregate>())
                .Where(a => a.End > from && a.Start < to)
                .OrderBy(a => a.Start)
                .ToList();
            if (items.Count == 0) return points;

            // ---- 规则 1：点不够密，直接用真实点 ----
            // 取桶中心作为横坐标，段宽为 0（原始样本）时直接用时间戳。
            if (items.Count <= targetPoints)
            {
                foreach (var item in items)
                {
                    if (!item.HasData) continue;
                    double center = item.Span > 0
                        ? item.Start + item.Span / 2.0
                        : item.Start;
                    points.Add(new ChartPoint(center, item.Percent, item.Charging, true));
                }
                return points;
            }

            // ---- 规则 2：点太多，重采样到固定数量 ----
            double step = (to - from) / targetPoints;
            int index = 0;
            for (int i = 0; i < targetPoints; i++)
            {
                double slotStart = from + step * i;
                double slotEnd = slotStart + step;

                long sum = 0;
                int count = 0;
                bool charging = false;
                while (index < items.Count && items[index].End <= slotStart) index++;

                int probe = index;
                while (probe < items.Count && items[probe].Start < slotEnd)
                {
                    var item = items[probe];
                    if (item.HasData && item.End > slotStart)
                    {
                        sum += (long)item.Percent * item.Samples;
                        count += item.Samples;
                        if (item.Charging) charging = true;
                    }
                    probe++;
                }

                points.Add(count > 0
                    ? new ChartPoint(slotStart + step / 2, (int)Math.Round((double)sum / count),
                        charging, true)
                    : new ChartPoint(slotStart + step / 2, 0, false, false));
            }
            return points;
        }

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
            return BuildBuckets(samples, now, 24, currentPercent, lastKnown);
        }

        /// <summary>
        /// 把样本分成 barCount 根柱子，跨越 windowHours 小时。
        ///
        /// 柱状图要能跟着时间选择走（与折线图共用 24h/7d/30d/1y/2y），
        /// 不能硬编码 24 根 × 1 小时：看 30 天时 24 根柱 = 一天 30 小时，
        /// 完全读不出信息。
        ///
        /// 柱宽（小时/根）= windowHours / barCount，按**本地时区**对齐到底层
        /// 聚合桶（小时或天），否则「今天」会从 UTC 凌晨划开。
        ///
        /// 设计取舍：不管窗口多长，**柱子数量固定**（默认 24）。这样卡片的
        /// 视觉密度不变，换来的是「每根柱代表的时间变长」—— 比「柱子越画越细
        /// 到看不清」好得多。
        /// </summary>
        public static List<Bucket> BuildBuckets(List<Sample> samples, double now,
            int windowHours, int? currentPercent = null, int lastKnown = -1,
            int barCount = 24)
        {
            if (barCount <= 0) barCount = 24;
            if (windowHours <= 0) windowHours = 24;

            var ordered = samples.OrderBy(s => s.T).ToList();

            // 桶宽按本地时区对齐：小时级向下取整到整点，天级取到本地零点。
            double stepSeconds = windowHours * 3600.0 / barCount;
            double localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow)
                .TotalSeconds;

            // 当前时间所在的桶的起点（向上对齐到桶边界）
            double alignedNow = Math.Floor((now + localOffset) / stepSeconds)
                * stepSeconds - localOffset;
            double windowStart = alignedNow - (barCount - 1) * stepSeconds;

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

            string FormatLabel(double start) => stepSeconds >= 86400
                ? TimeText.FromUnix(start).ToString("MM-dd")
                : TimeText.FromUnix(start).ToString("HH:mm");

            var buckets = new List<Bucket>(barCount);
            int idx = 0;
            int running = baseline;
            for (int i = 0; i < barCount; i++)
            {
                double start = alignedNow - (barCount - 1 - i) * stepSeconds;
                double end = start + stepSeconds;
                long sum = 0;
                int count = 0;
                int lastValue = running;
                while (idx < ordered.Count && ordered[idx].T < start) idx++;
                int j = idx;
                while (j < ordered.Count && ordered[j].T < end)
                {
                    sum += ordered[j].Percent;
                    lastValue = ordered[j].Percent;
                    count++;
                    j++;
                }
                if (count > 0) running = lastValue;

                buckets.Add(new Bucket
                {
                    Hour = FormatLabel(start),
                    // 长窗口下取**区间平均**而不是末值：
                    // 一天里末值可能正好是刚开机的一瞬间，会把整天代表错。
                    // 24 小时视图仍然是末值（count==1，两者等价）
                    Percent = count > 0 ? (int)Math.Round((double)sum / count) : running,
                    Count = count,
                    Sleeping = count == 0 && running > 0,
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

        /// <summary>
        /// 按任意时间范围统计 min/max 与在线时长。
        ///
        /// 为什么要单独一个：上面的 Analyze 里 min/max 固定取 24 小时窗口
        /// （续航预测需要固定窗口才可比）。但卡片标题会写「近 7 天 51%~89%」，
        /// 如果那个区间其实只算了 24 小时，就是在报假数。
        ///
        /// 超过原始保留期时改从聚合层统计 —— 否则选 30 天时
        /// 只有最后 48 小时参与，最大/最小值会失真。
        /// </summary>
        public static (int min, int max, int activeBuckets) RangeStats(
            List<Sample> samples, List<Aggregate> hours, List<Aggregate> days,
            double now, double windowSeconds)
        {
            int min = -1, max = -1, active = 0;

            void Take(int percent)
            {
                if (percent <= 0 || percent > 100) return;
                if (min < 0 || percent < min) min = percent;
                if (max < 0 || percent > max) max = percent;
            }

            if (windowSeconds <= RetainSeconds)
            {
                double from = now - windowSeconds;
                foreach (var sample in samples)
                {
                    if (sample.T < from || sample.T > now) continue;
                    Take(sample.Percent);
                }
                active = BuildBuckets(
                    samples.Where(s => s.T >= from && s.T <= now).ToList(),
                    now, (int)Math.Round(windowSeconds / 3600), null, -1)
                    .Count(b => b.Count > 0);
                return (min, max, active);
            }

            double cutoff = now - windowSeconds;
            var source = windowSeconds <= HourAggregateRetain ? hours : days;
            if (source == null) return (min, max, 0);

            foreach (var item in source)
            {
                if (!item.HasData || item.End <= cutoff || item.Start > now) continue;
                Take(item.MinPercent);
                Take(item.MaxPercent);
            }

            // 在线时长用「有数据的桶占比 × 窗口小时数」估算。
            // 聚合层的 Span 是客观值，比拿样本数倒推更靠得住。
            int span = windowSeconds <= HourAggregateRetain ? HourSpan : DaySpan;
            int counted = 0;
            foreach (var item in source)
            {
                if (item.HasData && item.End > cutoff && item.Start <= now) counted++;
            }
            active = span > 0 ? counted * span / 3600 : 0;
            return (min, max, active);
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
        private List<Aggregate>? _hours;
        private List<Aggregate>? _days;
        private double _lastPersist;

        public string Path { get; }

        public static string DefaultDirectory
        {
            // 复用 Settings 的目录解析：那里包含便携 / 改名搬家 / 失败回退
            get { return Settings.Directory; }
        }

        public HistoryStore(string deviceKey, string? directory = null)
        {
            string safe = new string(deviceKey.Select(
                c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            string dir = directory ?? DefaultDirectory;
            Path = System.IO.Path.Combine(dir, "history_" + safe + ".json");
        }

        /// <summary>
        /// 读历史文件。一个文件同时装三层数据（见 HistoryAnalysis 的分层注释）。
        ///
        /// 容错要求很高 —— 这个文件是「攒了几周才有用」的数据，宁可丢掉
        /// 损坏的那一段，也不能因为一个字段写坏就整份清零：
        ///   * samples / hours / days 三个数组各自独立 try
        ///   * 单条解析失败只跳过那一条
        ///   * v1 旧文件只有 samples，读出来小时后层为空，下次折叠自动补齐
        /// </summary>
        private void LoadAll()
        {
            if (_samples != null) return;

            var samples = new List<Sample>();
            var hours = new List<Aggregate>();
            var days = new List<Aggregate>();

            try
            {
                if (File.Exists(Path))
                {
                    using var stream = File.OpenRead(Path);
                    using var doc = JsonDocument.Parse(stream);

                    ReadSamples(doc, samples);
                    ReadAggregates(doc, "hours", HistoryAnalysis.HourSpan, hours);
                    ReadAggregates(doc, "days", HistoryAnalysis.DaySpan, days);
                }
            }
            catch (Exception)
            {
                // 整个文件坏了：保留内存里已收集到的，剩下重新开始
            }

            samples.Sort((a, b) => a.T.CompareTo(b.T));
            hours.Sort((a, b) => a.Start.CompareTo(b.Start));
            days.Sort((a, b) => a.Start.CompareTo(b.Start));

            _samples = samples;
            _hours = hours;
            _days = days;
        }

        private static void ReadSamples(JsonDocument doc, List<Sample> target)
        {
            if (!doc.RootElement.TryGetProperty("samples", out var array)) return;
            if (array.ValueKind != JsonValueKind.Array) return;

            foreach (var item in array.EnumerateArray())
            {
                try
                {
                    int percent = item.GetProperty("percent").GetInt32();
                    if (percent <= 0 || percent > 100) continue;
                    double t = item.GetProperty("t").GetDouble();
                    bool charging = item.TryGetProperty("charging", out var c)
                        && c.GetBoolean();
                    target.Add(new Sample(t, percent, charging));
                }
                catch (Exception) { }
            }
        }

        private static void ReadAggregates(JsonDocument doc, string name, int span,
            List<Aggregate> target)
        {
            if (!doc.RootElement.TryGetProperty(name, out var array)) return;
            if (array.ValueKind != JsonValueKind.Array) return;

            foreach (var item in array.EnumerateArray())
            {
                try
                {
                    int percent = item.GetProperty("percent").GetInt32();
                    if (percent <= 0 || percent > 100) continue;
                    target.Add(new Aggregate
                    {
                        Start = item.GetProperty("start").GetDouble(),
                        Span = span,
                        Percent = percent,
                        MinPercent = item.TryGetProperty("min", out var lo) ? lo.GetInt32() : percent,
                        MaxPercent = item.TryGetProperty("max", out var hi) ? hi.GetInt32() : percent,
                        Samples = item.TryGetProperty("n", out var n) ? n.GetInt32() : 1,
                        Charging = item.TryGetProperty("chg", out var chg) && chg.GetBoolean(),
                    });
                }
                catch (Exception) { }
            }
        }

        private List<Sample> Load()
        {
            LoadAll();
            return _samples!;
        }

        /// <summary>
        /// 把即将被裁掉的原始样本折叠进小时层，再把超期的小时层折叠进天层。
        ///
        /// 为什么不能「每次全量重算」：原始样本只保留 48 小时，
        /// 一旦被 Prune 删掉，上周的数据就只能靠小时层；而小时层保留 30 天，
        /// 再往前的只能靠天层。所以折叠必须**在裁剪之前**做，且只能做一次。
        /// </summary>
        private void Compact(double now)
        {
            var samples = _samples!;
            var hours = _hours!;
            var days = _days!;

            double sampleCutoff = now - HistoryAnalysis.RetainSeconds;

            // ---- 1. 即将离开原始窗口的样本 → 小时层 ----
            // 留 1 小时余量：桶边界对齐后尾部样本可能刚跨过 cutoff
            double foldBefore = sampleCutoff + HistoryAnalysis.HourSpan;
            var expiring = samples.Where(s => s.T < foldBefore).ToList();
            if (expiring.Count > 0)
            {
                _hours = HistoryAnalysis.MergeInto(hours,
                    HistoryAnalysis.Fold(expiring, HistoryAnalysis.HourSpan),
                    HistoryAnalysis.HourSpan, now, HistoryAnalysis.HourAggregateRetain);

                // 不能真删：续航预测与柱状图还要用 24 小时内的原始样本。
                // Prune 会按自己的窗口处理，这里只负责折叠。
            }

            // ---- 2. 超期的小时层 → 天层 ----
            double hourCutoff = now - HistoryAnalysis.HourAggregateRetain;
            var oldHours = _hours!.Where(a => a.End <= hourCutoff).ToList();
            if (oldHours.Count > 0)
            {
                // 小时层已经是「每小时一个平均值」，直接当样本再聚合即可：
                // 每个小时贡献 1 个点，天桶算出来就是其内各小时的均值。
                var asSamples = oldHours
                    .Where(a => a.HasData)
                    .Select(a => new Sample(a.Start, a.Percent, a.Charging))
                    .ToList();

                _days = HistoryAnalysis.MergeInto(days,
                    HistoryAnalysis.Fold(asSamples, HistoryAnalysis.DaySpan),
                    HistoryAnalysis.DaySpan, now, HistoryAnalysis.DayAggregateRetain);
            }
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

                // 先折叠再裁剪：原始样本一旦被 Prune 删掉，那段就只能靠
                // 聚合层了（小时层保留 30 天，天层 24 个月）。
                // Compact 自己只看窗口，重复调用不会重复累加 ——
                // 已经在聚合里的样本不会被再次合并（见 MergeInto 的按桶合并）。
                Compact(current);

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

        /// <summary>
        /// 指定时间范围的图表点。
        ///
        /// 层次选择决定了「看得到多久的历史」：
        ///   * ≤ 48 小时：直接用原始样本（最高分辨率）
        ///   * ≤ 30 天  ：用小时聚合
        ///   * 更长     ：用天聚合（24 个月）
        /// 阈值取「正好等于该层的保留时长」：超过之后原始样本早就被裁了，
        /// 再拿它画会出现大段空白。
        /// </summary>
        public List<ChartPoint> Points(double windowSeconds, double? now = null,
            int targetPoints = 60)
        {
            double current = now ?? TimeText.UnixNow();
            double from = current - Math.Max(3600, windowSeconds);

            lock (_gate)
            {
                LoadAll();

                if (windowSeconds <= HistoryAnalysis.RetainSeconds)
                {
                    // 原始样本 → 临时折叠成 1 个点粒度交给重采样，
                    // 不直接当聚合用（样本间隔不均匀，会画出锯齿）
                    var raw = _samples!
                        .Where(s => s.T >= from && s.T <= current)
                        .Select(s => new Aggregate
                        {
                            Start = s.T,
                            Span = 0,
                            Percent = s.Percent,
                            MinPercent = s.Percent,
                            MaxPercent = s.Percent,
                            Samples = 1,
                            Charging = s.Charging,
                        }).ToList();
                    return HistoryAnalysis.SamplePoints(raw, from, current, targetPoints);
                }

                var source = windowSeconds <= HistoryAnalysis.HourAggregateRetain
                    ? _hours!
                    : _days!;
                return HistoryAnalysis.SamplePoints(source, from, current, targetPoints);
            }
        }

        /// <summary>
        /// 指定时间范围的柱状图数据。
        ///
        /// 与 Points 一样按层次选择数据源：窗口超过原始保留期就换成聚合层，
        /// 否则拉 30 天时只有最后 48 小时能画出柱子，前面全空。
        ///
        /// 返回的 Bucket 数量固定（默认 24），每根柱代表 windowHours/24 小时。
        /// </summary>
        public List<Bucket> Buckets(double windowHours, int? currentPercent,
            bool charging, double? now = null, int barCount = 24)
        {
            double current = now ?? TimeText.UnixNow();
            double windowSeconds = Math.Max(3600, windowHours * 3600);

            lock (_gate)
            {
                LoadAll();

                // 窗口内只用原始样本 → 直接交给 BuildBuckets（它自带分桶）
                if (windowSeconds <= HistoryAnalysis.RetainSeconds)
                {
                    var fresh = _samples!
                        .Where(s => s.T >= current - windowSeconds && s.T <= current)
                        .ToList();
                    return HistoryAnalysis.BuildBuckets(fresh, current,
                        (int)Math.Round(windowHours), currentPercent, -1, barCount);
                }

                // 长窗口：先把聚合层展开成样本再分桶。
                // 每个聚合贡献一条（用它的平均值），样本数用 Samples 作为权重
                // —— 直接当成单条样本会把「一天只有 2 条记录」和
                // 「一天有 200 条记录」看得一样重。
                //
                // 这里不追求像素级精度（柱状图本来就只看大体形状），
                // 但必须保证「看得见的都是有数据的时段」。
                var source = windowSeconds <= HistoryAnalysis.HourAggregateRetain
                    ? _hours!
                    : _days!;

                var expanded = source
                    .Where(a => a.HasData && a.End > current - windowSeconds)
                    .SelectMany(a =>
                    {
                        // 用平均值重复 Samples 次会在长窗口里把内存拉爆，
                        // 所以重复次数封顶 20：权重足够体现相对量级，
                        // 又不会因为一天 200 条样本就多存 200 个点。
                        int weight = Math.Clamp(a.Samples, 1, 20);
                        var list = new List<Sample>(weight);
                        for (int i = 0; i < weight; i++)
                        {
                            list.Add(new Sample(a.Start, a.Percent, a.Charging));
                        }
                        return list;
                    })
                    .ToList();

                return HistoryAnalysis.BuildBuckets(expanded, current,
                    (int)Math.Round(windowHours), currentPercent, -1, barCount);
            }
        }

        /// <summary>
        /// 指定时间范围的统计（min / max / 在线小时），用于卡片标题。
        /// 与 Analyze 分开：Analyze 的 min/max 固定 24 小时窗口（预测用），
        /// 这里跟随用户选的时间范围。
        /// </summary>
        public (int min, int max, int activeHours) Stats(double windowHours,
            double? now = null)
        {
            double current = now ?? TimeText.UnixNow();
            lock (_gate)
            {
                LoadAll();
                return HistoryAnalysis.RangeStats(_samples!, _hours!, _days!,
                    current, Math.Max(3600, windowHours * 3600));
            }
        }

        /// <summary>当前已积累的聚合条数，供设置面板/调试显示</summary>
        public (int hours, int days) AggregateCounts()
        {
            lock (_gate)
            {
                LoadAll();
                return (_hours!.Count, _days!.Count);
            }
        }

        private void Persist(double now)
        {
            _lastPersist = now;

            // ⚠️ 必须先 LoadAll：Persist 可能在「从未读过」的实例上被调用
            // （进程启动后什么都不做就退出 → Flush → 这里）。
            // 不先加载，_samples 就是 null，会把整个文件写成空数组 ——
            // 用户攒了几周的曲线一次退出就没了。
            LoadAll();

            var samples = _samples ?? new List<Sample>();
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new
                {
                    version = 2,
                    samples = samples.Select(s => new
                    {
                        t = Math.Round(s.T, 3),
                        percent = s.Percent,
                        charging = s.Charging,
                    }).ToList(),
                    // 聚合层字段名刻意缩短（start/percent/n/chg）：
                    // 730 条 × 多台设备，长字段名会把文件白撑大好几倍。
                    hours = (_hours ?? new List<Aggregate>()).Select(WriteAggregate).ToList(),
                    days = (_days ?? new List<Aggregate>()).Select(WriteAggregate).ToList(),
                };
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = false,
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

        private static object WriteAggregate(Aggregate a)
        {
            return new
            {
                start = Math.Round(a.Start, 0),
                percent = a.Percent,
                min = a.MinPercent,
                max = a.MaxPercent,
                n = a.Samples,
                chg = a.Charging,
            };
        }
    }
}
