// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [设置必须落盘] 用户的每一项自定义都要写进数据目录的 settings.json，
//    重启后保持。只放内存里会导致「每次开机都回到默认」。
// 2. [便携优先] 数据目录默认就在 **exe 旁边**（见 Directory）：
//    用户把整个文件夹拷到 U 盘 / 另一台机器，设置、历史、日志全部跟着走。
//    程序目录写不进去（如放在 Program Files）才回退到 %LOCALAPPDATA%。
//    旧版数据在 %LOCALAPPDATA%\外设电量，首次启动会迁过来，不能清零。
// 3. [字段只增不改] 新增字段必须给默认值，删字段要保留兼容读取（旧文件缺字段
//    时用默认值即可，不要抛异常）。用户的配置文件不会跟着版本升级。
// 4. [区间校验集中在此] 刷新间隔、卡片宽度的合法范围只允许定义在这里，
//    界面层不许自己写 magic number 判断。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBattery.Services
{
    public sealed class Settings
    {
        public const int IntervalMin = 10;
        public const int IntervalMax = 600;
        public const int DefaultInterval = 30;

        public const double WidthMin = 240;
        public const double WidthMax = 420;
        public const double DefaultWidth = 288;

        public const double ChartHeightMin = 34;
        public const double ChartHeightMax = 110;
        public const double DefaultChartHeight = 54;

        /// <summary>卡片固定高度的范围。0 表示「由内容自动撑开」</summary>
        public const double CardHeightMin = 240;
        public const double CardHeightMax = 900;

        /// <summary>采样间隔（秒）。历史与续航预测依赖这个密度</summary>
        [JsonPropertyName("interval")]
        public int Interval { get; set; } = DefaultInterval;

        /// <summary>卡片宽度（DIP）</summary>
        [JsonPropertyName("card_width")]
        public double CardWidth { get; set; } = DefaultWidth;

        /// <summary>柱状图高度（DIP）</summary>
        [JsonPropertyName("chart_height")]
        public double ChartHeight { get; set; } = DefaultChartHeight;

        /// <summary>
        /// 卡片固定高度（DIP）。0 = 由内容自动撑开（SizeToContent）。
        /// 设了固定值会把内容裁掉，但能保证卡片尺寸在多设备间保持一致。
        /// </summary>
        [JsonPropertyName("card_height")]
        public double CardHeight { get; set; } = 0;

        // ---------------------------------------------------------- 历史图表
        /// <summary>
        /// 图表样式："bar" = 24 小时柱状图，"line" = 折线趋势图。
        /// 存字符串而不是枚举，是因为 System.Text.Json 默认把枚举写成数字，
        /// 日后调枚举顺序会静默改变用户已有配置的含义。
        /// </summary>
        [JsonPropertyName("chart_style")]
        public string ChartStyle { get; set; } = "bar";

        /// <summary>
        /// 折线图的时间跨度（小时）。可选 24 / 168(7天) / 720(30天)
        /// / 8760(365天) / 17520(730天)。
        /// 超过 30 天时数据来自天级聚合（保留 24 个月），分辨率自然下降。
        /// </summary>
        [JsonPropertyName("chart_range_hours")]
        public int ChartRangeHours { get; set; } = 24;

        // ---------------------------------------------------------- 卡片容量
        /// <summary>
        /// 卡片最多显示多少台设备。
        ///
        /// 为什么需要上限：卡片高度由内容决定，一台设备约 130 DIP。
        /// 设备多了总高度会超过屏幕工作区 —— 实测 14 台时约 2900 DIP，
        /// 而 1080p 的工作区只有 1019。虽然加了自动限高 + 滚动条兜底，
        /// 但「一屏塞十几台、每台只剩一条」的观感很差。
        ///
        /// 取 5：1920×1080 下 5 台约 650 DIP，加上标题栏仍在一屏内，
        /// 不需要滚动就能看全。超过 5 台的设备不进卡片，但仍会在托盘环上
        /// 出现（环位按启用顺序排，最多 3 圈），悬停提示与低电量提醒也照常。
        /// </summary>
        public const int MaxCardDevicesMin = 1;
        public const int MaxCardDevicesMax = 8;
        public const int DefaultMaxCardDevices = 5;

        /// <summary>卡片最多显示几台设备</summary>
        [JsonPropertyName("max_card_devices")]
        public int MaxCardDevices { get; set; } = DefaultMaxCardDevices;

        public static int ClampMaxCardDevices(int value)
        {
            return Math.Clamp(value, MaxCardDevicesMin, MaxCardDevicesMax);
        }

        // ---------------------------------------------------------- 设备开关
        /// <summary>
        /// 被关闭的设备（history key 列表）。
        ///
        /// 关闭 = **彻底不参与**：不探测、不占托盘环、不进卡片、不写历史。
        /// 这和「离线」是两件事：离线是设备没应答，下次可能回来；
        /// 关闭是用户明确的「这台我不再看了」。
        ///
        /// 用 history key 而不是显示名：名字会改（「鼠标」→「PRO X 2」），
        /// key 是稳定的文件标识。
        ///
        /// 存成列表而不是每台一个布尔字段，是因为设备清单本身是代码里的表
        /// （见 Program.BuildDevices）；以后加设备不需要再动设置结构。
        /// </summary>
        [JsonPropertyName("disabled_devices")]
        public List<string> DisabledDevices { get; set; } = new();

        /// <summary>
        /// 是否已经做过「首次启动自动探测」。
        ///
        /// 为什么需要这个标记：首次启动时要把「实际连着哪些设备」探出来
        /// （否则内置的 11 个未验证型号全都默认关闭，用户接了也看不到）。
        /// 但探测本身要挨个枚举、每个未接的设备都要等超时，
        /// 14 台能拖十几秒 —— 不能每轮都做。
        ///
        /// 所以只做一次：探完把结果写进 disabled_devices，然后置 true。
        /// 之后完全以那次的结论为准，用户在设置里改了也不会被覆盖。
        /// </summary>
        [JsonPropertyName("auto_probe_done")]
        public bool AutoProbeDone { get; set; }

        // ---------------------------------------------------------- 备注
        /// <summary>
        /// 设备备注：Identity → 用户起的名字。
        ///
        /// 为什么按 Identity（而不是型号 key）存：备注要跟着**那台物理设备**走。
        /// 两台同型号的鼠标，用户可能一台叫「主力」一台叫「备用」，
        /// 按型号存会让两台共用同一个备注。
        ///
        /// Identity 可能是降级形式（`model:razer`），此时备注对整个型号生效 ——
        /// 这是可以接受的：既然分不出实例，备注也分不出来才合理。
        /// </summary>
        [JsonPropertyName("device_nicknames")]
        public Dictionary<string, string> DeviceNicknames { get; set; } = new();

        /// <summary>取备注；没有则返回空串</summary>
        public string GetNickname(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return "";
            return DeviceNicknames.TryGetValue(identity, out string? name)
                ? (name ?? "")
                : "";
        }

        // ---------------------------------------------------------- 通道别名
        /// <summary>
        /// 「备选通道身份 → 主身份」的映射。
        ///
        /// 场景：耳机用蓝牙时身份是 `bt:44:5E:CD:40:7C:2A`，
        /// 切到 2.4G 后是 `hid:1532:0555:&lt;接收器序列&gt;`。
        /// 不收敛的话，用户切个连接方式就发现备注没了、历史断了。
        ///
        /// 收敛规则：同一**型号槽位**（HistoryKey）第一次成功识别时记下
        /// 那个身份当主身份；之后该槽位报上来的其它身份都写进这张表，
        /// 指回主身份。
        ///
        /// 为什么按槽位而不是按设备：程序无法从协议层得知「蓝牙那个 MAC
        /// 与这个 2.4G 接收器是同一台耳机」。但一个槽位同时只服务一台
        /// 设备（同型号多台才需要多实例，那是另一个改造），所以
        /// 「同一槽位 = 同一台」这个近似是安全且可预期的。
        /// </summary>
        [JsonPropertyName("channel_aliases")]
        public Dictionary<string, string> ChannelAliases { get; set; } = new();

        /// <summary>
        /// 把某槽位报上来的身份收敛成主身份。
        ///
        /// 第一次调用会把这个身份登记为主身份；之后再来的不同身份
        /// 会被记进别名表并返回主身份。
        /// </summary>
        public string ResolveChannelIdentity(string historyKey, string reported)
        {
            if (string.IsNullOrEmpty(historyKey) || string.IsNullOrEmpty(reported))
            {
                return reported ?? "";
            }

            string primaryKey = "slot:" + historyKey.ToLowerInvariant();
            if (!ChannelAliases.TryGetValue(primaryKey, out string? primary)
                || string.IsNullOrEmpty(primary))
            {
                // 首次：这个身份就是主身份
                ChannelAliases[primaryKey] = reported;
                return reported;
            }

            if (string.Equals(primary, reported, StringComparison.Ordinal))
            {
                return primary;
            }

            // 不同身份：登记别名，但仍返回主身份，保证备注/历史连续
            ChannelAliases[reported] = primary;
            return primary;
        }

        /// <summary>查别名：如果 reported 是某个主身份的别名，返回它</summary>
        public string LookupAlias(string reported)
        {
            if (string.IsNullOrEmpty(reported)) return "";
            return ChannelAliases.TryGetValue(reported, out string? primary)
                && !string.IsNullOrEmpty(primary)
                ? primary
                : "";
        }

        /// <summary>备注的长度上限（脱离 UI 也不该无限长）</summary>
        public const int NicknameMaxLength = 24;

        /// <summary>设置备注。空值表示清除。返回 true 表示真的变了。</summary>
        public bool SetNickname(string identity, string? nickname)
        {
            if (string.IsNullOrEmpty(identity)) return false;

            // 这里必须自己截断，不能指望 Sanitize()：
            // Sanitize 只在「读配置」时调用，用户正在输入时不会经过它，
            // 否则要重启才生效 —— 那就成了「输入框里显示 100 字、
            // 实际只存 24 字」的诡异状态。
            string clean = (nickname ?? "").Trim();
            if (clean.Length > NicknameMaxLength)
            {
                clean = clean.Substring(0, NicknameMaxLength).Trim();
            }

            string current = GetNickname(identity);
            if (string.Equals(current, clean, StringComparison.Ordinal)) return false;

            if (clean.Length == 0) DeviceNicknames.Remove(identity);
            else DeviceNicknames[identity] = clean;
            return true;
        }

        /// <summary>
        /// 用户**手动**关闭过的设备（history key）。
        ///
        /// 与 DisabledDevices 的区别：
        ///   DisabledDevices  = 当前关闭的全部设备（含首次探测没找到的）
        ///   ManualDisabled   = 用户亲手取消勾选的
        ///
        /// 为什么需要区分：首次探测没找到的设备应当继续参与「后续自动检测」
        /// （用户以后可能插上）；而用户手动关掉的不能因为设备还插着就被
        /// 自动重新打开 —— 那是违背他的明确意愿。
        /// </summary>
        [JsonPropertyName("manual_disabled")]
        public List<string> ManualDisabled { get; set; } = new();

        /// <summary>这台设备是否被用户手动关闭过</summary>
        public bool IsManuallyDisabled(string historyKey)
        {
            if (string.IsNullOrEmpty(historyKey)) return false;
            return ManualDisabled.Exists(k =>
                string.Equals(k, historyKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>记录/撤销「用户手动关闭」标记</summary>
        public void MarkManualDisabled(string historyKey, bool disabled)
        {
            if (string.IsNullOrEmpty(historyKey)) return;
            ManualDisabled.RemoveAll(k =>
                string.Equals(k, historyKey, StringComparison.OrdinalIgnoreCase));
            if (disabled) ManualDisabled.Add(historyKey);
        }

        /// <summary>某台设备是否被用户关闭了监控</summary>
        public bool IsDeviceEnabled(string historyKey)
        {
            if (string.IsNullOrEmpty(historyKey)) return true;
            return !DisabledDevices.Exists(k =>
                string.Equals(k, historyKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 设置某台设备的开关状态。返回 true 表示真的变了 ——
        /// 调用方据此决定要不要重算环位与刷新。
        /// </summary>
        public bool SetDeviceEnabled(string historyKey, bool enabled)
        {
            if (string.IsNullOrEmpty(historyKey)) return false;
            bool currentlyEnabled = IsDeviceEnabled(historyKey);
            if (currentlyEnabled == enabled) return false;

            DisabledDevices.RemoveAll(k =>
                string.Equals(k, historyKey, StringComparison.OrdinalIgnoreCase));
            if (!enabled) DisabledDevices.Add(historyKey);
            return true;
        }

        /// <summary>可选的时间跨度（小时）。界面按这个列表生成切换按钮</summary>
        public static readonly int[] ChartRangeOptions = { 24, 168, 720, 8760, 17520 };

        /// <summary>时间跨度的显示名</summary>
        public static string ChartRangeLabel(int hours)
        {
            switch (hours)
            {
                case 24: return "24 小时";
                case 168: return "7 天";
                case 720: return "30 天";
                case 8760: return "1 年";
                case 17520: return "2 年";
                default: return hours + " 小时";
            }
        }

        /// <summary>
        /// 胶囊按钮上的超短名（不带空格）。
        /// 288px 宽的卡片里同时摆五个选项，带空格的「30 天」会溢出，
        /// 所以按钮用这种紧凑写法，详细名称留给上方标题。
        /// </summary>
        public static string ChartRangeShortLabel(int hours)
        {
            switch (hours)
            {
                case 24: return "24h";
                case 168: return "7天";
                case 720: return "30天";
                case 8760: return "1年";
                case 17520: return "2年";
                default: return hours + "h";
            }
        }

        public static int ClampChartRange(int hours)
        {
            // 不在候选表里就回默认，不抛异常 —— 用户手改配置不该弄崩程序
            return Array.IndexOf(ChartRangeOptions, hours) >= 0 ? hours : 24;
        }

        public static string ClampChartStyle(string? style)
        {
            return string.Equals(style, "line", StringComparison.OrdinalIgnoreCase)
                ? "line"
                : "bar";
        }

        // ---------------------------------------------------------- 配色
        /// <summary>
        /// 电量配色。留空表示用内置默认。
        /// 每项都是 #RRGGBB，解析失败时回退默认，绝不让程序因为配色写错而崩。
        /// </summary>
        [JsonPropertyName("color_high")]
        public string ColorHigh { get; set; } = "";

        [JsonPropertyName("color_mid")]
        public string ColorMid { get; set; } = "";

        [JsonPropertyName("color_low")]
        public string ColorLow { get; set; } = "";

        [JsonPropertyName("color_critical")]
        public string ColorCritical { get; set; } = "";

        [JsonPropertyName("color_charging")]
        public string ColorCharging { get; set; } = "";

        /// <summary>档位阈值（百分比）。低于等于该值即降档</summary>
        [JsonPropertyName("threshold_critical")]
        public int ThresholdCritical { get; set; } = 10;

        [JsonPropertyName("threshold_low")]
        public int ThresholdLow { get; set; } = 30;

        [JsonPropertyName("threshold_mid")]
        public int ThresholdMid { get; set; } = 55;

        /// <summary>低电量提醒开关</summary>
        [JsonPropertyName("low_notify")]
        public bool LowNotify { get; set; } = true;

        /// <summary>托盘图标是否跟随系统主题（关掉则强制白线）</summary>
        [JsonPropertyName("follow_theme")]
        public bool FollowTheme { get; set; } = true;

        // ---------------------------------------------------------- 校验
        public static int ClampInterval(int value)
        {
            return Math.Clamp(value, IntervalMin, IntervalMax);
        }

        public static double ClampWidth(double value)
        {
            return Math.Clamp(value, WidthMin, WidthMax);
        }

        public static double ClampChartHeight(double value)
        {
            return Math.Clamp(value, ChartHeightMin, ChartHeightMax);
        }

        public static double ClampCardHeight(double value)
        {
            // 0 是合法值，表示「自动」
            if (value <= 0) return 0;
            return Math.Clamp(value, CardHeightMin, CardHeightMax);
        }

        /// <summary>把越界值拉回合法范围（读写文件后都调一次）</summary>
        public void Sanitize()
        {
            Interval = ClampInterval(Interval);
            CardWidth = ClampWidth(CardWidth);
            ChartHeight = ClampChartHeight(ChartHeight);
            CardHeight = ClampCardHeight(CardHeight);
            ChartStyle = ClampChartStyle(ChartStyle);
            ChartRangeHours = ClampChartRange(ChartRangeHours);
            MaxCardDevices = ClampMaxCardDevices(MaxCardDevices);

            // 别名表：清理空键空值
            ChannelAliases ??= new Dictionary<string, string>();
            foreach (var key in ChannelAliases.Keys.ToList())
            {
                if (string.IsNullOrWhiteSpace(key)
                    || string.IsNullOrWhiteSpace(ChannelAliases[key]))
                {
                    ChannelAliases.Remove(key);
                }
            }

            // 手动关闭列表：清理空键与重复
            ManualDisabled ??= new List<string>();
            ManualDisabled.RemoveAll(string.IsNullOrWhiteSpace);
            ManualDisabled = ManualDisabled
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 备注表同样要清理：空键、空值、过长
            DeviceNicknames ??= new Dictionary<string, string>();
            foreach (var key in DeviceNicknames.Keys.ToList())
            {
                string value = DeviceNicknames[key];
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    DeviceNicknames.Remove(key);
                    continue;
                }
                // 托盘提示只有 63 字符，备注留太长会把别的行挤掉。
                // 与 SetNickname 共用同一个上限常量，避免两处不同步。
                string trimmed = value.Trim();
                if (trimmed.Length > NicknameMaxLength)
                {
                    trimmed = trimmed.Substring(0, NicknameMaxLength).Trim();
                }
                DeviceNicknames[key] = trimmed;
            }

            // 手改配置可能留下空串或重复项，会导致「同一条被关两次」这类怪相
            DisabledDevices ??= new List<string>();
            DisabledDevices.RemoveAll(string.IsNullOrWhiteSpace);
            DisabledDevices = DisabledDevices
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 阈值必须严格递增，否则配色会出现「不可达档位」
            ThresholdCritical = Math.Clamp(ThresholdCritical, 1, 60);
            ThresholdLow = Math.Clamp(ThresholdLow, ThresholdCritical + 1, 90);
            ThresholdMid = Math.Clamp(ThresholdMid, ThresholdLow + 1, 99);
        }

        // ---------------------------------------------------------- 落盘
        /// <summary>appdata 下的数据目录名（便携模式不可用时的回退位置）</summary>
        public const string AppFolderName = "外设电量";

        /// <summary>改名前的目录名，升级时整目录搬家</summary>
        private const string LegacyFolderName = "2.4G设备电量";

        /// <summary>可执行文件所在目录</summary>
        public static string ProgramDirectory
        {
            get
            {
                try
                {
                    string? exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        string? dir = Path.GetDirectoryName(exe);
                        if (!string.IsNullOrEmpty(dir)) return dir!;
                    }
                }
                catch (Exception) { }

                try { return AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar); }
                catch (Exception) { return System.IO.Directory.GetCurrentDirectory(); }
            }
        }

        private static string? _resolvedDirectory;

        /// <summary>
        /// 数据目录。**默认就在 exe 旁边**，这样整个文件夹可以整体搬走
        /// （U 盘、另一台电脑），设置 / 历史 / 日志一起走。
        ///
        /// 三级解析，按顺序尝试：
        ///   1. `&lt;exe 目录&gt;\data` —— 能写就用它（便携）
        ///   2. `%LOCALAPPDATA%\外设电量` —— 程序目录不可写时的回退
        ///   3. 旧目录 `%LOCALAPPDATA%\2.4G设备电量` —— 连 2 都建不出来时兼容
        ///
        /// 为什么多包一层 `data\` 而不是直接倒进 exe 目录：
        /// exe 旁边已经有 dist 产物、图标、文档，再混进 history_*.json
        /// 会让用户根本分不清哪些能删。一个 data 子目录一目了然。
        ///
        /// 便携模式必须做**写探针**（实际建一个文件）而不是只看
        /// FileAttributes.ReadOnly：放在 Program Files 或只读 U 盘时，
        /// 目录属性可能仍是「可写」，但创建文件会被 ACL 拒绝。
        /// </summary>
        public static string Directory
        {
            get
            {
                if (_resolvedDirectory != null) return _resolvedDirectory;

                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                string portable = Path.Combine(ProgramDirectory, "data");
                string current = Path.Combine(local, AppFolderName);
                string legacy = Path.Combine(local, LegacyFolderName);

                // ---- 1. 便携：exe 旁边的 data\ ----
                if (IsWritable(portable))
                {
                    _resolvedDirectory = portable;
                    MigrateLegacyInto(portable, current, legacy);
                    return _resolvedDirectory;
                }

                // ---- 2. 回退到 %LOCALAPPDATA%\外设电量 ----
                try
                {
                    if (!System.IO.Directory.Exists(current)
                        && System.IO.Directory.Exists(legacy))
                    {
                        System.IO.Directory.Move(legacy, current);
                    }
                }
                catch (Exception)
                {
                    // 搬家失败（比如旧进程还占着文件）：继续用旧目录
                    if (System.IO.Directory.Exists(legacy)
                        && !System.IO.Directory.Exists(current))
                    {
                        _resolvedDirectory = legacy;
                        return _resolvedDirectory;
                    }
                }

                _resolvedDirectory = current;
                return _resolvedDirectory;
            }
        }

        /// <summary>
        /// 目录是否真的能写。必须实际建删一个文件：只有属性位判断不够，
        /// ACL / 只读卷 / 虚拟化都可能让「看起来可写」实际失败。
        /// </summary>
        private static bool IsWritable(string directory)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 首次进入便携模式时，把旧位置的数据搬过来。
        ///
        /// 只搬一次，且**只在便携目录里还没有对应文件时**搬 —— 否则用户
        /// 从 U 盘插到另一台机器（那台机器上有更旧的历史）时，会用旧数据
        /// 覆盖他手里较新的那份。
        /// </summary>
        private static void MigrateLegacyInto(string portable, string current, string legacy)
        {
            foreach (string source in new[] { current, legacy })
            {
                try
                {
                    if (!System.IO.Directory.Exists(source)) continue;

                    foreach (string file in System.IO.Directory.GetFiles(source))
                    {
                        string name = Path.GetFileName(file);
                        // 只认本程序的数据文件，别把用户其它东西抄进来
                        if (!name.StartsWith("history_", StringComparison.OrdinalIgnoreCase)
                            && !name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("log", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string target = Path.Combine(portable, name);
                        if (File.Exists(target)) continue;   // 便携目录里的优先
                        File.Copy(file, target, overwrite: false);
                    }
                }
                catch (Exception)
                {
                    // 迁不动不影响启动：继续用便携目录里的（可能为空）
                }
            }
        }

        /// <summary>日志目录。与数据同处，整体搬走时一起走。</summary>
        public static string LogDirectory { get { return Path.Combine(Directory, "logs"); } }

        /// <summary>设置文件名</summary>
        public const string FileName = "settings.json";

        public static string FilePath { get { return Path.Combine(Directory, FileName); } }

        public static Settings Load()
        {
            return LoadFrom(Directory);
        }

        /// <summary>
        /// 从指定目录读取。directory 参数是为了测试能隔离到临时目录 ——
        /// 早期版本只能读全局数据目录，测试会污染开发机的真实设置。
        /// </summary>
        public static Settings LoadFrom(string directory)
        {
            try
            {
                string path = Path.Combine(directory, FileName);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<Settings>(json);
                    if (loaded != null)
                    {
                        loaded.Sanitize();
                        return loaded;
                    }
                }
            }
            catch (Exception)
            {
                // 文件损坏不该让程序起不来：丢掉设置用默认值，下次保存时覆盖
            }
            return new Settings();
        }

        public void Save()
        {
            SaveTo(Directory);
        }

        public void SaveTo(string directory)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                // 不写 BOM：带 BOM 的 JSON 会让其它解析器直接报错
                string path = Path.Combine(directory, FileName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // 写不进去只损失持久化，不影响本次运行
            }
        }
    }
}
