// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [每个开关必须有即时可见的回执] 任何一项被调整，都必须立刻改变自身外观
//    （滑块位置、数值文案、预览），不能只写文件就完事。
//    之前齿轮按钮悄悄去切「开机自启」，写成功了但界面毫无变化，
//    用户就会认为「点设置没用」。
// 2. [复用卡片的亚克力外观] 不另起一套配色，所有颜色只从 Palette 取。
// 3. [改动即生效] 回调 SettingsChanged 后由 Program 重新应用（间隔、
//    卡片宽度、图表高度），不需要用户重启程序。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using PeripheralBattery.Core;
using PeripheralBattery.Services;

namespace PeripheralBattery.UI
{
    /// <summary>设置面板：贴在卡片左侧的亚克力小窗</summary>
    public sealed class SettingsPanel : Window
    {
        private const double PanelWidth = 268;
        private const double PanelRadius = 10;

        private readonly StackPanel _root;
        private readonly Border _panelBorder;
        private bool _backdropApplied;

        private readonly Settings _settings;

        private readonly CheckBox _autoStart;
        private readonly CheckBox _lowNotify;
        private readonly CheckBox _followTheme;
        private readonly Slider _intervalSlider;
        private readonly TextBlock _intervalValue;
        private readonly Slider _widthSlider;
        private readonly TextBlock _widthValue;
        private readonly Slider _chartSlider;
        private readonly TextBlock _chartValue;
        private readonly CheckBox _autoHeight;
        private readonly Slider _heightSlider;
        private readonly TextBlock _heightValue;

        // 配色
        private readonly System.Collections.Generic.Dictionary<string, Border> _swatches = new();
        private const string SwatchHigh = "high";
        private const string SwatchMid = "mid";
        private const string SwatchLow = "low";
        private const string SwatchCritical = "critical";
        private const string SwatchCharging = "charging";

        private bool _suppressEvents;

        /// <summary>任意设置变化（已落盘）</summary>
        public event Action? SettingsChanged;

        private Rect? _cardRect;
        private int _frames;

        public SettingsPanel(Settings settings)
        {
            _settings = settings;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            Background = Brushes.Transparent;
            ShowActivated = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Topmost = true;
            Width = PanelWidth;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(PanelRadius),
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(0),
            });

            _root = new StackPanel { Margin = new Thickness(16, 12, 16, 15) };
            _panelBorder = new Border
            {
                CornerRadius = new CornerRadius(PanelRadius),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.BrushOf(Theme.IsDark
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)),
                Background = Brushes.Transparent,
                Width = PanelWidth,
                Child = _root,
            };
            Content = _panelBorder;

            // ---- 标题栏 ----
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = MakeText("设置", 12.5, Primary(), FontWeights.SemiBold);
            Grid.SetColumn(title, 0);
            bar.Children.Add(title);

            var close = new Button
            {
                Content = "\u2715",
                Width = 22,
                Height = 20,
                FontSize = 11,
                Cursor = Cursors.Hand,
                Focusable = false,
                Foreground = Secondary(),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "关闭",
            };
            close.Template = TransparentButtonTemplate();
            close.Click += (_, _) => Hide();
            Grid.SetColumn(close, 1);
            bar.Children.Add(close);
            _root.Children.Add(bar);

            // ---- 刷新间隔 ----
            _intervalSlider = new Slider
            {
                Minimum = Settings.IntervalMin,
                Maximum = Settings.IntervalMax,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = settings.Interval,
                Margin = new Thickness(0, 0, -2, 0),
            };
            _intervalValue = MakeText("", 11.5, Primary(), FontWeights.SemiBold);
            _root.Children.Add(SliderRow("刷新间隔", _intervalSlider, _intervalValue,
                "采样太稀，历史柱状图与续航预测都会失真"));
            _intervalSlider.ValueChanged += (_, e) =>
            {
                _intervalValue.Text = (int)e.NewValue + " 秒";
                if (_suppressEvents) return;
                _settings.Interval = Settings.ClampInterval((int)e.NewValue);
                Persist();
            };

            // ---- 卡片宽度 ----
            _widthSlider = new Slider
            {
                Minimum = Settings.WidthMin,
                Maximum = Settings.WidthMax,
                TickFrequency = 4,
                IsSnapToTickEnabled = true,
                Value = settings.CardWidth,
                Margin = new Thickness(0, 0, -2, 0),
            };
            _widthValue = MakeText("", 11.5, Primary(), FontWeights.SemiBold);
            _root.Children.Add(SliderRow("卡片宽度", _widthSlider, _widthValue,
                "太窄会让型号名被省略号截断"));
            _widthSlider.ValueChanged += (_, e) =>
            {
                _widthValue.Text = (int)e.NewValue + " px";
                if (_suppressEvents) return;
                _settings.CardWidth = Settings.ClampWidth(e.NewValue);
                Persist();
            };

            // ---- 图表高度 ----
            _chartSlider = new Slider
            {
                Minimum = Settings.ChartHeightMin,
                Maximum = Settings.ChartHeightMax,
                TickFrequency = 4,
                IsSnapToTickEnabled = true,
                Value = settings.ChartHeight,
                Margin = new Thickness(0, 0, -2, 0),
            };
            _chartValue = MakeText("", 11.5, Primary(), FontWeights.SemiBold);
            _root.Children.Add(SliderRow("图表高度", _chartSlider, _chartValue,
                "24 小时柱状图的高度"));
            _chartSlider.ValueChanged += (_, e) =>
            {
                _chartValue.Text = (int)e.NewValue + " px";
                if (_suppressEvents) return;
                _settings.ChartHeight = Settings.ClampChartHeight(e.NewValue);
                Persist();
            };

            // ---- 卡片高度 ----
            _heightSlider = new Slider
            {
                Minimum = Settings.CardHeightMin,
                Maximum = 720,
                TickFrequency = 10,
                IsSnapToTickEnabled = true,
                Value = settings.CardHeight > 0 ? settings.CardHeight : 470,
                Margin = new Thickness(0, 0, -2, 0),
            };
            _heightValue = MakeText("", 11.5, Primary(), FontWeights.SemiBold);
            _root.Children.Add(SliderRow("卡片高度", _heightSlider, _heightValue,
                "固定高度后内容可滚动，保证多台设备的卡片尺寸一致"));
            _heightSlider.ValueChanged += (_, e) =>
            {
                _heightValue.Text = (int)e.NewValue + " px";
                if (_suppressEvents || _autoHeight.IsChecked == true) return;
                _settings.CardHeight = Settings.ClampCardHeight(e.NewValue);
                Persist();
            };

            _autoHeight = new CheckBox
            {
                Content = MakeText("高度随内容自动（推荐）", 11, Secondary()),
                IsChecked = settings.CardHeight <= 0,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, -4, 0, 12),
            };
            _autoHeight.Checked += (_, _) =>
            {
                _heightSlider.IsEnabled = false;
                _heightSlider.Opacity = 0.45;
                if (_suppressEvents) return;
                _settings.CardHeight = 0;
                Persist();
            };
            _autoHeight.Unchecked += (_, _) =>
            {
                _heightSlider.IsEnabled = true;
                _heightSlider.Opacity = 1.0;
                if (_suppressEvents) return;
                _settings.CardHeight = Settings.ClampCardHeight(_heightSlider.Value);
                Persist();
            };
            _root.Children.Add(_autoHeight);

            _root.Children.Add(Divider());

            // ---- 配色 ----
            _root.Children.Add(MakeSectionTitle("电量配色"));

            var colorGrid = new Grid { Margin = new Thickness(0, 6, 0, 4) };
            for (int i = 0; i < 5; i++)
            {
                colorGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddSwatch(colorGrid, 0, "充足", SwatchHigh, settings.ColorHigh, Palette.BatteryHigh);
            AddSwatch(colorGrid, 1, "一般", SwatchMid, settings.ColorMid, Palette.BatteryMid);
            AddSwatch(colorGrid, 2, "偏低", SwatchLow, settings.ColorLow, Palette.BatteryLow);
            AddSwatch(colorGrid, 3, "告急", SwatchCritical, settings.ColorCritical, Palette.BatteryCritical);
            AddSwatch(colorGrid, 4, "充电", SwatchCharging, settings.ColorCharging, Palette.Charging);
            _root.Children.Add(colorGrid);

            var resetColors = new Button
            {
                Content = "恢复默认配色",
                FontSize = 10.5,
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 10),
                Background = Brushes.Transparent,
                Foreground = Secondary(),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.BrushOf(Theme.IsDark
                    ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x24, 0x00, 0x00, 0x00)),
            };
            resetColors.Click += (_, _) => ResetColors();
            _root.Children.Add(resetColors);

            var thresholdHint = MakeText(
                "档位阈值 " + settings.ThresholdCritical + "% / " + settings.ThresholdLow
                + "% / " + settings.ThresholdMid + "%，可在 settings.json 里改 threshold_*",
                9, Faint());
            thresholdHint.TextWrapping = TextWrapping.Wrap;
            thresholdHint.Margin = new Thickness(0, 0, 0, 2);
            _root.Children.Add(thresholdHint);

            _root.Children.Add(Divider());

            // ---- 开关项 ----
            _autoStart = MakeCheckBox("开机自启", AutoStart.Enabled,
                "登录 Windows 后自动在托盘常驻\n（同时写 Run 键与启动文件夹，并清除任务管理器的禁用标志）");
            _autoStart.Checked += (_, _) => { AutoStart.SetEnabled(true); SyncAutoStart(); };
            _autoStart.Unchecked += (_, _) => { AutoStart.SetEnabled(false); SyncAutoStart(); };
            _root.Children.Add(_autoStart);

            _lowNotify = MakeCheckBox("低电量提醒", settings.LowNotify,
                "任一设备降到 10% 或以下时弹一次托盘通知\n（充电后自动复位，会重新提醒）");
            _lowNotify.Checked += (_, _) =>
            {
                _settings.LowNotify = true;
                Persist();
            };
            _lowNotify.Unchecked += (_, _) =>
            {
                _settings.LowNotify = false;
                Persist();
            };
            _root.Children.Add(_lowNotify);

            _followTheme = MakeCheckBox("跟随系统主题", settings.FollowTheme,
                "关掉则托盘图标固定用白线");
            _followTheme.Checked += (_, _) =>
            {
                _settings.FollowTheme = true;
                Persist();
            };
            _followTheme.Unchecked += (_, _) =>
            {
                _settings.FollowTheme = false;
                Persist();
            };
            _root.Children.Add(_followTheme);

            var pathNote = MakeText("设置保存在 %LOCALAPPDATA%\\外设电量\\settings.json",
                9, Faint());
            pathNote.TextWrapping = TextWrapping.Wrap;
            pathNote.Margin = new Thickness(0, 2, 0, 0);
            _root.Children.Add(pathNote);

            SourceInitialized += (_, _) => ApplyBackdrop();
            ContentRendered += (_, _) => ApplyBackdrop();

            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };
            timer.Tick += (_, _) => CheckDismiss();
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) { _frames = 0; timer.Start(); }
                else timer.Stop();
            };
            Closed += (_, _) => timer.Stop();

            RefreshValues();
        }

        // ============================================================ 状态
        private void RefreshValues()
        {
            _suppressEvents = true;
            try
            {
                _intervalSlider.Value = _settings.Interval;
                _intervalValue.Text = _settings.Interval + " 秒";
                _widthSlider.Value = _settings.CardWidth;
                _widthValue.Text = (int)_settings.CardWidth + " px";
                _chartSlider.Value = _settings.ChartHeight;
                _chartValue.Text = (int)_settings.ChartHeight + " px";

                bool auto = _settings.CardHeight <= 0;
                _autoHeight.IsChecked = auto;
                _heightSlider.IsEnabled = !auto;
                _heightSlider.Opacity = auto ? 0.45 : 1.0;
                _heightSlider.Value = auto ? 470 : _settings.CardHeight;
                _heightValue.Text = auto ? "自动" : (int)_settings.CardHeight + " px";
                _autoStart.IsChecked = AutoStart.Enabled;
                _lowNotify.IsChecked = _settings.LowNotify;
                _followTheme.IsChecked = _settings.FollowTheme;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>每次显示前回读真实状态（别的入口可能改过开机自启）</summary>
        public void SyncFromSystem()
        {
            RefreshValues();
        }

        private void Persist()
        {
            _settings.Save();
            SettingsChanged?.Invoke();
        }

        /// <summary>
        /// 写注册表可能失败（权限/组策略），回读真实状态再刷新勾选，
        /// 避免出现「界面上打着勾、实际没生效」的假象。
        /// </summary>
        private void SyncAutoStart()
        {
            bool actual = AutoStart.Enabled;
            if (_autoStart.IsChecked != actual)
            {
                _suppressEvents = true;
                _autoStart.IsChecked = actual;
                _suppressEvents = false;
            }
        }

        // ============================================================ 关闭
        private void CheckDismiss()
        {
            if (!IsVisible) return;
            if (_frames < 3) { _frames++; return; }

            if (!Win32Cursor.TryGetCursorPos(out int cx, out int cy)) return;

            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            bool inSelf = cx >= topLeft.X && cx <= bottomRight.X
                       && cy >= topLeft.Y && cy <= bottomRight.Y;

            bool inCard = false;
            if (_cardRect.HasValue)
            {
                var r = _cardRect.Value;
                inCard = cx >= r.Left && cx <= r.Right && cy >= r.Top && cy <= r.Bottom;
            }

            if (cx < 0 || cy < 0) return;

            // 前台跑到别的程序：面板收起（与卡片同一规则）
            IntPtr foreground = Win32.GetForegroundWindow();
            if (foreground != IntPtr.Zero && !Win32.IsOwnWindow(foreground)
                && !inSelf && !inCard)
            {
                Hide();
                return;
            }

            bool pressed = Mouse.LeftButton == MouseButtonState.Pressed
                        || Mouse.RightButton == MouseButtonState.Pressed;
            if (pressed && !inSelf && !inCard) Hide();
        }

        // ============================================================ 外观
        private void ApplyBackdrop()
        {
            if (_backdropApplied) return;
            _backdropApplied = true;
            if (!Acrylic.Apply(this, Theme.IsDark, PanelRadius))
            {
                Acrylic.Clear(this);
                _panelBorder.Background = Palette.BrushOf(Theme.IsDark
                    ? Palette.SurfaceDark
                    : Palette.SurfaceLight);
            }
        }

        private Brush Primary()
        {
            return Palette.BrushOf(Theme.IsDark ? Palette.TextPrimaryDark : Palette.TextPrimaryLight);
        }

        private Brush Secondary()
        {
            return Palette.BrushOf(Theme.IsDark ? Palette.TextSecondaryDark : Palette.TextSecondaryLight);
        }

        private Brush Faint()
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
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private Border Divider()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(0, 12, 0, 12),
                Background = Palette.BrushOf(Theme.IsDark
                    ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
            };
        }

        /// <summary>一行滑块：左标签 + 右数值 + 下方滑块 + 说明</summary>
        private StackPanel SliderRow(string label, Slider slider, TextBlock value,
            string note)
        {
            var head = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelText = MakeText(label, 11.5, Primary());
            Grid.SetColumn(labelText, 0);
            head.Children.Add(labelText);
            Grid.SetColumn(value, 1);
            head.Children.Add(value);

            var noteText = MakeText(note, 9, Faint());
            noteText.TextWrapping = TextWrapping.Wrap;
            noteText.Margin = new Thickness(0, 3, 0, 0);

            var host = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            host.Children.Add(head);
            host.Children.Add(slider);
            host.Children.Add(noteText);
            return host;
        }

        private TextBlock MakeSectionTitle(string text)
        {
            return MakeText(text, 11, Secondary(), FontWeights.SemiBold);
        }

        /// <summary>
        /// 一个色块。点击时循环切换预设色 —— 比弹 ColorDialog 省事，
        /// 也避免用户选出一个荧光粉把界面毁了。
        /// </summary>
        private void AddSwatch(Grid host, int column, string label, string key,
            string current, Color fallback)
        {
            Color shown = ParseOrDefault(current, fallback);

            var stack = new StackPanel { Margin = new Thickness(0, 0, 5, 0) };
            var chip = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = Palette.BrushOf(shown),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.BrushOf(Color.FromArgb(0x20, 0, 0, 0)),
                Cursor = Cursors.Hand,
                ToolTip = label + "（点击切换预设）",
            };
            var caption = MakeText(label, 8.5, Faint());
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 3, 0, 0);

            stack.Children.Add(chip);
            stack.Children.Add(caption);
            Grid.SetColumn(stack, column);
            Grid.SetRow(stack, 0);
            host.Children.Add(stack);

            _swatches[key] = chip;

            int index = IndexOfPreset(key, shown);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                var presets = Presets(key);
                index = (index + 1) % presets.Length;
                Color next = presets[index];
                chip.Background = Palette.BrushOf(next);
                ApplyColor(key, next.R.ToString("X2") + next.G.ToString("X2")
                    + next.B.ToString("X2"));
            };
        }

        private static Color ParseOrDefault(string text, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            string value = text.Trim().TrimStart('#');
            if (value.Length != 6) return fallback;
            try
            {
                uint packed = Convert.ToUInt32(value, 16);
                return Color.FromRgb((byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }
            catch (Exception) { return fallback; }
        }

        private static Color[] Presets(string key)
        {
            switch (key)
            {
                case SwatchHigh:
                case SwatchCharging:
                    return new[]
                    {
                        Color.FromRgb(0x22, 0xC5, 0x5E),
                        Color.FromRgb(0x10, 0xB9, 0x81),
                        Color.FromRgb(0x00, 0x67, 0xC0),
                        Color.FromRgb(0x60, 0xCD, 0xFF),
                        Color.FromRgb(0x8B, 0x5C, 0xF6),
                    };
                case SwatchMid:
                    return new[]
                    {
                        Color.FromRgb(0xF5, 0xA5, 0x24),
                        Color.FromRgb(0xE8, 0xB9, 0x2E),
                        Color.FromRgb(0xF9, 0x73, 0x16),
                        Color.FromRgb(0xCA, 0x8A, 0x04),
                    };
                default:
                    return new[]
                    {
                        Color.FromRgb(0xEF, 0x44, 0x44),
                        Color.FromRgb(0xDC, 0x26, 0x26),
                        Color.FromRgb(0xF4, 0x3F, 0x5E),
                        Color.FromRgb(0xB9, 0x1C, 0x1C),
                    };
            }
        }

        private static int IndexOfPreset(string key, Color color)
        {
            var presets = Presets(key);
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].R == color.R && presets[i].G == color.G
                    && presets[i].B == color.B)
                {
                    return i;
                }
            }
            return 0;
        }

        private void ApplyColor(string key, string hex)
        {
            switch (key)
            {
                case SwatchHigh: _settings.ColorHigh = hex; break;
                case SwatchMid: _settings.ColorMid = hex; break;
                case SwatchLow: _settings.ColorLow = hex; break;
                case SwatchCritical: _settings.ColorCritical = hex; break;
                case SwatchCharging: _settings.ColorCharging = hex; break;
                default: return;
            }
            Persist();
        }

        private void ResetColors()
        {
            _settings.ColorHigh = "";
            _settings.ColorMid = "";
            _settings.ColorLow = "";
            _settings.ColorCritical = "";
            _settings.ColorCharging = "";
            _settings.Save();

            _swatches[SwatchHigh].Background = Palette.BrushOf(Palette.BatteryHigh);
            _swatches[SwatchMid].Background = Palette.BrushOf(Palette.BatteryMid);
            _swatches[SwatchLow].Background = Palette.BrushOf(Palette.BatteryLow);
            _swatches[SwatchCritical].Background = Palette.BrushOf(Palette.BatteryCritical);
            _swatches[SwatchCharging].Background = Palette.BrushOf(Palette.Charging);

            SettingsChanged?.Invoke();
        }

        /// <summary>带说明的两行复选框</summary>
        private CheckBox MakeCheckBox(string label, bool isChecked, string description)
        {
            var stack = new StackPanel();
            stack.Children.Add(MakeText(label, 11.5, Primary()));

            var note = MakeText(description, 9, Faint());
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(note);

            return new CheckBox
            {
                Content = stack,
                IsChecked = isChecked,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 11),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
        }

        private static ControlTemplate TransparentButtonTemplate()
        {
            var root = new FrameworkElementFactory(typeof(ContentPresenter));
            root.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            root.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            return new ControlTemplate(typeof(Button)) { VisualTree = root };
        }

        /// <summary>贴到卡片左侧；放不下就贴右侧</summary>
        public void PlaceNear(Rect cardRect)
        {
            _cardRect = cardRect;
            UpdateLayout();
            double width = ActualWidth > 0 ? ActualWidth : PanelWidth;
            double height = ActualHeight > 0 ? ActualHeight : 420;
            var work = SystemParameters.WorkArea;

            double x = cardRect.Left - width - 10;
            if (x < work.Left + 8) x = cardRect.Right + 10;
            x = Math.Clamp(x, work.Left + 8, work.Right - width - 8);

            double y = Math.Clamp(cardRect.Top, work.Top + 8, work.Bottom - height - 8);
            Left = x;
            Top = y;
        }
    }
}
