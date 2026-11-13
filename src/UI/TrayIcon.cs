// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [图标用 GDI+ 现画] 不依赖 ImageList / 位图资源，直接生成 HICON。
//    画完之后必须 DestroyIcon，否则每 30 秒刷新一次会漏一个图标句柄，
//    跑几十分钟就能把 GDI 对象配额耗光（任务栏图标会变成白块）。
// 2. [左键单击即弹卡片] 按用户要求：WM_LBUTTONUP 直接切换卡片，右键才弹菜单。
//    改回「双击」会让单击变成无反应的死键。
// 3. [WM_TASKBARCREATED 自愈] explorer.exe 重启后托盘图标会消失，必须监听
//    RegisterWindowMessage("TaskbarCreated") 并重新 Shell_NotifyIcon(NIM_ADD)。
//    这是 AGENT_RULES.md 第 5 条的硬性要求。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PeripheralBattery.Core;
using PeripheralBattery.Models;
using PeripheralBattery.UI;

namespace PeripheralBattery.UI
{
    /// <summary>托盘图标：同心环图标 + 右键菜单 + 图标矩形查询</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly uint _taskbarCreated;
        private IntPtr _hicon;
        private Icon? _icon;

        public event Action? Clicked;
        public event Action? RefreshRequested;
        public event Action? AutoStartToggled;
        public event Action? ExitRequested;

        public ToolStripMenuItem AutoStartItem { get; }

        public TrayIcon()
        {
            _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

            _menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("查看电量详情", null,
                (_, _) => Clicked?.Invoke())
            { Font = new Font(_menu.Font, FontStyle.Bold) };
            _menu.Items.Add(openItem);
            _menu.Items.Add(new ToolStripMenuItem("立即刷新", null,
                (_, _) => RefreshRequested?.Invoke()));

            AutoStartItem = new ToolStripMenuItem("开机自启", null,
                (_, _) => AutoStartToggled?.Invoke());
            _menu.Items.Add(AutoStartItem);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("退出", null,
                (_, _) => ExitRequested?.Invoke()));

            _notifyIcon = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Text = "外设电量\n正在读取…",
                Visible = true,
            };
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) Clicked?.Invoke();
            };

            // 托盘重建自愈需要拿到 NotifyIcon 的内部窗口句柄。
            // NotifyIcon 没有公开句柄，但它的 Text setter 会惰性创建；
            // 这里通过反射取一次并缓存，失败就退化为「图标不可自愈但功能正常」。
            TryHookTaskbarCreated();
        }

        private void TryHookTaskbarCreated()
        {
            try
            {
                var field = typeof(NotifyIcon).GetField("window",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(_notifyIcon) is NativeWindow window)
                {
                    var hook = new TaskbarCreatedHook(this);
                    window.AssignHandle(window.Handle);
                    _hook = hook;
                }
            }
            catch (Exception) { }
        }

        private TaskbarCreatedHook? _hook;

        /// <summary>
        /// 设备插入/拔出时触发。
        ///
        /// 用 WM_DEVICECHANGE 而不是定时轮询：
        ///   * Windows 会**主动广播**，插入即响应（零延迟、零平时开销）
        ///   * 轮询要挨个试超时，14 个未接设备一轮十几秒
        /// </summary>
        public event Action? DeviceChanged;

        /// <summary>WM_DEVICECHANGE：设备数量或配置发生变化</summary>
        private const int WM_DEVICECHANGE = 0x0219;
        /// <summary>DBT_DEVICEARRIVAL：设备已插入且可用</summary>
        private const int DBT_DEVICEARRIVAL = 0x8000;
        /// <summary>DBT_DEVICEREMOVECOMPLETE：设备已移除</summary>
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private sealed class TaskbarCreatedHook : NativeWindow
        {
            private readonly TrayIcon _owner;
            public TaskbarCreatedHook(TrayIcon owner) { _owner = owner; }

            protected override void WndProc(ref Message m)
            {
                if ((uint)m.Msg == _owner._taskbarCreated)
                {
                    // explorer 重启：重新挂上图标
                    _owner._notifyIcon.Visible = true;
                }
                else if (m.Msg == WM_DEVICECHANGE)
                {
                    int evt = m.WParam.ToInt32();
                    if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                    {
                        // 不要在窗口过程里做重活（枚举 HID、开句柄可能卡几百毫秒），
                        // 那会把系统广播循环堵住。扔到线程池里异步做。
                        Action? handler = _owner.DeviceChanged;
                        if (handler != null)
                        {
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                try { handler(); }
                                catch (Exception) { }
                            });
                        }
                    }
                }
                base.WndProc(ref m);
            }
        }

        // ============================================================ 图标
        /// <summary>
        /// 生成托盘图标：三圈同心环 —— 外环 = 鼠标，中环 = 耳机，内环 = 键盘。
        /// 平时纯白/纯黑，≤30% 变黄、≤10% 变红，充电时整圈点亮并变绿。
        ///
        /// rings 按 Ring 槽位排（0 外 → 2 内）；缺的槽位传 null，
        /// 对应那圈只画浅浅的轨道，不消失 —— 圈数固定，用户才能一眼分辨
        /// “这台设备离线”和“这台设备不存在”。
        ///
        /// 具体绘制在 TrayIconArt 里（纯 GDI+，可脱机渲染预览）。
        /// 这里只负责取主题色 + 把位图变成 HICON。
        /// </summary>
        public void SetReadings(Reading?[] rings)
        {
            int size = IconSize;
            Color fore = Theme.IsDark ? Color.White : Color.FromArgb(32, 32, 32);

            // 颜色跟随设置面板里的配色（用户在设置里改色，图标同步变），
            // 阈值也用用户配置的那套，这样卡片与托盘对「同一电量」的判断一致。
            // 唯一保留的独立阈值是「告急」，因为它决定是否走最深的红色。
            var colors = new RingColors
            {
                High = ToDrawing(Palette.ForBattery(100, false)),
                Mid = ToDrawing(Palette.ForBattery(Palette.ThresholdMid, false)),
                Low = ToDrawing(Palette.ForBattery(Palette.ThresholdLow, false)),
                Critical = ToDrawing(Palette.ForBattery(Palette.ThresholdCritical, false)),
                Charging = ToDrawing(Palette.ForBattery(0, true)),
                Track = fore,
                Offline = fore,
            };

            using var bitmap = TrayIconArt.Render(
                size, rings, fore, _ringGeometry, colors,
                lowThreshold: Palette.ThresholdLow,
                criticalThreshold: Palette.ThresholdCritical,
                midThreshold: Palette.ThresholdMid);

            SetIconBitmap(bitmap);
        }

        /// <summary>
        /// 图标绘制尺寸。取 32 而不是 24：
        /// Windows 会从图标里挑最接近实际显示尺寸的图像再缩，
        /// 直接从 64 缩到 24 会明显糊，32→24 的损失小得多。
        /// </summary>
        private const int IconSize = 32;

        /// <summary>
        /// 显式指定的环形几何；null = 按实际环数自适应。
        ///
        /// ⚠️ 默认必须是 null，不能是 TrayIconArt.Default。
        /// 之前默认给了 Default（三环），于是 Render 里
        /// 「geometry == null 才按环数自适应」的分支永远不成立 ——
        /// 只有一台设备也照样画三圈。这是实测报上来的 bug。
        /// </summary>
        private RingGeometry? _ringGeometry;

        /// <summary>替换图标几何参数（调参 / 预览用）</summary>
        public void SetRingGeometry(RingGeometry geometry)
        {
            if (geometry == null)
            {
                // 显式要求「恢复自适应」
                if (_ringGeometry == null) return;
                _ringGeometry = null;
            }
            else
            {
                if (_ringGeometry != null && _ringGeometry.SameAs(geometry)) return;
                _ringGeometry = geometry;
            }
        }

        /// <summary>WPF 颜色 → GDI+ 颜色（两套 Color 类型不通用）</summary>
        private static Color ToDrawing(WpfColor color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        private static void DrawRing(Graphics g, RectangleF box, float width,
            Color fore, Reading? reading)
        {
            Color color = fore;
            bool full = false;
            float fraction = 0;

            if (reading != null)
            {
                if (reading.Charging)
                {
                    color = ToDrawing(Palette.Charging);
                    full = true;
                }
                else if (reading.Percent.HasValue)
                {
                    int percent = reading.Percent.Value;
                    color = percent <= 10 ? ToDrawing(Palette.BatteryCritical)
                          : percent <= 30 ? ToDrawing(Palette.BatteryLow)
                          : percent <= 55 ? ToDrawing(Palette.BatteryMid)
                          : fore;
                    fraction = Math.Clamp(percent / 100f, 0f, 1f);
                }
            }

            using var track = new Pen(Color.FromArgb(46, fore), width);
            g.DrawEllipse(track, box);

            // 没有读数（设备离线/不存在）就只留轨道：
            // 留轨道而不是整圈不画，是为了让用户看出“这台设备还在监控范围内，
            // 只是此刻没读到”，而不是以为工具少画了一圈。
            if (!full && fraction <= 0) return;
            using var pen = new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            if (full)
            {
                g.DrawEllipse(pen, box);
            }
            else
            {
                g.DrawArc(pen, box, -90, 360 * fraction);
            }
        }

        private void SetIconBitmap(Bitmap bitmap)
        {
            IntPtr newHandle = bitmap.GetHicon();
            if (newHandle == IntPtr.Zero) return;

            var newIcon = Icon.FromHandle(newHandle);
            var oldIcon = _icon;
            IntPtr oldHandle = _hicon;

            _icon = (Icon)newIcon.Clone();
            _hicon = newHandle;
            _notifyIcon.Icon = _icon;

            // 句柄必须显式销毁：每 30 秒刷新一次，不销毁会持续泄漏 GDI 对象
            if (oldIcon != null)
            {
                IntPtr legacy = oldHandle;
                if (legacy != IntPtr.Zero) Win32.DestroyIcon(legacy);
            }
        }

        // ============================================================ 文本
        public void SetTooltip(string text)
        {
            // NotifyIcon.Text 上限 63 字符（含换行），超出会抛异常
            if (text.Length > 63) text = text.Substring(0, 63);
            _notifyIcon.Text = text;
        }

        public void ShowNotification(string title, string message)
        {
            try
            {
                _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
            }
            catch (Exception) { }
        }

        // ============================================================ 位置
        /// <summary>
        /// 托盘图标的屏幕中心。
        ///
        /// 用 Shell_NotifyIconGetRect 而不是猜 Shell_TrayWnd 的坐标：
        /// 图标可能被折叠进「^」浮出面板，也可能被拖到副屏，只有系统自己
        /// 知道它此刻在哪。
        /// </summary>
        public Point? IconCenter()
        {
            try
            {
                var field = typeof(NotifyIcon).GetField("window",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(_notifyIcon) is not NativeWindow window) return null;

                var identifier = new Win32.NOTIFYICONIDENTIFIER
                {
                    cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(),
                    hWnd = window.Handle,
                    uID = 1,
                };
                if (Win32.Shell_NotifyIconGetRect(ref identifier, out Win32.RECT rect) != 0)
                {
                    return null;
                }
                return new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try { _notifyIcon.Visible = false; } catch (Exception) { }
            _notifyIcon.Dispose();
            _menu.Dispose();
            _hook?.ReleaseHandle();
            if (_hicon != IntPtr.Zero) Win32.DestroyIcon(_hicon);
            _icon?.Dispose();
        }
    }
}
