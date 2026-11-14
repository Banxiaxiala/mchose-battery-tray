# 交接文档（HANDOVER）

> 给接手这个项目的人。目标：**30 分钟内能改一行代码、跑起来、看到效果**。
>
> 当前版本 `4.2.0`。若本文与你看到的代码不一致，**以代码为准**，
> 并把过时的地方顺手改掉（这是 AGENTS.md 第 13 条的要求）。

---

## 1. 一分钟认识这个项目

一个 Windows 托盘小工具：**一个托盘图标同时显示多台外设的电量**
（环数 = 设备数），左键单击弹出亚克力卡片，看历史曲线与续航预测。

| 项 | 值 |
|---|---|
| 技术栈 | C# 12 + WPF，`net8.0-windows` |
| 代码量 | 生产代码 30 个文件 / 约 15100 行；测试 7 个文件 / 约 4100 行 |
| 单文件 exe | 约 503 KB（framework-dependent） |
| 第三方依赖 | **零**（只用 BCL + WPF + Win32 P/Invoke） |
| 测试 | **242 项**脱机断言，全部通过 |
| 许可证 | **GPL-3.0**（派生自 GPL 项目，见根 README 致谢） |
| 内置设备 | **14 台**，覆盖 **14 种协议** |
| 真机实测设备 | 罗技 G304/PRO X 2、雷蛇 BlackShark V2 Pro（2.4G + 蓝牙）、维咖 MAX102（2.4G + 蓝牙） |

### 这个项目现在的形态

**从「写死三台的托盘工具」变成了「设备表驱动的框架」**：

- 设备清单是**数据**（`Hardware/DeviceCatalog.cs`），加型号不改代码
- 用户也能在 `data\devices.json` 里加自定义型号，不用重编译
- 一台设备可以由**多条通道**组成（2.4G 优先、蓝牙回落），按顺序降级
- 支持 14 种协议：罗技 HID++、雷蛇 PA/PI、VIA/QMK、蓝牙属性、
  以及 10 家厂商的私有报文（ATK / 飞智 / 漫步者 / 迈从鼠标 / 迈从键盘 /
  GameSir / 华硕 ROG / HyperX / 宏碁 / NuPhy / 高斯）

### 证据分级（重要）

只有 **3 台**是作者真机实测过的（🟢）。其余 11 台是**照公开参考实现移植**的
（🟡），代码已接入、有断言，但**没在对应真机上验证过**。

改动这些 🟡 协议时请保守：帧格式的来源写在 `VendorProtocols.cs` 的注释里，
不要凭推测改字节偏移。详见 [COMPATIBILITY.md](COMPATIBILITY.md)。

详见 [ARCHITECTURE.md](ARCHITECTURE.md) 的「设备表驱动」「蓝牙通道」
「同类项目调研结论」三节。

---

## 2. 第一天要做的事

### 2.0 新机器从零接手（5 步）

```bat
:: 1) 拿代码
git clone https://github.com/Banxiaxiala/mchose-battery-tray.git
cd mchose-battery-tray

:: 2) 装 .NET SDK 8（见下一节，含不装 C 盘的方案）

:: 3) 跑测试，先确认基线是绿的
dotnet run --project tests\PeripheralBattery.Tests.csproj
::    应看到「共 242 项，通过 242，失败 0」

:: 4) 编译并验证能读到设备
build.bat
dist\外设电量.exe --probe

:: 5) 起托盘看实际效果
dist\外设电量.exe
```

**前三步不需要任何硬件**。第 4 步若一台设备都没读到，属于正常 —— 
说明这台机器上没有受支持的设备，看 `--probe` 输出里每台设备的报错原因即可
（「没有找到 XXX 接口」= 设备没插；「没有应答」= 插了但休眠/固件不支持）。

接手前必读的三件事：

1. **[COMPATIBILITY.md](COMPATIBILITY.md)** —— 哪些设备是真机验证过的（🟢），
   哪些只是「照公开实现移植、没验证」（🟡）。**改 🟡 协议要格外保守**。
2. **[AGENTS.md](AGENTS.md)** —— 硬性约束。尤其第 6 条（UI 铁律）与
   第 9 条（数据可信度），那些都是踩过坑写下来的。
3. **本文第 5 节「必须知道的坑」** —— 改协议/改 UI 之前看一遍能省半天。

### 2.1 装环境

只需要 **.NET SDK 8**：

```powershell
winget install Microsoft.DotNet.SDK.8
# 装完重开终端，验证：
dotnet --list-sdks     # 应看到 8.0.x
dotnet --list-runtimes # 应看到 Microsoft.WindowsDesktop.App 8.0.x
```

> ⚠️ SDK 的 MSI 安装器**不支持改安装目录**，只能装在 `C:\Program Files\dotnet`。
>
> **不想装到 C: 盘的话，`dotnet-install.ps1` 是可以的**（v4 交接时实测）：
>
> ```powershell
> curl -sSL -o dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
> powershell -ExecutionPolicy Bypass -File .\dotnet-install.ps1 -Channel 8.0 -InstallDir D:\tool\dotnet -NoPath
> ```
>
> 它会连 `Microsoft.WindowsDesktop.App`（WPF 运行时）一起装进 `InstallDir`，
> 之后用绝对路径 `D:\tool\dotnet\dotnet.exe build ...` 即可。
>
> 早前版本的本文档说「绿色版不含 WPF 运行时」是错的 —— 那是
> `-Runtime dotnet`（只装 .NET 运行时、不装桌面框架）的用法，别混。
>
> 另一个坑：很多机器上**只装了运行时、没装 SDK**（`dotnet --list-runtimes`
> 有 WindowsDesktop，`dotnet --list-sdks` 却是空的）。这时 `dotnet build`
> 会直接报「No .NET SDKs were found」，先确认 SDK 在不在。

### 2.2 跑起来

```bat
build.bat
```

它做四件事：杀残留进程 → 跑 51 项测试 → 编译 → 发布到 `dist\`。

想跳过测试只编译：

```bat
dotnet build src\PeripheralBattery.csproj
```

### 2.3 验证设备能读到

```bat
dist\外设电量.exe --probe
```

正常输出：

```
设备探测
============================================================

[罗技]
  PRO X 2
    协议    HID++
    电量    48%
    状态    放电中
    在线    是

[雷蛇]
  BlackShark V2 Pro
    协议    PA-PI
    电量    38%
    状态    放电中
    在线    是
```

耳机切换成蓝牙模式（不插 dongle）后，同一组会变成：

```
[雷蛇]
  BlackShark V2 Pro
    协议    蓝牙
    电量    100%
    状态    蓝牙已连接（充电状态未知）
    在线    是
```

> 「充电状态未知」是正常的：系统的蓝牙电量属性里没有充电位
> （`{104EA319-…} 3` 实测恒缺失），不能默认成「放电中」。

如果三台都是「读取失败」，见本文第 6 节排查。

### 2.4 出问题先开 `--trace`

**这是排查「读不到」最快的手段**，别靠猜：

```bat
dist\外设电量.exe --probe --trace
```

它会把每一步的原始报文都打出来：

```
┌─ 罗技 HID 接口 ──────────────────────────────────────────────
│  046D:C09B up=FF00 u=0001 io=7/7 feat=0 prod=PRO X 2
│  046D:C54D up=FF00 u=0002 io=20/20 feat=0 prod=USB Receiver
│  配成 2 个接收器
│  [罗技 slot=1 feat=0x00 fn=0] 发 →  11 01 00 0A 10 04 00 00 …
│  收 ←  11 01 00 0A 06 00 05 00 00 …
│  [罗技 slot=1 feat=0x06 fn=1] 发 →  11 01 06 1A 00 00 …
│  收 ←  11 01 06 1A 64 08 03 01 …   (0x64=100%, 0x03=已充满)
```

怎么读：

| 现象 | 含义 |
|---|---|
| `收 ← 短集合错误帧（该槽位不可达）` | 接收器明确说这个槽位没设备，**正常**，不是错误 |
| `配成 N 个接收器` | 每个端点算一个。插着数据线时会多出一个「设备直连」端点 |
| `收 ← （四字段不匹配，丢弃）` | 收到的是设备主动推送的通知，或上一次请求的迟到应答 |
| `槽位 N: 应答里没有可用电量` | 设备应答了，但三条电量特性都不支持 |
| 完全没有 `收 ←` | 设备休眠 / 未配对 / 被厂商软件独占 |

开关实现在 `Core/Tracer.cs`，默认关闭（`Enabled=false` 时每次调用只是一次
静态布尔判断，不影响 30 秒一次的轮询性能）。

> ⚠️ 托盘模式下没有控制台，别在那里开 `--trace`；用 `--probe` 配合。

---

## 3. 代码在哪

```
build.bat                 ← 一键构建（自动定位装了 SDK 的 dotnet）
docs/                     ← 本文件、README、ARCHITECTURE、AGENTS、COMPATIBILITY
src/
  PeripheralBattery.csproj  ← 唯一的生产工程文件（net8.0-windows）
  Program.cs              ← 装配层：设备表 + 刷新循环 + 交互。改「怎么跑」看这里
  Models/
    Models.cs             ← 纯数据 Reading/Sample/Bucket/Forecast/Analysis
                            + 设备身份规则、屏幕坐标夹取（都可脱机测）
    TooltipText.cs        ← 悬停提示拼装与 63 字符降级
  Core/
    Win32.cs              ← 全项目唯一的 P/Invoke 声明。加系统 API 来这
    Hid.cs                ← HID 枚举 + 句柄 + 常驻挂起读 + Feature Report
    BluetoothBattery.cs   ← BTHLE+BTHENUM 电量属性（两棵树都要扫）
    Tracer.cs             ← --trace 日志（同时写 data\logs）
  Hardware/
    DeviceCatalog.cs      ← ★ 设备表 + 协议链声明。**加型号只改这里**
    DeviceBinder.cs       ← 把设备定义装配成读函数
    Protocols.cs          ← 罗技/雷蛇/VIA 三套协议的纯解析
    VendorProtocols.cs    ← 10 家厂商私有报文（纯解析，零 I/O）
    Readers.cs            ← 罗技/雷蛇的 I/O 编排
    Max102Reader.cs       ← VIA 族键盘（维咖/高斯），参数化
    VendorReader.cs       ← 厂商私有报文的统一 I/O 骨架
  Services/
    History.cs            ← 三层聚合历史（48h/30d/24月）+ 预测 + 跳变过滤
    Settings.cs           ← 设置持久化、便携目录、备注、通道别名
    AutoStart.cs          ← 三重自启动对齐
  UI/
    Palette.cs            ← 配色唯一来源。改颜色只改这里
    Icons.cs              ← 矢量图形（电池/闪电/刷新/齿轮）
    SparkChart.cs         ← 柱状图（自绘，支持悬停提示）
    LineChart.cs          ← 折线图（真实时间轴，断线，悬停提示）
    Acrylic.cs            ← DWM 毛玻璃 + 圆角（两级回退）
    BatteryCard.cs        ← 卡片窗口。改布局看这里
    SettingsPanel.cs      ← 设置面板 + 二级「管理设备」窗口
    TrayIcon.cs           ← 托盘图标 + 菜单 + 位置查询 + 设备接入广播
    TrayIconArt.cs        ← 环状图标绘制（环数随设备数自适应）
    Win32Cursor.cs        ← 光标位置薄封装
  Tools/
    CardPreview.cs        ← --preview：合成数据渲染卡片并截图
    TrayVectorPreview.cs  ← --tray / --traybar：输出矢量 SVG
tests/
  PeripheralBattery.Tests.csproj ← 242 项脱机断言，链接 src 下的源文件
  Harness.cs              ← 60 行手写测试运行器（不用 xunit）
  CatalogTests.cs         ← 设备表、身份、备注、标题栏计数
  AggregateTests.cs       ← 三层聚合折叠与重采样
  HistoryTests.cs         ← 历史/预测/设置
  ProtocolTests.cs        ← 三套主协议的真机帧断言
  VendorProtocolTests.cs  ← 10 家厂商报文的帧构造与校验
  PaletteTests.cs         ← 配色阈值、托盘几何、按钮文案
tools/
  draw_diagrams.py        ← 生成 README 的 SVG 示意图
  list_files.py           ← 交接自检（必需文件是否齐全）
```

### 依赖方向（不要违反）

```
Program → UI → Services → Hardware → Core → Models
```

- `Models` 不依赖任何其它层
- `Hardware` 不许 `using PeripheralBattery.UI`
- `Program.cs` 只做装配，不许出现协议字节或窗口布局

---

## 4. 常见改动怎么下手

### 4.1 改颜色

`UI/Palette.cs`。注意每组颜色都有浅色/深色两份，别只改一个。

电量档位色（用户也能在设置里覆盖）：
```csharp
public static readonly Color BatteryHigh = Color.FromRgb(0x22, 0xC5, 0x5E);
```

### 4.2 改卡片布局

`UI/BatteryCard.cs` 的 `BuildDevice()`。顺序是：
标签行 → 电池图标 + 大号电量 + 档位 → 进度条 → 24h 区间 → 柱状图。

### 4.3 改刷新逻辑

`Program.RefreshNow()`。目前跑在 UI 线程（HID 读取约 130ms + 55ms，可接受）。
要加设备的话建议改成 `Task.Run` + `Dispatcher.Invoke`。

### 4.4 支持新设备

三步：

1. 抓包，把真机帧补进 `tests/ProtocolTests.cs` 做断言
2. 在 `Hardware/Protocols.cs` 加纯解析函数（只吃 `byte[]`，不碰 I/O）
3. 在 `Hardware/Readers.cs` 加 I/O 编排，然后在 `Program` 里接上

**先写测试断言，再写实现** —— 协议是逆向来的，没有抓包帧的解析代码
等于没有约束，改一行就可能悄悄错位。

抓包帧直接从 `--probe --trace` 的输出里抄，格式与断言里的
`byte[]` 一一对应（`Core/Tracer.Hex()` 输出的就是 `XX XX XX` 空格分隔）。

### 4.5 加一个设置项

1. `Services/Settings.cs` 加属性（给默认值）+ 在 `Sanitize()` 里校验
2. `UI/SettingsPanel.cs` 加控件
3. `Program.ApplySettings()` 里应用
4. `tests` 加断言

---

## 5. 必须知道的坑

这些都在代码注释里写了「为什么」，这里汇总一下。

### 5.1 HID 三连坑

| 坑 | 症状 | 正确做法 |
|---|---|---|
| `CreateFileW` 忘了 `FILE_FLAG_OVERLAPPED` | 带超时的重叠 I/O 全失效，`PendingRead` 永久挂死 | `Win32.OpenDevice(..., overlapped: true)` |
| `SP_DEVICE_INTERFACE_DETAIL_DATA_W` 路径偏移写成 8 | 得到 `"?\hid#..."`，`CreateFile` 报错误 123 | 偏移固定 **4**（x64 也是 4，不是 8） |
| 罗技读取时每次 poll 都取消重建 `ReadFile` | 空槽位永远判不出来，只能等满超时 | 用 `PendingRead` 让读请求一直挂着 |

### 5.2 键盘类 HID 要 0 权限

用 `GENERIC_READ` 打开键盘集合会被 `ERROR_ACCESS_DENIED (5)` 拒绝。
读属性必须 `Win32.OpenDevice(path, readWrite: false, overlapped: false)`。

### 5.3 WPF 与 WinForms 命名冲突

`System.Drawing.Color` vs `System.Windows.Media.Color`、
`Point`、`Brush` 三组类型会撞成 CS0104。
本项目关掉 `ImplicitUsings`，用 `GlobalUsings.cs` 里的显式别名。

### 5.4 图标不能用字体字形

图标字体的码点在不同 Windows 版本上会渲染成完全不相干的东西
（参考项目 logi-tray 踩过：`U+E0AC` 画出来是仪表盘而不是刷新箭头）。
所以 `UI/Icons.cs` 全部用 `Geometry` 现画。

### 5.5 卡片关闭时机

`ShowActivated=false` 让卡片拿不到焦点，等 `Deactivated` 永远不触发。
必须主动比对 `GetForegroundWindow()` + 轮询鼠标按键，两条规则并用。
并且弹出后**前 3 帧不判定**（那一刻窗口还在定位）。

### 5.6 `.bat` 不能有 BOM

cmd.exe 会把 BOM 当命令的一部分，第一行必然报
「不是内部或外部命令」。`.bat` 必须纯 ASCII + CRLF。

### 5.7 `build.bat` 必须先杀进程

程序在跑时 `dotnet build` 会报 MSB3021（exe 被占用）。
脚本里用 PowerShell 按进程路径匹配来杀 —— 中文 exe 名在纯 ASCII 的
`.bat` 里写不进去，`taskkill /IM` 匹配不可靠。

---

## 6. 排查手册

### 罗技显示「充电中 100%」，但鼠标明明是无线用的

说明程序读到的是**设备直连端点**，不是接收器。这完全正常：

- 鼠标**插着数据线**时，会自己枚举出一个 HID++ 端点
  （本机是 `046D:C09B`，product 为 `PRO X 2`）
- 同时接收器（`046D:C54D`，"USB Receiver"）还在，但**它上面已经没有设备了** ——
  鼠标一切到有线模式就不再通过 2.4G 上报，接收器对 1~7 号槽位全部回「不可达」

`GroupReceivers()` 会**优先试接收器**，接收器读不到才回退到直连端点。
用 `--trace` 可以看到接收器的 7 个槽位全部返回错误帧，然后才落到直连：
这正说明逻辑走对了，不是 bug。

想验证无线链路：**拔掉数据线**，再跑一次 `--probe`。

### 所有设备都读不到

```bat
dist\外设电量.exe --probe --trace
```

**维咖键盘读不到 / 显示「固件没有电量补丁」**

键盘有三种形态，先看 `--probe` 末尾的 `[蓝牙电量属性]` 段确认当前走哪条：

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 「固件没有电量补丁（发 cmd 20 回 0xFF）」 | 原厂固件，`cmd 20` 是补丁命令 | 刷 `weikav-max102-re/firmware/max102_patched_battery.bin` |
| 「USB 有线模式，无线模块未工作」 | 插着数据线，`md_info` 全零 | 拔线用 2.4G，或切到蓝牙通道 |
| 「没有找到维咖 VIA 接口」 | dongle 不在 / 键盘关机 | 插 dongle，按 FN 切到 2.4G 通道 |
| 「没有找到蓝牙通道的 MAX102」 | 系统没记录过电量 | 先蓝牙连一次，等 Windows 同步（几秒） |
| 电量一直是旧值 | 属性是缓存的 | 看 `在线` 字段；离线时的值不代表当前 |

> ⚠️ **绝对不要试图用 `cmd 6` 读电量** —— 那是「恢复出厂默认键位」，
> 会把用户改的键位全冲掉。

**罗技读不到**

1. 接收器插好了吗？设备管理器里应有 `USB Receiver`（`046D:C54D`）
2. 鼠标休眠了？摇一下鼠标再试（唤醒首帧可能慢到 850ms，
   代码里第二遍探测给了 1.3 秒窗口）
3. G HUB 正在独占？关掉 G HUB 再试（同一 HID 集合，Windows 只把
   报告投给其中一个持有句柄的进程）

**雷蛇读不到**

1. 耳机开机了吗？关机时接收器还在但设备不应答，会报
   「雷蛇电量查询无应答（耳机可能已休眠）」
2. 按蓝牙模式在用（没插 dongle）时，走的是系统电量属性：
   `--probe` 末尾的「[蓝牙电量属性]」应该能看到
   `Razer BlackShark V2 Pro BT Hands-Free AG`。看不到就说明
   系统也没拿到电量 —— 在「设置 → 蓝牙」里看一眼卡片上有没有百分比。
3. 用 `Windows PowerShell` 跑一次 `--probe`，看是否被权限拦住

> 耳机蓝牙模式有两个经典坑（都是本次补丁踩过的）：
> 电量在 `BTHENUM` 而不是 `BTHLE`；电量节点的 `IsConnected` 恒为
> `False`，在线状态必须按 MAC 去父节点 `BTHENUM\DEV_<MAC>` 取。
> 详见 ARCHITECTURE 的「蓝牙通道」一节。

### 托盘图标不显示

Windows 默认把新图标折叠进「^」里。从「^」把图标拖到任务栏固定即可。

### 卡片位置不对

检查 `BatteryCard.Reposition()`。必须等 `ActualHeight > 0` 才能算位置，
所以定位挂在 `ContentRendered` / `SizeChanged` 上，不能放在构造函数里。

### 测试挂了

```bat
dotnet run --project tests
```

会逐项打印 PASS/FAIL 与失败原因。测试零硬件依赖，挂了一般是真改坏了。
当前是 242 项。

---

## 7. 数据文件

```
%LOCALAPPDATA%\外设电量\
  history_logitech.json   罗技历史样本（48 小时，最多约 170 KB）
  history_razer.json      雷蛇历史样本
  history_max102.json     维咖键盘历史样本
  settings.json           用户设置
```

> 目录名在 v4 从 `2.4G设备电量` 改成了 `外设电量`。
> `Settings.Directory` 首次访问时会**整目录 Move** 过来（历史一起搬），
> 搬不动（旧进程占用）就退回旧目录继续用，不会丢数据。

- **都不带 BOM**（带 BOM 的 JSON 会让 Python 等解析器直接报错）
- 删掉即恢复初始状态
- 换电脑想保留历史曲线，把整个文件夹拷过去

---

## 8. 打包分发

### 给自己用（默认）

```bat
build.bat
```

产出 `dist\外设电量.exe`，**503 KB**。
需要目标机已装 `Microsoft.WindowsDesktop.App` 8.0 运行时。

### 给别人用（自包含）

```bat
dotnet publish src\PeripheralBattery.csproj -c Release ^
  -p:PublishSingleFile=true -p:SelfContained=true ^
  -p:RuntimeIdentifier=win-x64 -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist_sc
```

产出 **66 MB**，开箱即用，不依赖任何已装运行时。

### `.pdb` 不用分发

调试符号文件，只有把崩溃堆栈还原成源码行号时才用。运行不需要。

---

## 8b. 仓库是 fork，但别用 Sync fork

GitHub 上本仓库标记为 fork 自
[yszyszysz233/2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer)，
这是为了记录方法论来源（把「枚举 HID → 抓包 → 试命令 → 爆破校验」
写成可复用 Prompt）。

**但这个 fork 与上游没有可合并的代码**：

| | 上游 | 本仓库 |
|---|---|---|
| 语言 | Python | C# / WPF |
| 许可证 | 无 | GPL-3.0 |
| 功能 | 两台具体设备的读取 | 多协议 + 三层历史 + 设备表 |

⚠️ **点「Sync fork」会把上游的 Python 文件合并进来**，且引入许可证冲突
（无许可证代码不能并入 GPL-3.0 项目）。要避免。

同理，`git pull upstream` 也不要用 —— 本地 upstream remote 的推送已被
禁用（`--PUSH-DISABLED`），但拉取仍会带入上游内容。

## 9. 当前状态与已知限制

### 已实现

- 罗技 HID++ 2.0 双集合读取（两遍槽位探测，空槽位 6ms 排除）
- 维咖 MAX102 双通道（2.4G VIA `cmd 20` / 蓝牙系统属性），2.4G 优先
- 雷蛇耳机双通道（2.4G 64 字节 PA/PI / 蓝牙系统属性），2.4G 优先
- 蓝牙电量统一读法：`BTHLE` + `BTHENUM` 两棵树，电量按名字、在线按 MAC 聚合
- 亚克力卡片（DWM 毛玻璃 + 圆角，系统不支持时自动回退不透明）
- 24 小时柱状图 + 续航预测（斜率法，与 logi-tray 对齐）
- 电量跳变过滤（> 3 点/分钟即丢弃，挡住唤醒瞬间的假读数）
- 设置面板：间隔 / 卡片宽高 / 图表高度 / 配色 / 阈值 / 开关
- 三重自启动对齐 + `WM_TASKBARCREATED` 托盘自愈
- 51 项脱机测试
- `--probe --trace` 原始报文抓取（排查读不到的唯一手段）

### 已知限制

| 限制 | 说明 |
|---|---|
| 逐型号逆向 | 换设备必须重新抓包验证，代码里没有通用自动发现 |
| 续航预测需要 30 分钟样本 | 刚装好会显示「放电数据积累中」 |
| 刷新在 UI 线程 | 一次读取约 200ms，设备多了会卡顿 |
| 只测过 3 款设备 | 罗技 PRO X 2、雷蛇 BlackShark V2 Pro（2.4G + 蓝牙）、维咖 MAX102（2.4G + 蓝牙） |
| 蓝牙通道拿不到充电状态 | 系统属性 `{104EA319-…} 3` 实测恒缺失，界面只能写「充电状态未知」 |
| 蓝牙通道按名字前缀认设备 | 耳机 `Razer*`、键盘 `MAX102*`。换品牌或改名就可能漏（为什么不按 MAC：MAC 随重配对变） |
| 罗技设备名依赖 `0x0005` 特性 | 个别老机型不支持时显示成「鼠标」 |
| 有线模式下读不到无线那端 | 物理限制：鼠标插线后不再通过 2.4G 上报，接收器槽位全部不可达。程序会如实读直连端点 |
| 直连与接收器同时在线时只取一个 | `GroupReceivers` 接收器优先、命中即返回。不做两者合并，避免两个来源的电量打架 |

### 没做但可能想做的

- 更多设备支持（罗技其他型号多半通用，雷蛇需要按 PID 加命令表）
- 历史数据导出 CSV
- 托盘图标直接显示数字（Windows 托盘只有 16×16，两个数字放不下）
- 自动更新

---

## 10. 交接检查清单

接手第一天，按这个顺序过一遍：

- [ ] `dotnet --list-sdks` 有 8.0.x
- [ ] `build.bat` 跑通，看到「51 项，通过 51，失败 0」
- [ ] `--probe` 能读到自己的设备（若设备不同见第 6 节）
- [ ] `--probe --trace` 能看到原始报文（这是你以后排查的唯一手段）
- [ ] 起托盘，左键点图标能弹出卡片
- [ ] 点齿轮能打开设置面板
- [ ] 改 `UI/Palette.cs` 里一个颜色，重新 `build.bat`，看到界面变了
- [ ] 读一遍 `AGENTS.md`（12 条维护铁律）
- [ ] 读一遍 `ARCHITECTURE.md`（关键设计决策的理由）
- [ ] 看一眼 `archive/python-prototype/` 知道还有个 Python 旧版
      （仅作协议基准保留，不参与构建）

---

## 11. 参考项目

这几个仓库是协议的来源，改协议前值得对照：

| 仓库 | 用途 |
|---|---|
| [yixing233/logi-tray](https://github.com/yixing233/logi-tray) | 罗技 HID++ 2.0 的槽位探测策略、各电量特性字节布局、24h 分桶与续航预测、这套卡片的视觉规格 |
| [Ferris-echo/FerrisPulse](https://github.com/Ferris-echo/FerrisPulse) | 雷蛇 64 字节 PA/PI 报文格式（`src/Hardware/RazerHeadsetHelper.cs`），以及本项目沿用的工程规范 |
| [marsuyone/mchose-battery-tray](https://github.com/marsuyone/mchose-battery-tray) | 本项目的基础（单图标多圆环、主题跟随、单实例） |
| [Banxiaxiala/weikav-max102-re](https://github.com/Banxiaxiala/weikav-max102-re) | 本项目作者自己的维咖 MAX102 固件逆向仓库：电量通路反汇编、`cmd 20` 补丁固件、指示灯位图、`DEVPKEY_Bluetooth_Battery` 读法 |

---

## 12. 联系与署名

协议逆向与实现参考了上面三个开源项目，请在修改时保留原仓库链接与署名。
本工具与罗技、雷蛇官方无关，不保证适配其他型号或固件版本。
