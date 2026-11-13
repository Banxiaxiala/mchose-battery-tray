# 架构说明

> 本文件按 AGENTS.md 第 12 条要求与代码同步维护。**任何新增模块、
> 公共方法调整或协议变更都必须同步更新本文件。**

当前技术栈：**C# 12 + WPF，`net8.0-windows`**。
`archive/python-prototype/` 是早期 Python 实现，仅作协议与算法基准保留。

## 分层

```
build.bat                       一键构建：定位 SDK → 杀进程 → 测试 → 编译 → 发布
src/                         生产代码
  PeripheralBattery.csproj      net8.0-windows + UseWPF + UseWindowsForms
  app.manifest                  PerMonitorV2 DPI、asInvoker、UTF-8 codepage
  GlobalUsings.cs               消除 WPF 与 WinForms 的 Color/Point/Brush 冲突
  Program.cs                    装配层：设备表 + 把各层接起来
  Models/Models.cs              纯数据：Reading / Sample / Bucket / Forecast / Analysis
  Core/
    Win32.cs                    全项目唯一的 P/Invoke 声明入口
    Hid.cs                      HID 枚举、句柄、常驻挂起读（PendingRead）
    BluetoothBattery.cs         BTHLE + BTHENUM 节点电量属性（SetupAPI，不连设备）
  Hardware/
    Protocols.cs                三套协议的纯解析/构造（零 I/O）
    Readers.cs                  I/O 编排：罗技两遍槽位探测、雷蛇 64B 收发 → 蓝牙回落
    Max102Reader.cs             VIA 族键盘（维咖/高斯）：2.4G 优先 → 蓝牙回落
    VendorProtocols.cs          九家厂商私有协议（纯解析，零 I/O）
    VendorReader.cs             三家私有协议的通用 I/O 骨架
  Services/
    History.cs                  样本存储、三层聚合（48h/30d/24m）、续航预测
    Settings.cs                 用户设置（间隔/尺寸/开关）持久化
    AutoStart.cs                三重自启动对齐
  UI/
    Palette.cs                  配色唯一来源 + 主题读取
    Icons.cs                    矢量图形（电池/闪电/刷新/齿轮/关闭）
    SparkChart.cs               24 小时柱状图（自绘，非 24 个 Border）
    LineChart.cs                趋势折线图（按真实时间轴，空段断线）
    Acrylic.cs                  DWM 毛玻璃 + 圆角，两级回退
    TrayIconArt.cs              三圈同心环的 GDI+ 绘制（纯绘制，可脱机预览）
    TrayIcon.cs                 NotifyIcon 封装 + HICON 生成 + 位置查询
    BatteryCard.cs              亚克力卡片主窗口
    SettingsPanel.cs            设置面板
    Win32Cursor.cs              光标位置薄封装
  Tools/CardPreview.cs          --preview：合成数据渲染卡片并截图
tests/                          106 项脱机断言（零硬件、零第三方框架）
docs/                           本文件、README、HANDOVER、AGENTS
assets/preview.png              README 用图
app.ico / icon.ico              应用图标（根目录，csproj 用 ..\app.ico 引用）
```

依赖方向严格单向，下层不许 import 上层：

```
Program → UI → Services → Hardware → Core → Models
```

`Models` 不依赖任何其它层；`Hardware` 不许 import `UI`。

## 关键设计决策

### 1. 为什么从 Python 换成 C# + WPF

Python + tkinter 做不出 logi-tray 那种亚克力效果，而且：
- 单文件 exe **12 MB**（C# 版 **378 KB**）
- 需要 hidapi / Pillow / pystray 三个第三方依赖
- tkinter 的圆角/毛玻璃要手写 DWM 互操作，且跨线程建 Tk 会让
  `geometry` 静默失效（窗口落到屏幕左上角）

C# + WPF 天然有 `WindowChrome` 圆角、`SetWindowCompositionAttribute` 亚克力、
`Path` 矢量图形、动画缓动函数，全是框架自带。

### 2. 为什么自建托盘而不是纯 `NotifyIcon`

`NotifyIcon` 不暴露窗口句柄，拿不到 `Shell_NotifyIconGetRect` 的结果，
就无法知道图标此刻的屏幕位置 —— 卡片只能猜坐标。
现在通过反射取到内部 `NativeWindow` 句柄，用它：
- 查图标真实矩形（`IconCenter()`）
- 挂 `TaskbarCreated` 钩子实现 explorer 重启自愈

### 3. 关闭时机：两条规则并用

`ShowActivated=false` 让卡片拿不到焦点，等 `Deactivated` 永远不触发。
所以 `CheckDismiss()` 每 60ms 检查两件事：

| 规则 | 作用 |
|---|---|
| 前台窗口不是本进程 | 点浏览器/游戏/资源管理器时收起 |
| 鼠标在本进程窗口之外按下 | 点任务栏、点桌面空白处时收起 |

再加两条豁免：
- 弹出后前 3 帧不判定（那一刻窗口还在定位，光标必然在「外面」）
- 设置面板打开期间，面板范围算作「卡片自己的一部分」

只靠「鼠标按下」一条会漏：点别的窗口时焦点瞬间转移，
光标轮询往往来不及命中按键那一帧，卡片就赖着不走 —— 这是用户报的
「点其他窗口不关闭」的根因。

### 4. HID 读写的三个必踩坑

1. **`CreateFileW` 必须带 `FILE_FLAG_OVERLAPPED`**
   否则后续即使传了 `OVERLAPPED` 结构，`ReadFile` 也会退化为同步阻塞，
   带超时的读写全部失效 —— `PendingRead` 会永久挂死。
2. **`SP_DEVICE_INTERFACE_DETAIL_DATA_W` 的字符串从第 4 字节开始**
   不分 x86/x64，`cbSize` 都是 `DWORD(4)`。按 x64 写成偏移 8 会吃掉路径前
   4 个字符，得到 `"?\hid#..."`，`CreateFile` 报错误 123。
3. **罗技接收器必须用常驻挂起读**
   目标槽位不可达时，接收器只在短集合（7 字节，`usage 0x0001`）上回一条
   3~5ms 的错误帧。若每次 poll 都 `CancelIoEx` 再重建 `ReadFile`，取消与
   重建之间的空窗恰好会吃掉这条帧，空槽位便只能等满超时。

### 5. 罗技槽位探测：轻探 + 耐心等

- **第一遍**（`QuickTimeout = 0.40s`）：靠短集合错误帧毫秒级排除空槽位。
  实测 7 个槽位扫完仅 74ms。
- **第二遍**（`PatientTimeout = 1.30s`）：只对「什么都没回」的槽位重试。
  设备从休眠唤醒时首帧可能慢到 850ms，且第一次请求可能直接失败。

### 6. 历史与预测

对齐 `yixing233/logi-tray` 的 `BatteryService`（两个参考项目里唯一有这块
功能的实现，FerrisPulse 没有）：

- 样本 `{t, percent, charging}`，保留 48 小时，**永远保留最新一条**
- 落盘节流 60 秒；电量没变且 20 秒内刚记过则不重复记
- 24 小时按小时分桶，无样本的时段沿用上一个已知值并标为休眠柱
- 预测：24h 内放电样本的首尾斜率 `rate = Δp / Δt`，
  剩余时间 `= 当前电量 / rate`；跨度 ≥ 0.5h 才算，> 4h 标「高」
- **跳变过滤**：> 3 个百分点/分钟即丢弃。无线设备唤醒瞬间会报假读数
  （实测 51% → 59% → 51%，间隔 700ms），写进历史会把斜率算歪。
  阈值取 3.0 的依据：长时间平均 0.1~0.3，高强度使用峰值约 1.5~2.5。

**当前读数与历史值严格分开**：读不到时 `Reading.Percent` 保持 `null`，
历史值只进 `Analysis.LastKnownPercent`，显示成「上次 77% · 2 小时前」。

### 7. 可配置项的两条底线

`Services/Settings.cs` 允许用户改间隔、尺寸、配色、阈值。两条底线：

| 底线 | 原因 |
|---|---|
| 解析失败必须回退默认 | `settings.json` 是给用户手改的，写错一个字符很常见。颜色写成 `#GGGGGG` 时界面不能变黑 |
| 阈值必须严格递增 | `critical=40, low=20, mid=10` 会让「偏低」「一般」两个档位永远取不到，配色看起来像失灵。`Sanitize()` 强制拉成递增 |

`card_height = 0` 表示「随内容自动」。固定高度时 `ApplyMetrics` 会把内容包进
`ScrollViewer`，否则第二台设备会被直接裁掉、且无法触及。

### 8. 线程模型

```
主线程（UI）      WPF Application.Run() + DispatcherTimer（卡片/面板轮询）
System.Timers    System.Windows.Forms.Timer 每 N 秒读一次两台设备
```

刷新在 UI 线程上执行（HID 读取一次约 130ms + 55ms + 30ms，可接受）。
如果将来要支持更多设备，再把读取移到 `Task.Run` + `Dispatcher.Invoke`。

### 9. 设备表驱动（而不是每台设备一组字段）

v3 里「鼠标 / 耳机」是两组平行字段：`_mouse`/`_headset`、
`_mouseAnalysis`/`_headsetAnalysis`、`_mouseError`/`_headsetError`，
`SetReadings(mouse, headset)`、`BuildTooltip` 里两行、`Probe` 里两段……
加第三台设备要同步改 **11 处**，漏一处就静默不显示。

现在统一成 `Program.BuildDevices()` 返回的 `List<DeviceSlot>`：

```csharp
new("鼠标", "logitech", 0, new LogitechReader().Read),
new("耳机", "razer",     1, new RazerReader().Read),
new("键盘", "max102",    2, new Max102Reader().Read),
```

`DeviceSlot` 自己持有 `Latest / Analysis / Error / History`，
托盘按 `Ring` 槽位（0 外 → 2 内）拿到 `Reading?[]` 再画圈。

**Ring 槽位必须稳定**：不能因为某台设备离线就把后面的设备前移补位，
否则用户会把键盘看成耳机。离线时那圈只画轨道、不画进度，圈数不变。

### 10. 「没读到」与「离线」必须分开（摇摇欲坠的 2.4G 空口）

用户报的实际症状：**「耳机有时候会显示掉线，实际上没掉线」**。

根因是三层叠加，任何一层单独存在都会让界面说谎：

| 层 | 旧行为 | 后果 |
|---|---|---|
| 读取层 | 雷蛇 `Query` 只发一次 | 丢一帧 → 整轮 `Read()` 返回 `null` |
| 状态层 | `connection != 0` 判在线 | `-1`（没问到）与 `0`（真休眠）被混为一谈 |
| 展示层 | `reading == null` 立刻 `Connected = false` | 错一次就错满整个刷新周期（默认 30 秒） |

三处现在都改掉了：

1. `RazerReader.QueryWithRetry` 重试 2 次（对齐 FerrisPulse 的
   `RazerHeadsetHelper.QueryValue`）；罗技本来就有两遍槽位探测。
2. `Protocols.InterpretRazerState(charging, connection)` 把三态映射做成
   纯函数 —— `connection == 0` → 已休眠（离线）、`== 1` → 在线、
   `== -1` → 在线但标「状态未确认」。**`-1` 不许当 0 用**。
3. `Models.ReadingDebounce`：连续失败 3 次才判离线。阈值取 3 是因为
   单帧丢失偶发但连续三帧都丢概率极低，而真休眠/关机是每帧都不应答。
   期间保留上一次的百分比与在线标记，界面不再闪跳。

`Read()` 返回 `null` 语义是「这一轮没拿到读数」，**不代表设备离线**；
离线判定只由 `ReadingDebounce` 决定。

### 11. 维咖 MAX102：一条设备、两条通道

键盘有三种形态，电量来源完全不同，所以 `Max102Reader.Read()` 内部做降级：

| 形态 | 判定 | 电量来源 |
|---|---|---|
| 2.4G dongle | 枚举到 `342D:E537` 且 `0xFF60/0x61` | VIA `cmd 20` |
| USB 有线 | 同上，但 `cmd 20` 回 `14 … state=0` | **拿不到** |
| 蓝牙 | 没有 `0xFF60/0x61` | `DEVPKEY_Bluetooth_Battery` |

**2.4G 优先**，因为它能同时拿到连接状态和指示灯位图，信息量更大；
且 dongle 在位时蓝牙通道通常不活跃。

三条不能改的判据：

1. **`cmd 20` 回 `0xFF` = 原厂固件（没刷电量补丁），不是 0%。**
   判成 0% 会让用户看到键盘永远没电并反复收到低电量提醒。
2. **`state == 0` = USB 有线，`md_info` 全零 = 拿不到电量，不是 0%。**
   补丁生效与不生效在这种模式下响应恰好相同，所以必须先看回显再分档。
3. **`cmd 6` 是「恢复出厂默认键位」，任何读取逻辑都不许碰。**

**充电状态永远未知**：主控固件里没有充电检测（X14076 的 `CHRG`/`STDBY`
直接驱动 LED，`0xA6 0x64/0x65/0x66` 那三条充电命令定义了但从没发过）。
所以键盘永不显示「充电中」。

蓝牙走系统属性而不走 GATT，三个理由：Windows 已经连着了（毫秒级、不用再连）、
走 GATT 要 WinRT 的 BLE 栈（额外依赖）、已配对的 BLE HID 被系统独占
（抢连接会影响键盘输入）。

蓝牙通道对**耳机和键盘一视同仁**：都是「先厂商 HID 私有通道，失败再回落蓝牙」。
耳机蓝牙下没有 `usagePage 0xFF00` 接口，键盘蓝牙下没有 `0xFF60/0x61` ——
两者都只能借系统属性，所以两条 Reader 共用 `Core/BluetoothBattery.cs`
与 `Protocols.InterpretBluetoothState`，避免两处文案/判定漂移。

⚠️ 系统属性**在设备离线时仍保留上次的值**。`Program.RefreshNow` 用
`reading.Connected` 门住 `History.Add`，否则陈旧读数会在历史里
伪造出一条水平曲线，把续航预测的斜率算成 0。

## 同类项目调研结论

加型号之前先查过一轮公开实现，结论直接决定了架构选择：

| 项目 | 提供了什么 | 我们用了吗 |
|---|---|---|
| [openrazer](https://github.com/openrazer/openrazer) | 雷蛇 PID 清单（最全） | ✅ 型号表 40+ 条来自这里 |
| [FerrisPulse](https://github.com/Ferris-echo/FerrisPulse) | 多品牌实测型号 + 协议实现 | ✅ 雷蛇/罗技 PID 交叉验证 |
| [logi-tray](https://github.com/yixing233/logi-tray) | 罗技 HID++ 两遍槽位探测 | ✅ 读取策略参考 |
| LogitechBatteryMonitor / PowerTray 等 | 单品实现 | ❌ 都是单设备，无新信息 |

**关键结论：型号表只影响显示文案，不影响能不能读到电量。**

雷蛇各代鼠标/耳机的电量都走同一条 PA/PI 命令（`0x80` 帧头 + `0x21` 电量），
openrazer 里就是同一个 `get_battery` 能力。所以本程序不需要为每个型号
写适配器 —— 需要的是一个能覆盖整个协议族的实现 + 一张可查的名字表。

这直接导致 `DeviceCatalog` 的设计：

- **协议按「族」实现**，不按型号分支（`ProtocolKind` 只有 4 个值）
- **型号表只是查名字**，查不到就 `product_string`，再查不到就「Razer 无线设备」
- 因此型号表可以「宁缺勿错」：不收不确定的型号名，也不会因漏收而读不到

同类项目里没有一个做「多层历史聚合」，也都只盯一个品牌 ——
这是本项目把「三层聚合 + 跨品牌 + 证据分级」当核心的原因。

## 协议速查

| | 罗技 HID++ | 雷蛇 PA/PI | 维咖 VIA |
|---|---|---|---|
| VID:PID（本机实测） | `046D:C54D` | `1532:0555` | `342D:E537` |
| 接口 | 短 `0xFF00/0x0001` 7B + 长 `0xFF00/0x0002` 20B | `0xFF00`，64B OUT/IN，feature=0 | `0xFF60/0x61`，33B |
| 请求 | `11 <slot> <featIdx> <(fn<<4)\|0x0A> <params>` | `02 80 08 00 00 50 41 08 00 03 <CMD> …` | `00 14 00 …` |
| 应答 | `11 <slot> <featIdx> <fnByte> <data…>` | `02 <len> 50 49 <seq> … <CMD> 01 01 <value>` | `14 00 00 00 <bat> <state> <ver> <ind>` |
| 电量 | 特性 `0x1004` → `0x1000` → `0x1001` 回退 | 命令 `0x21`，`reply[15]` | `reply[4]`（`cmd 20`，补丁命令） |
| 充电 | `status[2]` 状态码 | 命令 `0x2A` | **无**（固件没有检测） |
| 在线 | 短集合错误帧 `10 <slot> 8F …` | 命令 `0x20` | `reply[5]` == 2，或 dongle 能应答 |

### 蓝牙通道（耳机 + 键盘共用）

两棵树都要枚举 —— 缺一棵就有一类设备永远读不到：

| 设备 | 枚举器 | 电量节点 |
|---|---|---|
| 耳机 BlackShark V2 Pro BT（经典蓝牙） | `BTHENUM` | `{0000111E-…}` Hands-Free AG 服务子节点 |
| 键盘 MAX102-BT1（低功耗） | `BTHLE` | `DEV_<MAC>` 父节点 |

| 属性 | 含义 |
|---|---|
| `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` | Byte 电量 0–100 |
| `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 7` | FILETIME 时间戳 |
| `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 3` | 充电中布尔；**实测恒缺失** |
| `{83DA6326-97A6-4088-9453-A1923F573B29} 15` | IsConnected |

⚠️ **电量与在线状态不在同一节点上。** 蓝牙耳机的电量挂在音频服务
子节点，而那个子节点的 `IsConnected` 恒为 `False`；真正表示链路在线
的是同 MAC 的父节点 `BTHENUM\DEV_<MAC>`，它**没有**电量。所以
`BluetoothBattery` 先按 MAC 聚合在线状态，再挂到带电量结果的节点上。
拿子节点的 `IsConnected` 当结论，会在耳机正放着歌时显示「蓝牙离线」。

⚠️ `BTHENUM` 树下还有蓝牙串口 / 鼠标 / 传真等无关节点，必须按
`{0000xxxx-…}` 的音频服务 UUID 先过滤（`Protocols.IsBluetoothAudioInstance`）。

详细字节布局见 `Hardware/Protocols.cs` 的注释与 `tests/ProtocolTests.cs`。

## API 成本与性能

| 指标 | 数值 |
|---|---|
| 单文件 exe | 457 KB（含分层历史与三个厂商协议） |
| 罗技读取 | ~130ms（含两遍探测） |
| 雷蛇 2.4G 读取 | ~55ms |
| 雷蛇蓝牙读取 | ~8ms（纯 PnP 属性查询，两棵树） |
| 维咖 VIA 读取 | ~30ms |
| 维咖蓝牙读取 | ~5ms（纯 PnP 属性查询） |
| 空槽位排除 | ~6ms/槽位 |
| 图标渲染 | ~4ms（每 N 秒一次） |
| 卡片首帧 | ~120ms（含亚克力应用） |
