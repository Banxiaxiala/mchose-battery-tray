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

        /// <summary>
        /// 这台**物理设备**的唯一标识，形如 `hid:046D:C54D:SN1234` 或
        /// `bt:44:5E:CD:40:7C:2A`。
        ///
        /// 为什么需要它：以前用「型号 key」当身份（如 `razer`），
        /// 两台同型号设备接入时会共用同一份历史文件、同一个开关、
        /// 同一个备注 —— 后读到的那台会覆盖前一台的曲线。
        ///
        /// 拿不到唯一标识时退化为 `model:<型号key>`，此时行为与旧版一致
        /// （同型号只算一台），但界面上会标注「无法区分实例」。
        /// </summary>
        public string Identity { get; set; } = "";

        /// <summary>
        /// 这个读数的**数据时间**。DateTime.MinValue 表示「不知道 / 不适用」。
        ///
        /// 为什么需要：蓝牙通道读的是 Windows 缓存，设备早关了属性还在。
        /// 而且 `IsConnected` 在链路刚断时还没更新（实测差几十秒到几分钟）。
        /// 真正能判「这台还在用」的是**电量被刷新的时间** ——
        /// 在用的设备系统会持续更新，关掉的会停在关机那一刻。
        ///
        /// HID 协议是实时读的，这个字段留 MinValue（永远算新鲜）。
        /// </summary>
        public DateTime DataAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// 「缓存数据仍然可信」的时间窗。
        ///
        /// ⚠️ 这不是「设备是否在线」的判据 —— 那个看 Connected。
        ///
        /// Windows 只在**电量发生变化时**才更新蓝牙属性的时间戳，
        /// 不是定时刷新。所以一台稳稳 90% 的耳机，时间戳可能十几分钟
        /// 甚至几小时不动，但它明明好好连着。
        ///
        /// 早期设成 10 分钟，结果用户插着耳机点刷新也加载不出来 ——
        /// 这个窗只该用来挡「很久以前就关掉、但 Connected 还没更新」
        /// 这种过渡态，取 6 小时足够。
        /// </summary>
        public static readonly TimeSpan TrustedCacheAge = TimeSpan.FromHours(6);

        /// <summary>
        /// 电量数据的「新鲜度后缀」，用于界面上如实标注。
        ///
        /// 为什么需要：蓝牙通道读的是 Windows 缓存，而 Windows 只在
        /// **设备主动上报**时才更新它。软件无法触发重读（实测
        /// CM_Reenumerate_DevNode 被拒、pnputil 需管理员、
        /// 枚举类设备只读属性不查询设备）。
        ///
        /// 用户的耳机实测能停 36 分钟不动。数字本身没错，但看起来
        /// 像「刚刚读到」就是误导 —— 所以把时间说出来。
        ///
        /// 返回值不含前导分隔符，空串表示「足够新，不必提」。
        /// </summary>
        public static string DescribeAge(DateTime dataAt, DateTime? now = null)
        {
            if (dataAt == DateTime.MinValue) return "";

            var current = now ?? DateTime.Now;
            var delta = current - dataAt;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;   // 时钟回拨

            // 5 分钟以内不提：默认 30 秒刷新一次，提了反而是噪音
            if (delta < TimeSpan.FromMinutes(5)) return "";

            if (delta < TimeSpan.FromHours(1))
            {
                return ((int)delta.TotalMinutes) + " 分钟前更新";
            }
            if (delta < TimeSpan.FromDays(1))
            {
                return ((int)delta.TotalHours) + " 小时前更新";
            }
            return ((int)delta.TotalDays) + " 天前更新";
        }

        /// <summary>
        /// 读数是否「新鲜」：要么是实时协议（无时间戳），
        /// 要么缓存时间在 maxAge 以内。
        /// </summary>
        public bool IsFresh(TimeSpan maxAge, DateTime? now = null)
        {
            if (DataAt == DateTime.MinValue) return true;   // 实时协议
            var current = now ?? DateTime.Now;
            return current - DataAt <= maxAge;
        }

        /// <summary>
        /// 用户可改的备注名。空表示用 <see cref="Name"/>。
        ///
        /// 存在 Reading 上而不是设置里，是因为它跟着**这一台**走：
        /// 拔掉设备再插回来，只要 Identity 一致，备注就还在。
        /// </summary>
        public string Nickname { get; set; } = "";

        /// <summary>卡片/提示上显示的最终名字：备注优先，其次型号</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Nickname)) return Nickname.Trim();
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                return "";
            }
        }

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

    /// <summary>
    /// 长期历史的一个聚合区间（小时级或天级）。
    ///
    /// 为什么需要它：原始样本只保留 48 小时（见 HistoryAnalysis.RetainSeconds）。
    /// 要做到「看 24 个月趋势」又不把文件飈到几十 MB，就必须把旧样本
    /// 折叠成聚合值 —— 聚合后每个小时/每天只留 4 个数字。
    ///
    /// Start 是区间起点（Unix 秒，本地时区对齐）。Percent / MinPercent /
    /// MaxPercent 是该区间内的统计值，Samples 是参与统计的原始样本数。
    /// 电量用**平均值**而不是取最后一个：聚合之后单点会随机跳动，
    /// 取末值会让折线按采样时间抖动，看不出真实趋势。
    /// </summary>
    public sealed class Aggregate
    {
        public double Start { get; set; }
        /// <summary>区间长度（秒）。小时级 3600，天级 86400</summary>
        public int Span { get; set; }
        public int Percent { get; set; }
        public int MinPercent { get; set; }
        public int MaxPercent { get; set; }
        /// <summary>区间内有多少个原始样本。0 = 补出来的空段</summary>
        public int Samples { get; set; }
        /// <summary>区间内是否出现过充电</summary>
        public bool Charging { get; set; }

        public bool HasData { get { return Samples > 0; } }
        public double End { get { return Start + Span; } }
    }

    /// <summary>
    /// 折线图上的一个点。与 Aggregate 分开是因为图表只关心「画在哪、画多高」，
    /// 而且折线图会把小时级聚合再重采样成固定数量的点。
    /// </summary>
    public struct ChartPoint
    {
        public double T;
        public int Percent;
        public bool Charging;
        /// <summary>该点代表的数据量；false 表示补出来的空段（不画线、断点）</summary>
        public bool HasData;

        public ChartPoint(double t, int percent, bool charging, bool hasData)
        {
            T = t;
            Percent = percent;
            Charging = charging;
            HasData = hasData;
        }
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

        /// <summary>
        /// 折线图用的点（已按当前选择的时间范围重采样）。
        /// 与 Buckets 共存：柱状图看每小时分布，折线图看趋势，两者用途不同。
        /// </summary>
        public System.Collections.Generic.List<ChartPoint> Points = new();

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

    /// <summary>
    /// 设备身份串的构造规则。
    ///
    /// 抽成纯函数是为了能脱机断言 —— 「两台同型号会不会撞同一个身份」
    /// 这件事必须靠测试保证，不能靠肉眼。
    /// </summary>
    public static class DeviceIdentity
    {
        /// <summary>蓝牙设备：用 MAC，最可靠</summary>
        public static string Bluetooth(string mac)
        {
            string clean = (mac ?? "").Trim();
            if (clean.Length == 0) return "";
            return "bt:" + clean.ToUpperInvariant();
        }

        /// <summary>
        /// HID 设备：VID:PID + 序列号。
        ///
        /// 序列号为空时**不生成身份** —— 否则同型号两台会得到同一个串，
        /// 反而制造「看起来唯一、实际撞车」的假象。调用方必须继续降级。
        /// </summary>
        public static string Hid(int vendorId, int productId, string? serial)
        {
            string clean = (serial ?? "").Trim();
            if (clean.Length == 0) return "";
            return $"hid:{vendorId:X4}:{productId:X4}:{clean}";
        }

        /// <summary>
        /// 罗技：接收器序列 + 设备槽位。
        ///
        /// 罗技槽位号在同一个接收器内唯一，接收器序列号又区分了不同接收器，
        /// 所以两者组合能区分「同一个接收器上的多个槽位」。
        /// </summary>
        public static string LogitechSlot(string? receiverSerial, int slot)
        {
            string clean = (receiverSerial ?? "").Trim();
            if (clean.Length == 0) return "";
            return $"hid:046D:{clean}:slot{slot}";
        }

        /// <summary>
        /// 降级身份：只有型号，不区分实例。
        ///
        /// 调用方拿到这种身份时应当在界面上标注「无法区分实例」，
        /// 因为同型号两台会共用历史与开关。
        /// </summary>
        public static string ModelOnly(string modelKey)
        {
            return "model:" + (modelKey ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 设备身份别名表：把一个通道的身份映射到「主身份」。
        ///
        /// 为什么要这一层：同一台设备切换连接方式时，两条通道算出的身份
        /// 天然不同 —— 蓝牙用 MAC，2.4G 用接收器序列号。如果不做映射：
        ///
        ///   用蓝牙时  身份 = bt:44:5E:CD:40:7C:2A  → 备注「客厅耳机」
        ///   切到 2.4G 身份 = hid:046D:RECV01:slot1   → 备注丢失
        ///
        /// 用户看到的是「备注没了」「历史断了」，像是换了一台设备。
        ///
        /// 解决方式：设备**第一次被识别**时记下它的身份，之后其它通道
        /// 报上来的身份通过 Alias 表收敛到同一个主身份。
        /// 判定「是不是同一台」靠型号 key + 最近通道的时间邻近性，
        /// 而不是硬编码 —— 因为程序无法从协议层直接得知两者是同一台。
        /// </summary>
        public static string ResolveIdentity(string reported, string modelKey,
            string preferredPrimary)
        {
            if (string.IsNullOrEmpty(reported)) return ModelOnly(modelKey);
            if (string.IsNullOrEmpty(preferredPrimary)) return reported;
            return preferredPrimary;
        }

        /// <summary>是不是降级身份（无法区分同型号实例）</summary>
        public static bool IsModelOnly(string? identity)
        {
            return identity != null && identity.StartsWith("model:",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 屏幕坐标夹取。
    ///
    /// 存在理由：`Math.Clamp(value, min, max)` 在 min &gt; max 时**直接抛异常**
    /// （"max cannot be less than min"）。而窗口尺寸由内容决定 ——
    /// 设备一多（实测 14 台），卡片高度就会超过屏幕工作区，
    /// 此时 `work.Bottom - height - 8` 算出来是负数，程序当场崩溃。
    ///
    /// 这种情况下的正确行为是**贴住起始边**：宁可让底部超出屏幕
    /// （配合滚动条，用户还能看到全部内容），也绝不能崩。
    ///
    /// 抽成纯函数是为了能脱机断言 —— 这个 bug 第一次出现时
    /// 没有测试，靠用户截图才发现。
    /// </summary>
    public static class ScreenClamp
    {
        /// <summary>
        /// 把 value 夹进 [min, max]；当 min &gt; max（放不下）时贴住 fallback。
        /// </summary>
        public static double Into(double value, double min, double max, double fallback)
        {
            if (max < min)
            {
                // 放不下：统一贴到起始边（min），但不低于 fallback。
                //
                // 为什么不保留 value：value 本身可能比 min 更靠外（甚至为负），
                // 那会让窗口左上角跑到屏幕外，用户连标题栏都看不到。
                // 「贴住起始边」是可预测的：至少能看到窗口的开头。
                return Math.Max(fallback, min);
            }
            return Math.Clamp(value, min, max);
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
