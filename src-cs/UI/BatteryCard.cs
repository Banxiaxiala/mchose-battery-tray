// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [定位用 Shell_NotifyIconGetRect] 不许猜 Shell_TrayWnd 的坐标：
//    图标可能被折叠进「^」浮出面板，也可能被拖到副屏，只有系统知道它在哪。
// 2. [尺寸在渲染后再定位] 卡片高度由内容决定（SizeToContent.Height），
//    必须在 ContentRendered / SizeChanged 之后再算位置，否则拿到的
//    ActualHeight 是 0，窗口会贴在屏幕最顶端（Python 版踩过）。
// 3. [圆角靠 WindowChrome + DWM] WindowStyle=None + AllowsTransparency=false
//    才能同时拿到亚克力与真圆角。开 AllowsTransparency 会禁用亚克力。
// 4. [图标用 Path 不用字体] 图标字体的码点在不同 Windows 版本上会渲染成
//    完全不相干的东西（logi-tray 踩过：U+E0AC 画出来是仪表盘不是刷新箭头）。
// 5. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using PeripheralBattery.Core;
using PeripheralBattery.Models;

namespace PeripheralBattery.UI
{
    /// <summary>一张卡片要显示的一台设备</summary>
    public sealed class CardDevice
    {
        public string Label = "";
        public Reading Reading = new();
        public Analysis Analysis = new();

        /// <summary>离线时返回 null，界面显示「--」而不是伪造的历史值</summary>
        public int? Percent { get { return Reading.Connected ? Reading.Percent : null; } }
    }

    /// <summary>
    /// 亚克力电量卡片。
    ///
    /// 视觉参考 yixing233/logi-tray 的 DeviceCardWindow：
    ///   * 10px 圆角 + 1px 描边 + DWM 硬件级毛玻璃
    ///   * 标题栏右侧刷新 / 设置图标按钮
    ///   * 每台设备一组：标签行 → 电池图标 + 大号电量 + 档位
    ///     → 动画进度条 → 24h 区间说明 → 柱状图
    /// </summary>
    public sealed class BatteryCard : Window
    {
        private const double CardRadius = 10;

        /// <summary>卡片宽度（DIP）。由设置驱动，改动后调 ApplyMetrics 立即生效</summary>
        public double CardWidth { get; private set; } = 288;

        /// <summary>24 小时柱状图高度（DIP）</summary>
        public double ChartHeight { get; private set; } = 54;

        /// <summary>卡片固定高度（DIP）；0 = 由内容自动撑开</summary>
        public double FixedHeight { get; private set; }

        private readonly StackPanel _root;
        private readonly Border _cardBorder;
        private bool _backdropApplied;

        private Action? _onRefresh;
        private Action? _onSettings;

        /// <summary>当前定位锚点（托盘图标的屏幕中心）</summary>
        public Point? Anchor { get; set; }

        /// <summary>
        /// 设置面板的屏幕矩形。点它里面不算「点卡片外侧」——
        /// 否则用户点齿轮后，卡片会因为光标跑到面板上而立刻自己关掉。
        /// </summary>
        public Rect? CompanionRect { get; set; }

        public BatteryCard()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 亚克力要求根背景透明；开 AllowsTransparency 会禁用亚克力，故保持 false
            AllowsTransparency = false;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Topmost = true;
            Width = CardWidth;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(CardRadius),
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(0),
            });

            _root = new StackPanel { Margin = new Thickness(15, 11, 15, 13) };

            _cardBorder = new Border
            {
                CornerRadius = new CornerRadius(CardRadius),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.BrushOf(Theme.IsDark
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)),
                // 亚克力生效时保持透明；失败时换成不透明底板，否则文字看不清
                Background = Brushes.Transparent,
                Width = CardWidth,
                Child = _root,
            };
            Content = _cardBorder;

            SourceInitialized += (_, _) => ApplyBackdrop();
            ContentRendered += (_, _) =>
            {
                ApplyBackdrop();
                Reposition();
            };
            SizeChanged += (_, _) => Reposition();

            // ------------------------------------------------------------------
            // 关闭时机：两种情况都算「用户不想看这张卡片了」
            //
            //   1. 在前台窗口里点了一下（且不是本进程的窗口）
            //   2. 鼠标在本进程窗口之外按下（覆盖点任务栏、点桌面空白处）
            //
            // 只靠第 2 条不够：点别的窗口时，如果那个窗口抢走焦点，本卡片的
            // 光标轮询往往来不及命中「按键按下」那一帧，于是卡片赖着不走 ——
            // 用户报的「点其他窗口不关闭」就是这个。
            // 光靠第 1 条也不够：ShowActivated=false 让卡片自己拿不到焦点，
            // 所以不能等 Deactivated，必须主动比对 GetForegroundWindow。
            // ------------------------------------------------------------------
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };
            timer.Tick += (_, _) => CheckDismiss();
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) timer.Start(); else timer.Stop();
            };
            Closed += (_, _) => timer.Stop();
        }

        private void ApplyBackdrop()
        {
            if (_backdropApplied) return;
            _backdropApplied = true;

            bool ok = Acrylic.Apply(this, Theme.IsDark, CardRadius);
            if (!ok)
            {
                // 系统不支持毛玻璃：退回不透明底板，否则文字直接叠在桌面上
                Acrylic.Clear(this);
                _cardBorder.Background = Palette.BrushOf(Theme.IsDark
                    ? Palette.SurfaceDark
                    : Palette.SurfaceLight);
            }
        }

        /// <summary>
        /// 应用尺寸设置。改动宽度后必须重新布局并重新定位 ——
        /// 只改 Width 而不 Reposition，卡片会保留旧坐标，看起来像「飘了」。
        /// </summary>
        public void ApplyMetrics(double width, double chartHeight, double fixedHeight = 0)
        {
            CardWidth = Math.Clamp(width, 240, 420);
            ChartHeight = Math.Clamp(chartHeight, 34, 110);
            FixedHeight = fixedHeight > 0 ? Math.Clamp(fixedHeight, 240, 900) : 0;

            Width = CardWidth;
            _cardBorder.Width = CardWidth;

            if (FixedHeight > 0)
            {
                // 固定高度：内容超高时给滚动条，否则用户会「看不到第二台设备」
                SizeToContent = SizeToContent.Manual;
                Height = FixedHeight;
                _cardBorder.Height = FixedHeight;
                EnsureScrollHost();
            }
            else
            {
                SizeToContent = SizeToContent.Height;
                _cardBorder.Height = double.NaN;
                Height = double.NaN;
                RemoveScrollHost();
            }
            Reposition();
        }

        private ScrollViewer? _scroll;

        /// <summary>固定高度时必须让内容可滚动，否则底部设备被裁掉且无法触及</summary>
        private void EnsureScrollHost()
        {
            if (_scroll != null) return;

            _cardBorder.Child = null;
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _root,
                Padding = new Thickness(0),
            };
            _cardBorder.Child = _scroll;
        }

        private void RemoveScrollHost()
        {
            if (_scroll == null) return;
            _scroll.Content = null;
            _cardBorder.Child = _root;
            _scroll = null;
        }

        public void SetHandlers(Action onRefresh, Action onSettings)
        {
            _onRefresh = onRefresh;
            _onSettings = onSettings;
        }

        // ============================================================ 定位
        /// <summary>鼠标松开后允许下一次「按下」再判定一次</summary>
        public void Reposition()
        {
            if (!IsVisible) return;

            double width = ActualWidth > 0 ? ActualWidth : CardWidth;
            double height = ActualHeight > 0 ? ActualHeight : 0;
            if (height <= 0) return;

            var work = SystemParameters.WorkArea;
            double x, y;

            if (Anchor.HasValue)
            {
                // 图标中心与卡片右边对齐，与系统托盘气泡方向一致
                x = Anchor.Value.X - width + 26;
                y = Anchor.Value.Y - height - 10;
            }
            else
            {
                x = work.Right - width - 12;
                y = work.Bottom - height - 56;
            }

            x = Math.Clamp(x, work.Left + 8, work.Right - width - 8);
            y = Math.Clamp(y, work.Top + 8, work.Bottom - height - 8);
            Left = x;
            Top = y;
        }

        private bool _armed;
        private int _framesSinceShow;

        /// <summary>本卡片是否应该因为「用户去了别处」而关闭</summary>
        private void CheckDismiss()
        {
            if (!IsVisible) return;

            // 刚弹出的前几帧不判定：那一刻窗口还在定位，光标必然落在「外面」
            if (_framesSinceShow < 3)
            {
                _framesSinceShow++;
                return;
            }

            if (!Win32Cursor.TryGetCursorPos(out int cx, out int cy)) return;

            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            bool inSelf = cx >= topLeft.X && cx <= bottomRight.X
                       && cy >= topLeft.Y && cy <= bottomRight.Y;

            // 设置面板开着时，把它的范围也算作「卡片自己的一部分」
            bool inCompanion = false;
            if (CompanionRect.HasValue)
            {
                var r = CompanionRect.Value;
                inCompanion = cx >= r.Left && cx <= r.Right
                           && cy >= r.Top && cy <= r.Bottom;
            }

            bool cursorHidden = cx < 0 || cy < 0;
            if (cursorHidden) return;

            // ---- 规则 1：前台窗口已经不是本进程 ----
            // 用户点了浏览器/游戏/资源管理器，卡片就该收起。
            // 必须在光标位于卡片「外面」时才生效，否则在卡片上拖动滑块之类的
            // 操作会因为焦点跳到别的窗口而误关。
            //
            // 例外：点的是托盘图标本身（本进程的 NotifyIcon 窗口）时不动，
            // 由 OnTrayIconClicked 统一处理成「再次点击 = 收起」。
            IntPtr foreground = Win32.GetForegroundWindow();
            if (foreground != IntPtr.Zero
                && !Win32.IsOwnWindow(foreground)
                && !inSelf && !inCompanion)
            {
                Hide();
                return;
            }

            // ---- 规则 2：在任意位置按下鼠标 ----
            bool pressed = Mouse.LeftButton == MouseButtonState.Pressed
                        || Mouse.RightButton == MouseButtonState.Pressed;
            if (!pressed) return;
            if (_armed) return;

            // 按下那一刻就判定一次，避免「按住拖出去」时卡片跟着不关
            _armed = true;
            if (!inSelf && !inCompanion) Hide();
        }

        /// <summary>重新显示时复位「点击判定」的中间状态</summary>
        public void ResetDismissState()
        {
            _armed = false;
            _framesSinceShow = 0;
        }

        // ============================================================ 构建
        public void Render(List<CardDevice> devices)
        {
            ResetDismissState();
            _root.Children.Clear();
            _root.Children.Add(BuildTopBar());

            if (devices.Count == 0)
            {
                var hint = MakeText("未检测到设备", 11, FaintBrush());
                hint.Margin = new Thickness(0, 10, 0, 6);
                _root.Children.Add(hint);
                return;
            }

            // 电量从低到高排：最需要充电的排最前
            var ordered = devices
                .OrderBy(d => d.Percent ?? int.MaxValue)
                .ToList();

            bool first = true;
            foreach (var device in ordered)
            {
                if (!first)
                {
                    // 分隔线不透明度取 0x2E 而不是 0x1A：
                    // 单层 1px 的 0x1A 在深色底上只差 3 个色阶，实机几乎看不见，
                    // 三台设备会糊成一整块。
                    _root.Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 12, 0, 12),
                        Background = Palette.BrushOf(Theme.IsDark
                            ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
                            : Color.FromArgb(0x24, 0x00, 0x00, 0x00)),
                    });
                }
                _root.Children.Add(BuildDevice(device));
                first = false;
            }
        }

        private Brush PrimaryBrush()
        {
            return Palette.BrushOf(Theme.IsDark ? Palette.TextPrimaryDark : Palette.TextPrimaryLight);
        }

        private Brush SecondaryBrush()
        {
            return Palette.BrushOf(Theme.IsDark ? Palette.TextSecondaryDark : Palette.TextSecondaryLight);
        }

        private Brush FaintBrush()
        {
            return Palette.BrushOf(Theme.IsDark ? Palette.TextFaintDark : Palette.TextFaintLight);
        }

        private TextBlock MakeText(string text, double size, Brush brush,
            FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>标题栏：左侧标题，右侧刷新与设置两个图标按钮</summary>
        private Grid BuildTopBar()
        {
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = MakeText("外设电量", 12.5, PrimaryBrush(), FontWeights.SemiBold);
            Grid.SetColumn(title, 0);
            bar.Children.Add(title);

            var refresh = IconButton(Icons.Refresh(), "立即刷新", 11.5, filled: false);
            refresh.Click += (_, _) => _onRefresh?.Invoke();
            Grid.SetColumn(refresh, 1);
            bar.Children.Add(refresh);

            var settings = IconButton(Icons.Gear(), "设置", 12.5, filled: true);
            settings.Click += (_, _) => _onSettings?.Invoke();
            Grid.SetColumn(settings, 2);
            bar.Children.Add(settings);

            return bar;
        }

        /// <summary>
        /// 图标按钮。用 Path 画图形而不是字体字形 —— 字体码点在不同 Windows
        /// 版本上会渲染成完全不相干的东西。
        /// </summary>
        private Button IconButton(Geometry geometry, string tip, double size, bool filled)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = SecondaryBrush(),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = filled ? SecondaryBrush() : Brushes.Transparent,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var viewbox = new Viewbox
            {
                Width = size,
                Height = size,
                Child = path,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var background = new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Child = viewbox,
            };

            var button = new Button
            {
                Content = background,
                Width = 24,
                Height = 22,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = tip,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            // 去掉默认按钮外观（WPF 默认的灰底+边框与这套卡片格格不入）
            button.Template = TransparentButtonTemplate();

            button.MouseEnter += (_, _) =>
                background.Background = Palette.BrushOf(Color.FromArgb(
                    Theme.IsDark ? (byte)0x26 : (byte)0x14, 0x80, 0x90, 0xA0));
            button.MouseLeave += (_, _) => background.Background = Brushes.Transparent;

            return button;
        }

        private static ControlTemplate TransparentButtonTemplate()
        {
            var root = new FrameworkElementFactory(typeof(ContentPresenter));
            root.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            root.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
            return template;
        }

        // ============================================================ 设备块
        private StackPanel BuildDevice(CardDevice device)
        {
            var panel = new StackPanel();
            var reading = device.Reading;
            var analysis = device.Analysis;

            bool online = reading.Connected && reading.Percent.HasValue;
            int percent = reading.Percent ?? 0;
            Color color = Palette.ForBattery(percent, reading.Charging);

            // ---- 第一行：标签 + 型号 ----
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = MakeText(device.Label, 11, FaintBrush());
            Grid.SetColumn(label, 0);
            head.Children.Add(label);

            if (!string.IsNullOrEmpty(reading.Name))
            {
                var model = MakeText(reading.Name, 11, FaintBrush());
                model.Margin = new Thickness(7, 0, 0, 0);
                Grid.SetColumn(model, 1);
                head.Children.Add(model);
            }
            panel.Children.Add(head);

            // ---- 第二行：电池图标 + 大号电量 + 档位/预测 ----
            var body = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var batteryIcon = BuildBatteryIcon(online ? percent : 0, reading.Charging,
                online, color);
            batteryIcon.VerticalAlignment = VerticalAlignment.Center;
            batteryIcon.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(batteryIcon, 0);
            body.Children.Add(batteryIcon);

            var bigText = online ? percent + "%" : "--";
            var big = MakeText(bigText, 24, Palette.BrushOf(online ? color : Palette.TextFaintLight),
                FontWeights.Bold);
            big.FontFamily = new FontFamily("Segoe UI Semibold");
            Grid.SetColumn(big, 1);
            body.Children.Add(big);

            var info = new StackPanel
            {
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            string levelText = Palette.LevelText(percent, reading.Charging, online);
            info.Children.Add(MakeText(levelText, 11.5, PrimaryBrush()));

            string second;
            if (!online)
            {
                string lastKnown = analysis.DescribeLastKnown();
                second = lastKnown.Length > 0 ? lastKnown : "无历史记录";
            }
            else if (reading.Charging)
            {
                second = "正在充电";
            }
            else if (analysis.Forecast.Hours.HasValue)
            {
                string confidence = string.IsNullOrEmpty(analysis.Forecast.Confidence)
                    ? ""
                    : " · 置信" + analysis.Forecast.Confidence;
                second = "预计剩余 " + analysis.Forecast.Text + confidence;
            }
            else
            {
                second = analysis.Forecast.Text;
            }
            var secondText = MakeText(second, 10.5, FaintBrush());
            secondText.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(secondText);

            Grid.SetColumn(info, 2);
            body.Children.Add(info);
            panel.Children.Add(body);

            // ---- 第三行：动画进度条 ----
            panel.Children.Add(BuildProgressBar(online ? percent : 0, color,
                reading.Charging, online));

            // ---- 第四行：24h 区间说明 ----
            string scope;
            if (analysis.MinPercent >= 0 && analysis.MaxPercent >= 0)
            {
                scope = analysis.MinPercent == analysis.MaxPercent
                    ? "近 24 小时 " + analysis.MinPercent + "%"
                    : "近 24 小时 " + analysis.MinPercent + "% ~ " + analysis.MaxPercent + "%";
                if (analysis.Forecast.RatePerHour > 0)
                {
                    scope += " · 每小时 " + analysis.Forecast.RatePerHour.ToString("0.0") + "%";
                }
                if (analysis.ActiveHours > 0)
                {
                    scope += " · 在线 " + analysis.ActiveHours + " 小时";
                }
            }
            else
            {
                scope = "近 24 小时 暂无数据";
            }
            var scopeText = MakeText(scope, 9.5, FaintBrush());
            scopeText.Margin = new Thickness(0, 7, 0, 4);
            panel.Children.Add(scopeText);

            // ---- 第五行：柱状图 ----
            var chart = new SparkChart { Height = ChartHeight };
            chart.SetData(analysis.Buckets, Theme.IsDark);
            panel.Children.Add(chart);

            return panel;
        }

        /// <summary>横向电池图标：外壳描边 + 按电量裁切的填充 + 充电闪电</summary>
        ///
        /// ⚠️ 外壳与填充必须共用同一个坐标系，否则填充会偏位。
        ///
        /// 具体做法：两者都定位到一个固定尺寸（28×14）的容器里，
        /// 并用 `Margin` 把填充向内缩，**不依赖垂直居中**。
        ///
        /// 之前踩的坑：两个 Path 各自靠 VerticalAlignment.Center 居中，
        /// 但它们的几何包围盒高度不同（外壳 14、填充 10），居中后
        /// 上边距不一致（1 vs 3），填充整体下沉 2px。
        /// 100% 电量时最明显：顶部一条白缝、底部贴死。
        /// </summary>
        private Grid BuildBatteryIcon(int percent, bool charging, bool online, Color color)
        {
            // 画布留出描边宽度，避免 Stroke 被裁掉
            var host = new Grid { Width = 30, Height = 16 };
            var canvas = new System.Windows.Controls.Canvas
            {
                Width = 28,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = false,
            };

            var shell = new System.Windows.Shapes.Path
            {
                Data = Icons.BatteryShell(28, 14),
                Stroke = Palette.BrushOf(online ? color : Palette.TextFaintLight),
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
            };
            canvas.Children.Add(shell);

            if (online && percent > 0)
            {
                var fill = new System.Windows.Shapes.Path
                {
                    Data = Icons.BatteryFill(28, 14, percent),
                    Fill = Palette.BrushOf(color),
                };
                // Canvas 只认 Left/Top，Margin 不参与布局。
                // 这里把填充显式放到描边内側：左/上各 2px，
                // 与 Icons.BatteryFill 里 "内缩 2px" 的尺寸计算对齐。
                System.Windows.Controls.Canvas.SetLeft(fill, 2);
                System.Windows.Controls.Canvas.SetTop(fill, 2);
                canvas.Children.Add(fill);
            }

            host.Children.Add(canvas);

            if (charging)
            {
                var bolt = new System.Windows.Shapes.Path
                {
                    Data = Icons.Bolt(),
                    Fill = Palette.BrushOf(Colors.White),
                    Stretch = Stretch.Uniform,
                    Width = 8,
                    Height = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                host.Children.Add(bolt);
            }

            return host;
        }

        /// <summary>
        /// 细腻的横向进度条，挂载后从 0 平滑展开。
        ///
        /// 用 Grid + 两个 Border 而不是 ProgressBar：WPF 默认 ProgressBar 的
        /// 圆角与高度都要重写模板，不如两个 Border 干净。
        /// </summary>
        private Grid BuildProgressBar(int percent, Color color, bool charging, bool online)
        {
            var host = new Grid { Height = 4, Margin = new Thickness(0, 9, 0, 0) };

            var track = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Palette.BrushOf(Theme.IsDark ? Palette.TrackDark : Palette.TrackLight),
            };
            host.Children.Add(track);

            if (!online || percent <= 0) return host;

            var fill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Palette.BrushOf(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            host.Children.Add(fill);

            host.Loaded += (_, _) =>
            {
                double available = host.ActualWidth;
                if (available <= 0) return;
                double target = available * Math.Clamp(percent / 100.0, 0, 1);
                var animation = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                fill.BeginAnimation(WidthProperty, animation);
            };

            return host;
        }
    }
}
