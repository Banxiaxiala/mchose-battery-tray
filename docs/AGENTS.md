# ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

> 本文件是本项目的最高指令。所有介入维护的 AI Agent 与开发者都必须遵守。
>
> 来源：参照 [Ferris-echo/FerrisPulse](https://github.com/Ferris-echo/FerrisPulse)
> 的 `AGENTS.md` / `AGENT_RULES.md`。原文件面向 C#/.NET Framework 4.0，
> 这里保留适用于本项目的条款，并把「C# 5.0 语法限制」换成
> 「net8.0 / C# 12 的现代写法」。
>
> **第一次接手？先读 [HANDOVER.md](HANDOVER.md)** —— 装环境、改代码、
> 排查手册、已知限制都在那里。本文件是长期约束，HANDOVER 是上手路径。
>
> **当前技术栈**：C# + WPF（`net8.0-windows`）。
> `archive/python-prototype/` 是早期 Python 实现，仅作协议与算法基准保留，
> 不再是最新产物。改协议时两边要一起对齐。

---

## 1. 零第三方依赖

- **运行时只允许 .NET 基础类库 + WPF + Win32 P/Invoke**。
  禁止 NuGet 包、第三方 DLL、图标字体包、图片资源包。
- 理由：单文件 exe 只有 **378 KB**（Python 版是 12 MB）。每引入一个包，
  体积、启动时间、崩溃面全都变差。
- 测试也不许用 xunit / nunit / MSTest —— `tests/Harness.cs` 里
  60 行手写运行器就够，`Main` 返回非 0 表示失败。
- 唯一的外部依赖是 .NET SDK 8（构建期）。它只能装到
  `C:\Program Files\dotnet`（MSI 不支持改目录，详见 `net-tool-policy` skill 3.8 节）。

## 2. 模块化分层，禁止单文件巨石化

- 所有生产代码必须位于 `src/` 下对应分层：
  - `Models/` — 纯数据与纯换算，**零 I/O**，可脱机单测
  - `Core/` — Win32 底层：`Win32.cs`（全部 P/Invoke）、`Hid.cs`（句柄与挂起读）、
    `BluetoothBattery.cs`（`BTHLE` + `BTHENUM` 电量属性，耳机与键盘共用）
  - `Hardware/` — `Protocols.cs`（纯解析，零 I/O）+ `Readers.cs`（I/O 编排）
    + `Max102Reader.cs`（维咖键盘的双通道降级）
  - `Services/` — `History.cs`（历史与预测）、`Settings.cs`、`AutoStart.cs`
  - `UI/` — `Palette.cs`（配色唯一来源）、`Icons.cs`、`SparkChart.cs`、
    `Acrylic.cs`、`BatteryCard.cs`、`SettingsPanel.cs`、`TrayIcon.cs`
- 依赖方向严格单向：`UI → Services → Hardware → Core → Models`。
  `Hardware` 严禁 import `UI`；`Models` 严禁 import 其它任何层。
- `Program.cs` 只做装配，不许出现协议字节或窗口布局。

## 3. 文件编码统一 UTF-8 with BOM

- 所有 `.cs` / `.md` 保存为 **UTF-8 with BOM**。
- **唯一的例外是 `.bat`**：必须纯 ASCII + CRLF，**绝不能有 BOM**。
  cmd.exe 会把 BOM 当成命令的一部分，第一行必然报
  「不是内部或外部命令」。中文说明写进 README。
- 运行时数据文件（`history_*.json` / `settings.json`）**必须无 BOM**：
  带 BOM 的 JSON 会让解析器直接报错。

## 4. 维护准则注释保留

- 每个源码文件头部必须永久保留：
  `⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️`
- **设备清单只能出现在 `Program.BuildDevices()` 一处**（`List<DeviceSlot>`）。
  不许再引入 `_mouse`/`_headset` 这类平行字段 —— v3 就是这么干的，
  加第三台设备要同步改 11 个地方，漏一处就静默不显示。
- 文件内的注释必须解释**为什么**这么做，而不是复述代码在做什么。
  尤其是踩过的坑：写清症状、根因、以及为什么不能改回原写法。

## 5. 托盘自愈与自启动守护

- **`WM_TASKBARCREATED` 自愈**：explorer.exe 重启后托盘图标会消失，
  必须监听 `RegisterWindowMessage("TaskbarCreated")` 并重新挂上图标。
  实现在 `UI/TrayIcon.cs` 的 `TaskbarCreatedHook`。
- **自启动三重对齐**（`Services/AutoStart.cs`）：
  1. `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  2. `HKCU\...\Explorer\StartupApproved\Run` — 必须清除「已禁用」标志，
     否则第 1 条写了也会被系统跳过
  3. `Shell:Startup` 启动文件夹快捷方式，冗余保障
- **工作目录重置**：自启动时工作目录可能是 `C:\Windows\System32`，
  `Program.Main` 首行必须切到程序目录。
- **绿色纯净**：绝不静默创建桌面快捷方式。

## 6. UI 铁律

### 6.1 每个开关必须有即时可见的回执

任何设置项被点击，都必须立刻改变自身外观（勾选、滑块、数值文案）。
**只写注册表/文件而不改界面 = 用户认为「这个按钮没用」**。
齿轮按钮曾经悄悄去切「开机自启」，注册表写成功了但界面零变化，
用户反馈就是「点设置没用」。

### 6.2 卡片尺寸改动后必须重新定位

只改 `Width` 而不调 `Reposition()`，卡片会保留旧坐标，看起来像「飘了」。

### 6.3 图标用 `Path` 画，不用字体字形

图标字体的码点在不同 Windows 版本上会渲染成完全不相干的东西
（logi-tray 踩过：`U+E0AC` 画出来是仪表盘而不是刷新箭头）。

### 6.4 几何图形靠 Path 拼时，别让两个 Path 各自居中

电池图标是「外壳 Path + 填充 Path」两层叠出来的。两个 Path 都用
`VerticalAlignment.Center` 居中会**错位**，因为 WPF 是按
**几何包围盒**对齐，而两者包围盒高度不同：

| 层 | 几何 | 包围盒高 | 16px 容器里的上边距 |
|---|---|---|---|
| 外壳 | `Rect(0,0,25,14)` | 14 | `(16-14)/2 = 1` |
| 填充 | `Rect(2,2,21,10)` | 10 | `(16-10)/2 = 3` |

→ 填充整体下沉 2px，顶部留缝、底部贴死。100% 电量时最明显。

正确做法：**统一坐标系**。外层套一个固定尺寸的 `Canvas`，
外壳画在 `(0,0)`，填充用 `Canvas.SetLeft/SetTop` 显式定位到描边内侧。
填充几何本身从 `(0,0)` 起画、只改尺寸，不要用坐标表达 inset。

### 6.5 关闭时机不能只靠焦点

`ShowActivated=false` 的卡片拿不到焦点，等 `Deactivated` 永远不会触发。
必须主动比对 `GetForegroundWindow()` + 轮询鼠标按键，两条规则并用：

1. 前台窗口不是本进程 → 收起
2. 鼠标在本进程窗口之外按下 → 收起

并且弹出后的**前几帧不判定**（那一刻窗口还在定位，光标必然在「外面」）。

### 6.6 圆角 + 亚克力靠 `WindowChrome` + DWM

`WindowStyle=None` + `AllowsTransparency=false` 才能同时拿到真圆角与亚克力。
开 `AllowsTransparency` 会**禁用**亚克力。系统不支持毛玻璃时必须退回
不透明底板，否则文字直接叠在桌面上、完全看不清。

### 6.7 WPF 与 WinForms 同时引用时要处理命名冲突

`System.Drawing.Color` / `System.Windows.Media.Color`、
`Point`、`Brush` 三组类型会撞成 CS0104。本项目关掉 `ImplicitUsings`，
用 `GlobalUsings.cs` 里的显式别名。

## 7. HID 读写的三个必踩坑（改协议前必读）

1. **`CreateFileW` 必须带 `FILE_FLAG_OVERLAPPED`**
   否则带超时的重叠读写全部失效，`ReadFile` 退化为同步阻塞，
   `PendingRead` 会永久挂死（Python 版就是这么卡了 3 分钟）。
2. **`SP_DEVICE_INTERFACE_DETAIL_DATA_W` 的路径从第 4 字节开始**
   不分 x86/x64，`cbSize` 都是 `DWORD(4)`。按 x64 写成 8 会吃掉路径前
   4 个字符，得到 `"?\hid#..."`，`CreateFile` 报错误 123。
3. **罗技接收器必须用常驻挂起读**
   不可达槽位的回绝帧只有 3~5ms，取消/重建 `ReadFile` 之间的空窗恰好
   会吃掉它。见 `Core/Hid.cs:PendingRead`。

## 8. 键盘类 HID 的 0 权限握手

- Windows 内核对键盘集合有防监听独占锁。用 `GENERIC_READ` 打开会被
  `ERROR_ACCESS_DENIED (5)` 拒绝。
- 读属性 / 能力必须用**权限 0**（`FILE_READ_ATTRIBUTES`）：
  `Win32.OpenDevice(path, readWrite: false, overlapped: false)`。
- 键鼠共用同一接收器时，必须按设备类别独立分类，状态缓存不许互相覆盖。

## 9. 历史数据的可信度

- 样本 `{t, percent, charging}`，保留 48 小时，但**永远保留最新一条**
  （它是「睡前还剩多少」的唯一来源）。
- **当前读数与历史值严格分开**：读不到时 `Reading.Percent` 保持 `null`，
  历史值只进 `Analysis.LastKnownPercent`，显示成「上次 77% · 2 小时前」。
  混在一起会让人以为设备还有 77% 电。
- **必须过滤不可能的跳变**：无线设备唤醒瞬间偶尔会报假读数
  （本机实测 51% → 59% → 51%，两次间隔 700ms）。判据是
  > 3 个百分点/分钟即丢弃 —— 实测长时间平均 0.1~0.3，高强度使用峰值
  约 1.5~2.5，取 3.0 既挡假值又不误伤真实快放电。
- **「没读到」≠「离线」**：2.4G 空口会丢帧。单帧丢失就判离线，用户看到的
  是「耳机明明戴着在用却显示掉线」（默认 30 秒刷新一次，错就错 30 秒）。
  判定必须过 `ReadingDebounce`（连续 3 次失败才算离线），
  且读取层自己也要重试（雷蛇 `QueryWithRetry` 试 2 次，罗技靠两遍探测）。
  拿到的状态值里 `-1` 一律表示「没问到」，**绝不能当 0 用** ——
  雷蛇的 `connection == 0` 是设备自报休眠，`-1` 是丢包，两者语义相反。
- **陈旧读数不得入历史**：蓝牙设备的电量是 Windows 缓存的，设备离线后
  属性仍留着上次的值（实测键盘离线 8 小时，属性还是 100%）。
  写进历史会伪造出一条水平曲线，把续航预测的斜率算成 0，
  于是界面显示“预计剩余无限久”。判据是 `Reading.Connected`，
  不是 `Percent.HasValue` —— 见 `Program.RefreshNow` 的 `live` 变量。

## 9b. 蓝牙电量（耳机与键盘共用）

两块内容永远不能混在一起：

1. **两棵枚举树都要扫**。`BTHLE`（低功耗，键盘）和 `BTHENUM`
   （经典蓝牙，耳机）缺一棵就有一类设备永远读不到 —— 耳机在蓝牙模式下
   的电量只存在于 `BTHENUM` 的音频服务子节点。
2. **电量与在线状态不在同一个节点上**。带电量那个子节点的
   `DEVPKEY_Device_IsConnected` 恒为 `False`；真正表示链路在线的
   是同一 MAC 的 `BTHENUM\DEV_<MAC>` 父节点，而它没有电量。
   所以在线状态必须按 MAC 先聚合，再挂到带电量结果的节点上。
   拿子节点的 `IsConnected` 当结论，会在耳机正放着歌时显示「蓝牙离线」。
3. **`BTHENUM` 必须先按音频服务 UUID 过滤**。那棵树下还有蓝牙串口、
   蓝牙鼠标、传真等节点，不过滤会串到别的设备上 ——
   见 `Protocols.IsBluetoothAudioInstance`。
4. **充电状态要如实报未知**。`{104EA319-…} 3` 实测恒缺失，
   不能默认成「放电中」，也不能默认成「充电中」——
   统一走 `Protocols.InterpretBluetoothState(info.Connected, charging: -1)`。
5. **状态文案只允许一处**。耳机与键盘都必须调 `InterpretBluetoothState`，
   不许各写一份，否则改一处就会瞒另一处。

## 9c. 历史图表（三层聚合）

看 24 个月趋势不能靠堆原始样本（30 秒一条 = 630 万条 ≈ 150 MB），
必须分层折叠：原始 48 小时 → 小时聚合 30 天 → 天聚合 24 个月。

**四条实测踩出来的约束：**

1. **必须先折叠再裁剪**。原始样本一旦被 `Prune` 删掉，那段就再也算不出来了。
   `Add` 里的顺序是 `Prune` → `Compact`，不能反过来。
2. **`Persist` 必须先 `LoadAll`**。之前 `Flush()` 在从未读过历史的实例上
   直接把 `_samples`（null）写成空数组 —— 用户攒了几周的曲线一次退出就没了。
   这个 bug 是靠 `AggregatesSurviveReload` 测试发现的，不是看代码看出来的。
3. **空段不补点，靠时间间隔断线**。补出来的点会伪造采样密度，把
   「设备关机 3 天」画成电量平稳的直线。断线判据放在图表层
   （`LineChart.GapBreakRatio`，典型间隔的 3 倍）。
4. **横轴必须按真实时间映射**。只按数组下标摆点时，「2 年窗口里只有
   24 小时数据」会被拉伸铺满整条横轴，看起来像两年的趋势 —— 实际只有一天。
   这个 bug 只在 `--preview ... line 17520` 下才暴露出来，日常 24 小时视图看不出来。

**柱状图也必须能选时间**。曾经把柱状图硬编码成 24 根 × 1 小时，
选 30 天时它默默不变，用户会认为「时间选择对柱状图没用」。
现在是 windowHours → 24 根柱，柱宽 = windowHours/24，卡片上写明每柱多宽。
长窗口下单桶取**区间平均**而不是末值 —— 末值可能正好是刚开机的一瞬间。

另外两条配色陷阱：

- `Palette.BatteryHigh` 与 `Palette.Charging` 是**同一个绿色** `0x22C55E`。
  用高电量档配色画折线，整条线看起来像充电段。折线用中性蓝。
- 图表的 min/max 文案来自固定的 24 小时窗口（`Analysis`），
  而折线可能正在看 2 年。标题必须跟着图表范围变，否则两边都不自洽。

## 10. 工作区与文件卫生

- **根目录纯净**：只允许 `build.bat`、`.gitignore`、`NuGet.Config`、
  `app.ico`、`icon.ico`，以及 `src/`、`tests/`、`docs/`、`assets/`、`tools/`。
  所有说明文档一律放 `docs/`，严禁在根目录倾倒临时截图、调试脚本、日志。
- **文档位置**：`docs/README.md`（说明）、`docs/ARCHITECTURE.md`（分层与决策）、
  `docs/HANDOVER.md`（交接与排查）、`docs/AGENTS.md`（本文件）。
  GitHub 首页会读根目录 README，若需要展示请放一个只含链接的极简
  `README.md`，不要把正文复制两份。
- **零二进制提交**：`bin/`、`obj/`、`dist/`、`release/`、`*.exe`、`*.pdb`
  全部在 `.gitignore` 里，严禁提交。发布走 GitHub Releases 附件。
- **收工自省**：宣布任务完成前必须 `git status` 审查工作区，主动清理临时文件。

## 11. 发布流程（留旧测新）

1. 改 `src/PeripheralBattery.csproj` 的 `<Version>`
2. 跑 `build.bat`（会自动杀进程 → 跑测试 → 编译 → 发布）
3. 测试必须 **136 项全 PASS**
   > 兼容性矩阵（`docs/COMPATIBILITY.md`）新增型号时，**必须**如实标注等级：
   > 没在真机上读过就只能是 🟡/⚪，不许写成 🟢。猜一个型号名比写「未知设备」更糟 ——
   > 用户会以为自己买到了假货。
4. 跑 `dist\外设电量.exe --probe`，两台设备都要读到
5. 手动起一次托盘，确认左键弹卡片、齿轮开设置、点别处能收起

## 12. 多设备托盘图标与改名

### 12.1 同心环的槽位必须稳定

托盘图标画 `Reading?[]`，索引 = `DeviceSlot.Ring`（0 外环 → 2 内环）。
**不能因为某台设备离线就把后面的设备前移补位** —— 用户会以为看到的
是另一台设备。离线时那圈只画浅轨道，圈数不变。

**描边宽度必须按最终显示尺寸给，不能按 64px 画布给。**
任务栏小图标在 144 DPI（150% 缩放）下只有 **24 设备像素**，
按 64px 画布给的 3.2px 描边缩下来只剩 1.2px —— 实机上细如发丝。
这是 v4 初版用户反馈「环太细」的根因。

当前参数（按 24px 反推，穷举求得的最粗解）：

| 环 | 半径（24px 逻辑） | 描边 |
|---|---|---|
| 外（鼠标） | 10.3 | 2.6 |
| 中（耳机） | 7.0 | 2.4 |
| 内（键盘） | 3.8 | 2.4 |

由此得：相邻净间隙 0.8px、中心留孔半径 2.6px、外沿 11.6px。

**四条几何硬约束**（`tests` 里有断言锁着，改参数会立刻失败）：

1. 相邻圈净间隙 ≥ 0.5px（再小就粘连）
2. 最外圈外沿 ≤ 12px（24px 图标半宽，超了被裁切）
3. 中心留孔半径 ≥ 2px（否则内圈变成实心点）
4. 每圈描边 ≥ 2px（再细在 24px 上看起来像发丝）

加粗描边就必须同时缩小半径 —— 两者抢同一份空间。
预览时**一定要缩到 24px 看**，别只看 4x/32px 大图，
大图上好看的比例缩下来经常是一团黑。

图标内部绘制尺寸取 **32px** 而不是 24：Windows 会从图标里挑最接近
实际显示尺寸的图像再缩，32→24 的损失比 64→24 小得多。

绘制实现在 `UI/TrayIconArt.cs`（纯 GDI+，可脱机 `Render()` 出图预览），
`UI/TrayIcon.cs` 只负责取主题色并把位图转成 HICON。

### 12.1.1 环的颜色与弧长

**两条语义（`ProtocolTests.RingColor*` / `RingArc*` 有断言）：**

| 项的语义 | 实现 |
|---|---|
| 弧长 = 电量占比 | 从 12 点顺时针，`sweep = 360 × percent / 100` |
| 颜色 = 电量档位 | ≤critical 深红 → ≤low 红 → ≤mid 橙 → 其余绿 |

三个坑：

1. **四个档位都必须是彩色，不能拿黑/白表示「正常」。**
   24px 托盘图标上，黑白线条和系统图标混一起分不出谁是谁；
   绿色一眼就知道「这台充足」。`RingColors.ForBattery` 必须
   把 `High/Mid/Low/Critical` 全用到 —— 只传 low/critical 的话，
   90% 和 40% 会撞成同一种橙，用户看不出变化。
2. **充电时整圈点亮**，不只画到电量位置。24px 上「弧长了多少」很难辨，
   整圈变绿才容易识别。
3. **最小可视弧长 12°。** 1% 电量在半径 10px 的环上只有约 0.2px 弧长，
   等于看不见 —— 那样「1%」和「没读数」在图标上完全一样。

颜色来自 `Palette.ForBattery()`（用户在设置面板改色会同步到托盘），
`TrayIcon` 负责把 WPF 的 `Color` 转成 GDI+ 的再传给 `TrayIconArt` ——
绘制层不 import WPF。

提示文本（`NotifyIcon.Text`）有 **63 字符硬上限**，超了会抛异常。
三台设备的完整行有 140+ 字符，必须降级。降级逻辑全在
`Models/TooltipText.cs`（可脱机单测），顺序是：
完整行 → 紧凑行（`鼠标 51%  放电中`）→ 标题 + 尽量多的紧凑行。
**绝不能写 `while (lines.Count > 1) RemoveAt(last)`** —— 旧实现这么干，
结果连标题都被删光，悬浮后什么都不显示。

### 12.2 改名必须保住历史数据

`2.4G设备电量` → `外设电量` 涉及四样东西，改漏一样用户就会丢数据或看到重复启动项：

| 项 | 位置 | 迁移方式 |
|---|---|---|
| 数据目录 | `%LOCALAPPDATA%\外设电量\` | `Settings.Directory` 首次访问时**整目录 Move**（含 history） |
| 自启动 Run 项 | `HKCU\...\Run` | `AutoStart.MigrateLegacyEntries()` 删旧名，旧的开着就替用户续开 |
| 启动文件平快捷方式 | `Shell:Startup` | 同上，删 `2.4G设备电量.lnk` |
| 单实例互斥量 | `Global\PeripheralBatteryTrayWPF*` | 换新名字，避免新旧版本各跑一个托盘 |

## 13. 完工反哺义务

- 解决新的架构共性陷阱 → 更新 `ARCHITECTURE.md`「关键设计决策」
- 新增设备私有报文细节 → 补进 `Hardware/Protocols.cs` 的注释与
  `tests/ProtocolTests.cs` 的实测帧断言（Python 侧同步）
- 发现新的 Win32 / WPF 坑 → 追加到本文件第 6、7 节，把症状与根因都写清
