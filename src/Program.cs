// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [工作目录重置] 自启动时工作目录可能是 C:\Windows\System32，
//    启动首行必须切到程序所在目录（AGENT_RULES.md 第 5 条）。
// 2. [单实例] 用全局互斥量，第二次启动直接退出。
// 3. [装配层] 本文件只允许「把各层接起来」，不许写协议字节或窗口布局。
// 4. [设备表驱动] 设备清单只允许在 BuildDevices() 里出现一次。
//    原来的实现把「鼠标 / 耳机」写成了两组平行字段（_mouse/_headset +
//    _mouseAnalysis/_headsetAnalysis + _mouseError/_headsetError），
//    加第三台设备要同步改 11 处、漏一处就静默不显示 —— 现在统一成列表。
// 5. [编码] UTF-8 with BOM。
//
// 外设电量托盘 —— 入口
// ----------------------------------------------------------------
// 一个托盘图标，同时显示三台外设的电量，左键单击弹亚克力卡片看
// 24 小时历史与续航预测。
//
// 支持设备（协议均为真机实测）：
//   * 罗技 LIGHTSPEED 接收器（HID++ 2.0）
//   * 雷蛇无线耳机（64 字节 PA/PI 中断报文）
//   * 维咖 MAX102 键盘（VIA cmd 20，蓝牙模式下走 Windows 蓝牙电量属性）

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using PeripheralBattery.Core;
using PeripheralBattery.Hardware;
using PeripheralBattery.Models;
using PeripheralBattery.Services;
using PeripheralBattery.UI;

namespace PeripheralBattery
{
    /// <summary>
    /// 一台被监控的设备：读取器 + 历史 + 卡片标签 + 托盘槽位。
    ///
    /// 托盘槽位决定这个读数画在图标第几圈：
    ///   0 = 外环（鼠标）、1 = 中环（耳机）、2 = 内环（键盘）。
    /// 槽位必须稳定 —— 图标不能因为某台设备离线就换圈，否则用户
    /// 会以为是另一台设备。
    /// </summary>
    internal sealed class DeviceSlot
    {
        public DeviceSlot(string label, string historyKey, int ring,
            Func<(Reading? reading, string error)> read)
        {
            Label = label;
            HistoryKey = historyKey;
            Ring = ring;
            Read = read;
            History = new HistoryStore(historyKey);
        }

        /// <summary>卡片上的标签，如「鼠标」</summary>
        public string Label { get; }
        /// <summary>历史文件名里的键，如 logitech / razer / max102</summary>
        public string HistoryKey { get; }
        /// <summary>托盘图标第几圈（0 外 → 2 内）</summary>
        public int Ring { get; }
        public Func<(Reading? reading, string error)> Read { get; }
        public HistoryStore History { get; }

        /// <summary>最近一次成功读数；null = 从没读到过</summary>
        public Reading? Latest { get; set; }
        public Analysis? Analysis { get; set; }
        public string Error { get; set; } = "";

        /// <summary>
        /// 连续没读到的次数。用来区分「空口丢了一帧」与「设备真的没了」。
        /// 没有它，丢一帧就会把卡片与托盘标成离线（默认 30 秒刷新一次，
        /// 错就错 30 秒）—— 用户看到的是「耳机明明在用却掉线」。
        /// </summary>
        public ReadingDebounce Debounce { get; } = new();
    }

    internal static class Program
    {
        // 改名「外设电量」后互斥量也要换：新旧版本并存时各跑各的托盘，
        // 用户升级后旧进程还在的话会看到两个图标。换名字可以让新版本
        // 在旧版本运行时不启动（配合下面 Initialize 里的提示）。
        private const string MutexName = "Global\\PeripheralBatteryTrayWPF3";
        private const int LowBatteryThreshold = 10;

        /// <summary>用户设置（间隔 / 尺寸 / 开关），从 settings.json 载入</summary>
        private static Settings _settings = Settings.Load();

        private static TrayIcon _tray = null!;
        private static BatteryCard _card = null!;
        private static SettingsPanel _settingsPanel = null!;
        private static System.Windows.Forms.Timer _timer = null!;

        private static List<DeviceSlot> _devices = null!;
        private static readonly HashSet<string> Notified = new();

        [STAThread]
        private static void Main(string[] args)
        {
            // 工作目录重置：自启动时可能是 System32
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
                if (dir.Length > 0) Directory.SetCurrentDirectory(dir);
            }
            catch (Exception) { }

            // --preview：用合成数据渲染卡片并截图，调视觉时用
            //
            //   --preview <输出路径> [bar|line] [范围小时]
            //
            // 带样式与范围参数是为了能离线验证折线图与切换条 ——
            // 真实的 24 个月数据要等两个月才攒得出来，不能靠等。
            if (args.Length > 0 && args[0] == "--preview")
            {
                AttachConsole(-1);
                string output = args.Length > 1 ? args[1] : "card.png";
                string style = args.Length > 2 ? args[2] : "bar";
                int range = 24;
                if (args.Length > 3) int.TryParse(args[3], out range);
                Tools.CardPreview.Run(output, style, range);
                return;
            }

            // --trace 可以与 --probe / --preview 组合，用来排查「设备读不到」
            if (Array.IndexOf(args, "--trace") >= 0)
            {
                Core.Tracer.Enabled = true;
            }

            // --probe：只读一次并打印，用来验证协议是否还通
            if (args.Length > 0 && args[0] == "--probe")
            {
                AttachConsole(-1);
                Core.Tracer.Line("提示：加上 --trace 可以看到每个 HID 帧的收发");
                Probe();
                return;
            }

            using var mutex = new Mutex(true, MutexName, out bool created);
            if (!created) return;

            // 启动时补写设置文件：
            //   * 首次运行：写出全部默认项，给用户一个可照着改的模板
            //   * 升级后：旧文件缺少新字段，这里一并补齐 ——
            //     否则用户永远不知道该版本多了哪些可调项
            // 已有值与新增默认值合并由 Settings.Load + Save 自然完成。
            _settings.Save();

            // 改名遗留：旧名字的自启动项、启动快捷方式一并收拾。
            // 必须在 _settings.Save() 之后 —— 那一步已经触发数据目录搬家，
            // 自启动快捷方式指向的是 exe 路径，两者互不影响但顺序更直观。
            AutoStart.MigrateLegacyEntries();

            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _devices = BuildDevices();

            _tray = new TrayIcon();
            _card = new BatteryCard();
            _card.SetHandlers(RefreshNow, OpenSettings);

            // 图表切换必须同时做三件事，少一件用户就会觉得「点了没反应」：
            //   1. 更新设置并落盘（下次打开保持）
            //   2. 重新拉数据并重绘卡片（设置变了但界面不变是最常见的抱怨）
            //   3. 卡片尺寸可能变（折线图与柱状图高度不同），要重新定位
            _card.ApplyChartSettings(_settings.ChartStyle, _settings.ChartRangeHours);
            _card.SetChartHandlers(
                style =>
                {
                    _settings.ChartStyle = Settings.ClampChartStyle(style);
                    _settings.Save();
                    SyncChartToCard();
                },
                hours =>
                {
                    _settings.ChartRangeHours = Settings.ClampChartRange(hours);
                    _settings.Save();
                    SyncChartToCard();
                });

            var panel = new SettingsPanel(_settings);
            panel.SettingsChanged += ApplySettings;
            _settingsPanel = panel;

            _tray.Clicked += ToggleCard;
            _tray.RefreshRequested += RefreshNow;
            _tray.AutoStartToggled += ToggleAutoStart;
            _tray.ExitRequested += () =>
            {
                _timer?.Stop();
                _card.Close();
                _tray.Dispose();
                Application.Exit();
            };

            ApplySettings();
            UpdateAutoStartMenu();
            RefreshNow();

            _timer = new System.Windows.Forms.Timer { Interval = _settings.Interval * 1000 };
            _timer.Tick += (_, _) => RefreshNow();
            _timer.Start();
            ApplySettings();

            Application.Run();
        }

        /// <summary>
        /// 设备清单 —— 唯一一处定义「监控哪些设备」。
        /// 顺序即托盘环的顺序（外 → 内），新增设备只需在这里加一行。
        /// </summary>
        private static List<DeviceSlot> BuildDevices()
        {
            return new List<DeviceSlot>
            {
                new("鼠标", "logitech", 0, new LogitechReader().Read),
                new("耳机", "razer", 1, new RazerReader().Read),
                new("键盘", "max102", 2, new Max102Reader().Read),
            };
        }

        // ============================================================ 交互
        private static void ToggleCard()
        {
            if (_card.IsVisible)
            {
                _card.Hide();
                return;
            }
            RefreshCardData();
            var center = _tray.IconCenter();
            _card.Anchor = center.HasValue
                ? new System.Windows.Point(center.Value.X, center.Value.Y)
                : null;
            _card.Show();
            _card.Reposition();
            _card.Activate();
        }

        /// <summary>
        /// 齿轮按钮：打开设置面板，贴在卡片左侧。
        ///
        /// 之前这里接的是「悄悄切换开机自启」——写注册表成功了，但界面上
        /// 什么都不变，看起来就像「点设置没用」。现在改成真的打开面板。
        /// </summary>
        private static void OpenSettings()
        {
            if (_settingsPanel.IsVisible)
            {
                _settingsPanel.Hide();
                _card.CompanionRect = null;
                return;
            }
            _settingsPanel.SyncFromSystem();
            _settingsPanel.Show();

            var cardRect = new System.Windows.Rect(
                _card.Left, _card.Top, _card.ActualWidth, _card.ActualHeight);
            _settingsPanel.PlaceNear(cardRect);

            // 把面板范围告诉卡片：点面板不该关掉卡片
            _card.CompanionRect = new System.Windows.Rect(
                _settingsPanel.Left, _settingsPanel.Top,
                _settingsPanel.ActualWidth, _settingsPanel.ActualHeight);
            _settingsPanel.Activate();
        }

        /// <summary>
        /// 设置变化后立即应用：间隔、卡片宽度、图表高度、托盘图标配色。
        /// 不需要用户重启程序。
        /// </summary>
        private static void ApplySettings()
        {
            if (_timer != null)
            {
                int ms = _settings.Interval * 1000;
                bool changed = _timer.Interval != ms;
                _timer.Interval = ms;
                if (changed && _timer.Enabled)
                {
                    // 立刻按新节奏走一轮，别让用户等满旧间隔
                    _timer.Stop();
                    _timer.Start();
                }
            }

            Theme.ForceDark = _settings.FollowTheme ? (bool?)null : true;

            // 配色与阈值必须先于重绘应用，否则卡片会用上一轮的旧色
            Palette.ApplyCustom(
                _settings.ColorHigh, _settings.ColorMid, _settings.ColorLow,
                _settings.ColorCritical, _settings.ColorCharging,
                _settings.ThresholdCritical, _settings.ThresholdLow,
                _settings.ThresholdMid);

            if (_card != null)
            {
                _card.ApplyMetrics(_settings.CardWidth, _settings.ChartHeight,
                    _settings.CardHeight);
                // 宽度/高度变化后内容要重排，否则会保留旧的紧凑布局
                if (_card.IsVisible) RefreshCardData();
            }

            if (_tray != null && _devices != null)
            {
                _tray.SetReadings(BuildRings());
            }
        }

        /// <summary>
        /// 把设置里的图表选项推给卡片，然后重绘。
        ///
        /// 重绘后必须 Reposition：折线图与柱状图的内容高度不同
        /// （折线图多了左侧刻度带与底部时间标签），卡片高度会变，
        /// 不重新定位就会往上顶出一个缺口（见 AGENTS.md 6.2）。
        /// </summary>
        private static void SyncChartToCard()
        {
            _card.ApplyChartSettings(_settings.ChartStyle, _settings.ChartRangeHours);
            RefreshCardData();
            if (_card.IsVisible) _card.Reposition();
        }

        private static void ToggleAutoStart()
        {
            AutoStart.SetEnabled(!AutoStart.Enabled);
            UpdateAutoStartMenu();
        }

        private static void UpdateAutoStartMenu()
        {
            _tray.AutoStartItem.Checked = AutoStart.Enabled;
        }

        // ============================================================ 刷新
        private static void RefreshNow()
        {
            foreach (var slot in _devices)
            {
                Reading? reading = null;
                string error = "";
                try
                {
                    (reading, error) = slot.Read();
                }
                catch (Exception exc)
                {
                    error = slot.Label + "读取异常：" + exc.Message;
                }
                slot.Error = error ?? "";

                // 只有真读到【当前】读数才记样本；读不到只回填 last_known，不改当前读数。
                // 蓝牙通道要注意：系统属性在设备离线时仍会留着上次的值，
                // 那种“陈旧值”写进历史会伪造出一条水平曲线，把续航预测的
                // 斜率算成 0。所以必须连通状态一起判。
                bool live = reading != null && reading.Connected;

                try
                {
                    if (live) slot.History.Add(reading!.Percent, reading.Charging);
                    slot.Analysis = slot.History.Analyze(reading?.Percent,
                        reading?.Charging ?? false);
                    if (live)
                    {
                        reading!.Forecast = slot.Analysis.Forecast.Hours.HasValue
                            ? "预计剩余 " + slot.Analysis.Forecast.Text
                            : "";
                    }
                }
                catch (Exception) { }

                if (reading != null)
                {
                    if (!reading.Connected) reading.Forecast = "";
                    slot.Latest = reading;
                    // 读到了（即使设备自报 state=已断开）：空口是通的，清零
                    slot.Debounce.Hit();
                }
                else if (slot.Latest != null)
                {
                    // 没读到：不要立刻标离线。先记一笔，连续多次才认。
                    // 期间保留上一次的百分比与在线标记，避免界面在
                    // 「在线/离线」之间闪跳。
                    if (slot.Debounce.Miss())
                    {
                        slot.Latest.Connected = false;
                        slot.Latest.Forecast = "";
                    }
                }
            }

            _tray.SetReadings(BuildRings());
            _tray.SetTooltip(BuildTooltip());

            foreach (var slot in _devices)
            {
                CheckLowBattery(slot);
            }

            if (_card.IsVisible)
            {
                RefreshCardData();
                _card.Reposition();
            }
        }

        /// <summary>按 Ring 槽位摊成数组交给托盘画图；缺的槽位传 null</summary>
        private static Reading?[] BuildRings()
        {
            int count = 0;
            foreach (var slot in _devices)
            {
                if (slot.Ring + 1 > count) count = slot.Ring + 1;
            }
            var rings = new Reading?[count];
            foreach (var slot in _devices)
            {
                if (slot.Ring >= 0 && slot.Ring < count) rings[slot.Ring] = slot.Latest;
            }
            return rings;
        }

        private static string BuildTooltip()
        {
            var entries = new List<TooltipText.Entry>();
            foreach (var slot in _devices)
            {
                string name = slot.Latest != null && slot.Latest.Name.Length > 0
                    ? slot.Latest.Name
                    : slot.Label;
                string full = slot.Latest != null
                    ? slot.Latest.Line(name)
                    : FallbackLine(slot.Error, slot.Label, slot.Analysis);

                entries.Add(new TooltipText.Entry
                {
                    Label = slot.Label,
                    Full = full,
                    Compact = TooltipText.CompactOf(slot.Label,
                        slot.Latest?.Percent, slot.Latest?.Connected ?? false,
                        slot.Latest?.Charging ?? false),
                });
            }
            // 降级与 63 字符上限全部在 TooltipText 里处理（可脱机单测），
            // 这里不再自己裁 —— 之前两处重复裁剪反而把标题删光了。
            return TooltipText.Build(entries);
        }

        private static string FallbackLine(string error, string label, Analysis? analysis)
        {
            string line = error.Length > 0 ? error : label + "  --";
            if (analysis != null)
            {
                string known = analysis.DescribeLastKnown();
                if (known.Length > 0) line += "  (" + known + ")";
            }
            return line;
        }

        private static void CheckLowBattery(DeviceSlot slot)
        {
            var reading = slot.Latest;
            if (reading?.Percent == null) return;
            if (reading.Charging)
            {
                Notified.Remove(slot.HistoryKey);
                return;
            }
            if (_settings.LowNotify && reading.Percent.Value <= LowBatteryThreshold)
            {
                if (Notified.Add(slot.HistoryKey))
                {
                    string name = reading.Name.Length > 0 ? reading.Name : slot.Label;
                    _tray.ShowNotification("外设电量",
                        name + " 电量仅剩 " + reading.Percent.Value + "%");
                }
            }
            else
            {
                Notified.Remove(slot.HistoryKey);
            }
        }

        private static void RefreshCardData()
        {
            var devices = new List<CardDevice>();

            // 折线图只在需要时才拉数据：柱状图模式下这轮拉 60 个点纯属浪费。
            bool needPoints = _card.ChartStyle == "line";
            int rangeHours = _card.ChartRangeHours;
            double now = TimeText.UnixNow();
            double windowSeconds = rangeHours * 3600.0;

            foreach (var slot in _devices)
            {
                var reading = slot.Latest ?? new Reading { Connected = false };
                if (!reading.Connected && slot.Analysis == null)
                {
                    // 从没读到过也要能显示「上次 xx%」
                    slot.Analysis = slot.History.Analyze(null, false);
                }

                var device = new CardDevice
                {
                    Label = slot.Label,
                    Reading = reading,
                    Analysis = slot.Analysis ?? new Analysis(),
                };

                // 两种样式都要按时间范围拉数据：
                // 柱状图硬编码 24 小时的话，选 30 天时它会默默不生效，
                // 用户会以为「时间选择对柱状图没用」。
                try
                {
                    if (needPoints)
                    {
                        device.Points = slot.History.Points(windowSeconds, now);
                    }
                    else
                    {
                        device.Buckets = slot.History.Buckets(rangeHours,
                            reading.Connected ? reading.Percent : null,
                            reading.Charging, now);
                    }
                }
                catch (Exception)
                {
                    // 历史文件损坏不该让卡片画不出来
                    if (needPoints) device.Points = new List<ChartPoint>();
                }

                // 范围统计：标题写「近 7 天 51%~89%」时，那个区间必须真的是 7 天
                try
                {
                    var stats = slot.History.Stats(rangeHours, now);
                    device.RangeMin = stats.min;
                    device.RangeMax = stats.max;
                    device.RangeActiveHours = stats.activeHours;
                }
                catch (Exception) { }

                // 横轴标签两种样式共用
                {
                    var from = TimeText.FromUnix(now - windowSeconds);
                    var to = TimeText.FromUnix(now);
                    bool shortSpan = rangeHours <= 24;
                    device.FromUnix = now - windowSeconds;
                    device.ToUnix = now;
                    device.RangeFrom = shortSpan
                        ? from.ToString("HH:mm")
                        : from.ToString("yyyy-MM-dd");
                    device.RangeTo = shortSpan ? "现在" : to.ToString("MM-dd");
                }

                devices.Add(device);
            }
            _card.Render(devices);
        }

        // ============================================================ 探测
        private static void Probe()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("设备探测");
            Console.WriteLine("============================================================");

            foreach (var slot in BuildDevices())
            {
                PrintReading("[" + slot.Label + "]", slot);
            }
            Console.WriteLine();

            // 蓝牙通道单独打一份原始信息：走的是系统属性而不是 HID，
            // 排查时先确认系统自己有没有记录过电量。
            PrintBluetoothNodes();
        }

        private static void PrintReading(string title, DeviceSlot slot)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            try
            {
                (Reading? reading, string error) = slot.Read();
                if (reading == null)
                {
                    Console.WriteLine("  读取失败：" + error);
                    return;
                }
                Console.WriteLine("  " + (reading.Name.Length > 0 ? reading.Name : "(未命名)"));
                Console.WriteLine("    协议    " + reading.Transport);
                Console.WriteLine("    电量    " + reading.Percent + "%");
                string state = reading.Status.Length > 0 ? reading.Status : "未知";
                if (reading.Charging) state += "（充电中）";
                Console.WriteLine("    状态    " + state);
                Console.WriteLine("    在线    " + (reading.Connected ? "是" : "否"));
            }
            catch (Exception exc)
            {
                Console.WriteLine("  异常：" + exc.Message);
            }
        }

        private static void PrintBluetoothNodes()
        {
            Console.WriteLine();
            Console.WriteLine("[蓝牙电量属性]");
            try
            {
                // 两棵树都要打：耳机在 BTHENUM（经典蓝牙），键盘在 BTHLE（低功耗）。
                // 只看 BTHLE 会误判“系统没记录耳机电量”。
                var nodes = BluetoothBattery.Query();
                if (nodes.Count == 0)
                {
                    Console.WriteLine("  没有带电量属性的蓝牙节点（BTHLE + BTHENUM）");
                    return;
                }
                foreach (var node in nodes)
                {
                    Console.WriteLine("  " + node.FriendlyName + "  " + node.Address);
                    Console.WriteLine("    电量    " + (node.Percent.HasValue
                        ? node.Percent.Value + "%" : "(无)"));
                    Console.WriteLine("    更新    " + (node.UpdatedAt == DateTime.MinValue
                        ? "(未知)" : node.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
                    Console.WriteLine("    在线    " + (node.Connected ? "是" : "否"));
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine("  异常：" + exc.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }
}
