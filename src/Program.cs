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
            Ring = ring;          // 初始值；启动后由 ReassignRings 按启用状态重算
            Read = read;
            History = new HistoryStore(historyKey);
        }

        /// <summary>卡片上的标签，如「鼠标」</summary>
        public string Label { get; }
        /// <summary>历史文件名里的键，如 logitech / razer / max102</summary>
        public string HistoryKey { get; }
        /// <summary>
        /// 托盘图标第几圈（0 外 → 2 内）。
        ///
        /// ⚠️ 这是**运行时分配**的，不是构造参数：关掉中间那台设备后，
        /// 后面的设备要往前补位，否则环上会留一个永久空洞。
        /// 由 Program.ReassignRings() 统一按「已启用设备的顺序」重算。
        /// </summary>
        public int Ring { get; set; }

        /// <summary>
        /// 用户是否开启了对这台设备的监控。
        ///
        /// false = 彻底不参与：不探测、不占环、不进卡片、不写历史。
        /// 与「离线」严格区分 —— 离线是设备没应答，关闭是用户不要了。
        /// </summary>
        public bool Enabled { get; set; } = true;
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
                bool light = Array.IndexOf(args, "--light") >= 0;
                int width = 0;
                int repeat = 1;
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (args[i] == "--width") int.TryParse(args[i + 1], out width);
                    if (args[i] == "--repeat") int.TryParse(args[i + 1], out repeat);
                }
                Tools.CardPreview.Run(output, style, range, light, width, repeat);
                return;
            }

            // --tray：把托盘图标输出成矢量 SVG（README 用，也用于视觉调试）
            //   --tray <输出路径.svg> [--light]
            // 图标本身是纯几何（圆 + 圆弧），做成矢量后放大到任何尺寸都清晰。
            //   --tray <输出.svg> [--light]   状态对比图（每格带说明）
            //   --traybar <输出.svg> [--light] 任务栏实景图（README 首图用）
            if (args.Length > 0 && (args[0] == "--tray" || args[0] == "--traybar"))
            {
                AttachConsole(-1);
                string output = args.Length > 1 ? args[1] : "tray.svg";
                bool lightTray = Array.IndexOf(args, "--light") >= 0;
                if (args[0] == "--traybar")
                {
                    Tools.TrayVectorPreview.RunBar(output, lightTray);
                }
                else
                {
                    Tools.TrayVectorPreview.Run(output, lightTray);
                }
                Console.WriteLine("已保存：" + output);
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

            // 首次启动（或用户删了 settings.json）时，自动探一遍
            // 「实际连着哪些设备」，只启用读得到的。
            // 之后以设置里的开关为准，不再覆盖用户的决定。
            if (!_settings.AutoProbeDone)
            {
                RunFirstRunProbe();
            }
            SyncEnabledFromSettings();

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

            var panel = new SettingsPanel(_settings,
                _devices.Select(d => (d.HistoryKey, d.Label)));
            panel.SettingsChanged += ApplySettings;
            panel.DeviceToggled += (key, enabled) =>
            {
                // 关键：关掉的设备必须立刻从「刷新循环」里消失，
                // 否则要等到下一轮刷新才停 —— 中间还会探测一次。
                foreach (var slot in _devices)
                {
                    if (string.Equals(slot.HistoryKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        slot.Enabled = enabled;
                    }
                }
                ReassignRings();
                RefreshNow();          // 立刻重画环 / 卡片 / 提示
                if (_card.IsVisible) _card.Reposition();
            };
            // 二级面板改完设备开关后，主面板的「已启用 N 台」要同步
            panel.SetDevicesChangedHandler(() =>
            {
                if (_card.IsVisible) RefreshCardData();
            });
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
        /// 从设置读取每台设备的开关状态。
        /// 必须在第一次刷新之前调用，否则被关闭的设备会先被探测一轮。
        /// </summary>
        private static void SyncEnabledFromSettings(List<DeviceSlot>? devices = null)
        {
            foreach (var slot in devices ?? _devices)
            {
                slot.Enabled = _settings.IsDeviceEnabled(slot.HistoryKey);
            }
            ReassignRings(devices);
        }

        /// <summary>
        /// 按「已启用设备在表里的顺序」重排托盘环位（0 外 → 2 内）。
        ///
        /// 为什么必须重排而不是保留原槽位：关掉中间那台之后，
        /// 环上会留一个永远空着的同心环，用户会以为「那里还有台设备但读不到」。
        /// 补位之后，环数 = 启用设备数，视觉上没有歧义。
        ///
        /// 关掉的那台把 Ring 设为 -1：BuildRings 会忽略负数，
        /// 万一哪里漏判了 Enabled，也不会画到环上。
        /// </summary>
        private static void ReassignRings(List<DeviceSlot>? devices = null)
        {
            int next = 0;
            foreach (var slot in devices ?? _devices)
            {
                slot.Ring = slot.Enabled ? next++ : -1;
            }
        }

        /// <summary>当前参与监控的设备（顺序即环位顺序）</summary>
        private static List<DeviceSlot> ActiveDevices()
        {
            return _devices.Where(d => d.Enabled).ToList();
        }

        /// <summary>
        /// 设备清单 —— 唯一一处定义「监控哪些设备」。
        /// 顺序即托盘环的顺序（外 → 内），新增设备只需在这里加一行；
        /// 用户关掉的设备在运行时跳过，见 ReassignRings。
        /// </summary>
        private static List<DeviceSlot> BuildDevices()
        {
            // 设备清单来自 Hardware/DeviceCatalog.cs —— 那是唯一一处定义。
            // 想加一台设备（尤其是「同协议的另一个型号」），改那张表即可，
            // 本文件一行都不用动。见 DeviceCatalog 顶部注释。
            var definitions = new List<DeviceDefinition>(DeviceCatalog.BuiltIn);

            // 用户自定义型号（devices.json）：允许把「另一个用同协议的型号」
            // 直接接进来，不需要重编译。
            try
            {
                foreach (var custom in LoadUserDevices())
                {
                    definitions.Add(custom);
                }
            }
            catch (Exception exc)
            {
                Core.Tracer.Note("读取 devices.json 失败：" + exc.Message);
            }

            var result = new List<DeviceSlot>(definitions.Count);
            for (int i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                result.Add(new DeviceSlot(definition.Label, definition.Key, i,
                    DeviceBinder.Bind(definition)));
            }
            return result;
        }

        /// <summary>
        /// 读取用户自定义设备。
        ///
        /// 路径与设置一致：程序目录的 data\ 优先，其次 %LOCALAPPDATA%。
        /// 解析失败只记日志、不抛出 —— 一个写错的 devices.json 不该让
        /// 整个托盘起不来。
        /// </summary>
        private static List<DeviceDefinition> LoadUserDevices()
        {
            string path = System.IO.Path.Combine(Settings.Directory, "devices.json");
            if (!System.IO.File.Exists(path)) return new List<DeviceDefinition>();

            string json = System.IO.File.ReadAllText(path);
            var items = System.Text.Json.JsonSerializer.Deserialize<
                List<DeviceDefinitionJson>>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            var devices = DeviceCatalog.ParseUserDevices(items, out var rejected);
            foreach (string reason in rejected)
            {
                Core.Tracer.Note("[devices.json] 已跳过 " + reason);
            }
            return devices;
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
        /// 把「每台设备的真实名字 + 唯一标识 + 备注」推给设置面板的二级页。
        ///
        /// 为什么要推：二级面板要能区分「同型号的两台」。型号 key 区分不了，
        /// 必须显示 Identity（MAC / 序列号 / 接口路径）。
        ///
        /// 没有读数时（设备还没接）也照样列出，"标识" 一栏显示「未检测到」，
        /// 这样用户仍然可以先给它起个备注。
        /// </summary>
        private static void SyncDeviceRows()
        {
            if (_settingsPanel == null) return;

            var rows = new List<SettingsPanel.DeviceRow>();
            foreach (var slot in _devices)
            {
                var reading = slot.Latest;

                // 只列出「真的连过」的设备。
                //
                // 14 个内置型号全列出来会让面板变成一张噪音清单 ——
                // 用户只插了一个鼠标，却看到 14 行「未检测到」，
                // 完全分不清哪个是自己要管理的。
                //
                // 判定「连过」：有过读数（哪怕现在离线），或者是用户
                // 主动开着的（他可能想先配好再插设备）。
                if (reading == null && !slot.Enabled) continue;

                string identity = reading?.Identity ?? "";
                if (identity.Length == 0)
                {
                    // 从没读到过：用型号 key 生成降级身份，
                    // 这样备注至少能存下来，等设备回来时对得上
                    identity = DeviceIdentity.ModelOnly(slot.HistoryKey);
                }

                string display = reading?.DisplayName ?? "";
                if (display.Length == 0) display = slot.Label;

                rows.Add(new SettingsPanel.DeviceRow
                {
                    Key = slot.HistoryKey,
                    DisplayName = display,
                    Identity = identity,
                    IdentityHint = DescribeIdentity(identity, reading),
                });
            }
            _settingsPanel.SetDeviceRows(rows);
        }

        /// <summary>
        /// 把 Identity 变成人眼可读的提示。
        ///
        /// 直接显示 `hid:046D:C54D:SN123` 太技术化，但完全不显示又分不出
        /// 同型号的两台。折中：显示**能区分**的那一段（MAC / 序列号尾号），
        /// 并在无法区分时明确标注。
        /// </summary>
        private static string DescribeIdentity(string identity, Reading? reading)
        {
            if (reading == null)
            {
                return "未检测到（备注会在设备接入后生效）";
            }

            if (DeviceIdentity.IsModelOnly(identity))
            {
                return "⚠ 无法区分同型号实例（该协议未提供唯一标识）";
            }

            // bt:44:5E:CD:40:7C:2A → 显示完整 MAC
            if (identity.StartsWith("bt:", StringComparison.OrdinalIgnoreCase))
            {
                return "蓝牙地址 " + identity.Substring(3);
            }

            // hid:...  → 取最后一段作为短标识
            int lastColon = identity.LastIndexOf(':');
            if (lastColon >= 0 && lastColon + 1 < identity.Length)
            {
                string tail = identity.Substring(lastColon + 1);
                if (tail.Length > 18) tail = tail.Substring(tail.Length - 18);
                return "标识 …" + tail;
            }

            return identity;
        }

        /// <summary>
        /// 首次启动探测：把「当前实际连着」的设备标记为启用。
        ///
        /// 为什么必须做：内置表里有 11 个「移植实现、未验证」的型号，
        /// 它们默认关闭（不能占用托盘环）。但如果用户恰好有其中一台，
        /// 第一次打开时什么都不显示，他会认为「这工具不支持我的设备」。
        ///
        /// 判定标准是**能读出电量**，而不是「枚举到接口」：
        /// 枚举到但读不到，说明设备在但协议不对，显示出来只会是个
        /// 永远 0% 的空环，比不显示更糟。
        ///
        /// 探测只做一次，结论写进设置。代价是首次启动慢几秒
        /// （每个未接设备都要等超时），之后启动是毫秒级。
        /// </summary>
        private static void RunFirstRunProbe()
        {
            try
            {
                Core.Tracer.Line("首次启动：正在探测实际连接的设备…");

                // 并行探测：每台未接设备都要等超时（约 0.4s，有的还两遍），
                // 串行 14 台能拖十几秒。并行后总耗时 ≈ 最慢的那台。
                //
                // 安全性：每个 slot 只被一个任务读写，slot 之间没有共享状态；
                // Reading 是每台独立的返回值。HID 枚举本身在 SetupAPI 里
                // 是线程安全的（每次调用各自开句柄）。
                var found = new List<string>();
                var results = new (string key, Reading? reading)[_devices.Count];
                System.Threading.Tasks.Parallel.For(0, _devices.Count, i =>
                {
                    var slot = _devices[i];
                    Reading? reading = null;
                    try
                    {
                        (reading, _) = slot.Read();
                    }
                    catch (Exception)
                    {
                        reading = null;
                    }
                    results[i] = (slot.HistoryKey, reading);
                });

                foreach (var (key, reading) in results)
                {
                    // ⚠️ 判据必须是「在线且有电量」，不能只看有电量。
                    //
                    // 蓝牙通道读的是 Windows 缓存属性：设备关机后，
                    // 那几个节点仍然留着上次的百分比（实测离线 8 小时还是 100%）。
                    // 只看 Percent.HasValue 会把「早就拔掉的耳机」当成在连着，
                    // 于是首次启动就启用了三台设备，但用户只插了一个鼠标。
                    if (reading == null || !reading.Percent.HasValue) continue;
                    if (!reading.Connected) continue;

                    // 第三道判据：蓝牙缓存的新鲜度。
                    // IsConnected 在链路刚断时还没更新（实测差几十秒到几分钟），
                    // 所以要再看「电量多久没刷新了」。实测数据：
                    //   在用的耳机   → 几十秒内刚刷新
                    //   两小时前关掉 → 停在 20:57
                    //   三天前关掉的键盘 → 停在 3 天前
                    // 取 10 分钟：足够容忍一次刷新间隔，又能挡住「早就关了」。
                    bool fresh = reading.IsFresh(TimeSpan.FromMinutes(10));
                    if (!fresh)
                    {
                        // 只记「跳过」这一种情况：正常探测到的不必刷日志，
                        // 但「为什么这台没启用」是排查时的关键信息。
                        Core.Tracer.Line($"[首次探测] {key} 跳过：数据太旧 " +
                            $"（{reading.DataAt:yyyy-MM-dd HH:mm}）");
                        continue;
                    }

                    found.Add(key);
                    // 回填到 slot，让首帧就有数（不然第一次刷新前是空的）
                    foreach (var slot in _devices)
                    {
                        if (string.Equals(slot.HistoryKey, key,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            slot.Latest = reading;
                            break;
                        }
                    }
                }

                // 只启用探到的；其余全部关掉（包括原本 EnabledByDefault 的）。
                // 这样用户第一次打开时，看到的正好是他机器上真实存在的设备。
                _settings.DisabledDevices = _devices
                    .Select(d => d.HistoryKey)
                    .Where(key => !found.Contains(key, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                // 一台都没探到：不要全关，否则界面空着更容易被当成坏了。
                // 保留内置默认启用的那几台（让用户至少看到「读不到」的原因）。
                if (found.Count == 0)
                {
                    _settings.DisabledDevices = new List<string>();
                    Core.Tracer.Line("首次探测什么都没读到，保留默认设备（会在界面上报原因）");
                }
                else
                {
                    Core.Tracer.Line("首次探测到：" + string.Join("、", found));
                }

                _settings.AutoProbeDone = true;
                _settings.Save();
            }
            catch (Exception exc)
            {
                // 探测失败不能挡住启动：退回默认启用集合
                Core.Tracer.Line("首次探测失败：" + exc.Message);
                _settings.AutoProbeDone = true;
                try { _settings.Save(); } catch (Exception) { }
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
            // 只刷新启用的设备：关闭的设备不探测、不写历史、不参与低电量提醒。
            // 这是「关闭监控」与「离线」的核心区别 —— 离线设备仍会被探测
            // （等它回来），关闭的设备一次 HID/PnP 调用都不发。
            foreach (var slot in ActiveDevices())
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

                // 身份与备注：每次读到都补一遍。
                //
                // 为什么在这里而不是 Reader 里：Note（备注）只有设置层知道，
                // Reader 不该依赖设置。而 Identity 的降级逻辑需要设备型号 key，
                // 也只有在这里拿得到。
                if (reading != null)
                {
                    // Reader 没给出身份 → 降级为「只有型号」，行为同旧版
                    if (string.IsNullOrEmpty(reading.Identity))
                    {
                        reading.Identity = DeviceIdentity.ModelOnly(slot.HistoryKey);
                    }

                    // ⚠️ 关键一步：同一台设备切换连接方式时，两条通道
                    // 报上来的身份不同（蓝牙用 MAC、2.4G 用接收器序列号）。
                    // 不收敛的话，用户切个连接方式就发现备注没了、历史断了。
                    //
                    // 收敛之后 reading.Identity 统一成该槽位的「主身份」，
                    // 所以备注与历史都跟着设备走，而不是跟着通道走。
                    reading.Identity = _settings.ResolveChannelIdentity(
                        slot.HistoryKey, reading.Identity);

                    reading.Nickname = _settings.GetNickname(reading.Identity);
                }

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

            SyncDeviceRows();

            _tray.SetReadings(BuildRings());
            _tray.SetTooltip(BuildTooltip());

            foreach (var slot in ActiveDevices())
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
            foreach (var slot in ActiveDevices())
            {
                // 备注名优先，其次型号，最后退回标签（鼠标/耳机/键盘）
                string name = slot.Latest != null
                    ? slot.Latest.DisplayName
                    : "";
                if (name.Length == 0) name = slot.Label;
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
                    string name = reading.DisplayName;
                    if (name.Length == 0) name = slot.Label;
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

            foreach (var slot in ActiveDevices())
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

            // 按设置截断：卡片最多显示 N 台（默认 5）。
            //
            // 为什么在「渲染前」而不是「采集前」截断：被省略的设备仍然
            // 要参与托盘环、悬停提示与低电量提醒 —— 只是不进卡片。
            // 所以数据照采，只在交给卡片时砍掉多余的。
            //
            // 排序已经按「电量从低到高」做过（BatteryCard.Render 里），
            // 所以留前 N 台 = 留最需要关注的 N 台。
            int limit = Settings.ClampMaxCardDevices(_settings.MaxCardDevices);
            int omitted = 0;
            if (devices.Count > limit)
            {
                // Render 内部会再排序，这里先按同样规则排好再截断，
                // 保证「留下的是电量最低的」而不是「表里靠前的」。
                devices = devices
                    .OrderBy(d => d.Percent ?? int.MaxValue)
                    .ToList();
                omitted = devices.Count - limit;
                devices = devices.Take(limit).ToList();
            }

            _card.Render(devices, omitted);
        }

        // ============================================================ 探测
        private static void Probe()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("设备探测");
            Console.WriteLine("============================================================");

            // 必须尊重用户关掉的设备：--probe 是排查工具，不是「绕过设置」的开关。
            // 关掉的设备如果在这里还去探测，用户会以为开关没生效。
            var probed = BuildDevices();
            SyncEnabledFromSettings(probed);

            var skipped = new List<string>();
            foreach (var slot in probed)
            {
                if (!slot.Enabled) { skipped.Add(slot.Label); continue; }
                PrintReading("[" + slot.Label + "]", slot);
            }
            if (skipped.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("已关闭监控：" + string.Join("、", skipped)
                    + "（在设置面板里重新开启）");
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

                // --probe 走的是独立的 BuildDevices()，不经过 RefreshNow，
                // 所以身份降级与备注要在这里补一遍 —— 否则输出里
                // 看不到「标识」和「备注」，排查时容易误判。
                if (string.IsNullOrEmpty(reading.Identity))
                {
                    reading.Identity = DeviceIdentity.ModelOnly(slot.HistoryKey);
                }
                // 与 RefreshNow 一致：先收敛通道身份，再取备注
                reading.Identity = _settings.ResolveChannelIdentity(
                    slot.HistoryKey, reading.Identity);
                reading.Nickname = _settings.GetNickname(reading.Identity);
                Console.WriteLine("  " + (reading.Name.Length > 0 ? reading.Name : "(未命名)"));
                Console.WriteLine("    协议    " + reading.Transport);
                if (reading.Identity.Length > 0)
                {
                    string mark = DeviceIdentity.IsModelOnly(reading.Identity)
                        ? "（无法区分同型号实例）" : "";
                    Console.WriteLine("    标识    " + reading.Identity + mark);
                }
                if (reading.Nickname.Length > 0)
                {
                    Console.WriteLine("    备注    " + reading.Nickname);
                }
                Console.WriteLine("    电量    " + reading.Percent + "%");
                string state = reading.Status.Length > 0 ? reading.Status : "未知";
                if (reading.Charging) state += "（充电中）";
                Console.WriteLine("    状态    " + state);
                Console.WriteLine("    在线    " + (reading.Connected ? "是" : "否"));

                // 附上协议链，便于报 issue 时说明「走的是哪条通道」。
                // 兼容性矩阵里的等级就是按通道划分的，没有这行就没法定位。
                var definition = DeviceCatalog.FindBuiltIn(slot.HistoryKey);
                if (definition != null)
                {
                    foreach (var channel in definition.Channels)
                    {
                        Console.WriteLine("    通道    " + channel.Describe());
                    }
                }
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
