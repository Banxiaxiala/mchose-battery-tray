// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [设置必须落盘] 用户的每一项自定义都要写进
//    %LOCALAPPDATA%\外设电量\settings.json，重启后保持。
//    改名前的目录是 2.4G设备电量，Settings.Directory 会在首次启动时
//    整目录搬过来（历史文件一起搬，不能清零）。
//    只放内存里会导致「每次开机都回到默认」。
// 2. [字段只增不改] 新增字段必须给默认值，删字段要保留兼容读取（旧文件缺字段
//    时用默认值即可，不要抛异常）。用户的配置文件不会跟着版本升级。
// 3. [区间校验集中在此] 刷新间隔、卡片宽度的合法范围只允许定义在这里，
//    界面层不许自己写 magic number 判断。
// 4. [编码] UTF-8 with BOM。

using System;
using System.IO;
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

            // 阈值必须严格递增，否则配色会出现「不可达档位」
            ThresholdCritical = Math.Clamp(ThresholdCritical, 1, 60);
            ThresholdLow = Math.Clamp(ThresholdLow, ThresholdCritical + 1, 90);
            ThresholdMid = Math.Clamp(ThresholdMid, ThresholdLow + 1, 99);
        }

        // ---------------------------------------------------------- 落盘
        /// <summary>数据目录名。改名后历史与设置一并搬过去。</summary>
        public const string AppFolderName = "外设电量";

        /// <summary>改名前的目录名，升级时整目录搬家</summary>
        private const string LegacyFolderName = "2.4G设备电量";

        private static string? _resolvedDirectory;

        /// <summary>
        /// 数据目录：%LOCALAPPDATA%\外设电量。
        ///
        /// 改名不能丢历史 —— 用户在 2.4G设备电量 目录里攒了几天的曲线，
        /// 直接换个目录就等于清零。所以首次启动时把旧目录【整目录 Move】
        /// 过去（历史与设置一起过去，不是只拷 settings）。
        /// 搬不动（被占用/权限）就退回旧目录继续用，优先保证不丢数据。
        /// </summary>
        public static string Directory
        {
            get
            {
                if (_resolvedDirectory != null) return _resolvedDirectory;

                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                string current = Path.Combine(local, AppFolderName);
                string legacy = Path.Combine(local, LegacyFolderName);

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

        public static string FilePath { get { return Path.Combine(Directory, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
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
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                // 不写 BOM：带 BOM 的 JSON 会让其它解析器直接报错
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception)
            {
                // 写不进去只损失持久化，不影响本次运行
            }
        }
    }
}
