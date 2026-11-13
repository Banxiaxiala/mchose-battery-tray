# 兼容性矩阵

> **这份表格的可信度分级是本项目最看重的东西。**
> 我们不把「协议理论上支持」写成「已验证」—— 那会让用户买回来发现读不到。
>
> 想把某台设备从 🟡 升到 🟢，只需要按[贡献指南](#如何把型号升级成实测)提交
> 一次 `--probe --trace` 的输出。

## 图例

| 等级 | 含义 | 你该期待什么 |
|:---:|---|---|
| 🟢 **实测** | 作者本人每天在用，型号、电量、充电状态全部验证过 | 直接可用 |
| 🟡 **协议族兼容** | 用同一条协议、同一套报文格式；协议层有单元测试，但**没在真机上验证过这个具体型号** | 大概率可用；读数不对请报 issue |
| 🔵 **仅蓝牙** | 只有蓝牙通道（Windows 系统电量属性），拿不到充电状态 | 能看电量；分辨率取决于系统 |
| ⚪ **未测试** | 型号存在、协议对得上，但完全没有证据 | 请先跑 `--probe --trace` 再反馈 |

**蓝牙通道对所有品牌的设备都通用** —— 它读的是 Windows 自己维护的
`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` 属性，不区分厂商。所以任意蓝牙
外设接上基本都能看到电量（见文末「通用蓝牙」一节）。

---

## 罗技 LIGHTSPEED（HID++ 2.0）

协议入口：`usagePage 0xFF00`，短集合 `usage 0x0001`（7B）+ 长集合 `usage 0x0002`（20B）。
读取特性链：`0x1004` UNIFIED_BATTERY → `0x1000` BATTERY_STATUS → `0x1001` BATTERY_VOLTAGE。

**这条链是罗技官方规范，凡支持 HID++ 2.0 的设备理论上都能读。**
真正的差异在于个别老设备的特性编号或固件行为。

| 型号 | VID:PID | 等级 | 备注 |
|---|---|:---:|---|
| PRO X 2 (G PRO X SUPERLIGHT 2) | `046D:C54D` | 🟢 | 本项目作者主力设备 |
| G304 / G102 Lightspeed | `046D:C53F` | 🟢 | 作者备用鼠标，走 `0x1000` 分支 |
| G502 X PLUS | `046D:C095` | 🟡 | HID++ 2.0，走 `0x1004` |
| G502 LIGHTSPEED | `046D:C539` | 🟡 | 与 `C53F` 可能同时出现（双接收器） |
| G PRO X Wireless 耳机 | `046D:0ABA` | 🟡 | HID++ 4.2，走 `0x1F20` ADC 曲线 |
| G703 / G903 / G604 / G603 | `046D:C539` 等 | 🟡 | 同族接收器 |
| G913 / G915 / G515 TKL | — | 🟡 | 键盘，同 HID++ 电量特性 |
| GPW 1 / 2 / 3、PRO 2 | — | ⚪ | 未验证；有报告 `0x0005` 名称需分段读 |
| MX Master 3 / 3S、MX Anywhere 3 | — | ⚪ | 办公线，可能只报电压（`0x1001`）不报百分比 |

> ⚠️ 罗技接收器同时插多个时，程序按「接收器优先、命中即返回」处理，
> 只显示一台设备。这是刻意的：两个来源的电量会打架。

## 雷蛇（PA/PI 64 字节中断报文）

协议入口：`usagePage 0xFF00`，64 字节 OUT/IN，`FeatureReportByteLength = 0`。
命令：`0x21` 电量、`0x2A` 充电、`0x20` 连接状态、`0x00` 序列号。

| 型号 | VID:PID | 等级 | 备注 |
|---|---|:---:|---|
| **BlackShark V2 Pro**（旋风黑鲨 2.4G, 2023） | `1532:0555` | 🟢 | 作者主力，2.4G + 蓝牙均实测 |
| **Kraken V4**（北海巨妖 V4） | `1532:056C` | 🟡 | PID 已入表；PA/PI 同族 |
| DeathAdder V4 Pro / Carbon | `1532:00BE` / `00EF` | 🟡 | |
| DeathAdder V3 Pro / HyperSpeed | `1532:00B6` / `00C4` | 🟡 | |
| DeathAdder V2 Pro / V2 X HyperSpeed | `1532:007C` / `009C` | 🟡 | |
| Viper V4 Pro / V3 Pro / V3 Pro SE | `1532:00E5` / `00C0` / `00DE` | 🟡 | |
| Viper V3 HyperSpeed / V2 Pro / Mini SE | `1532:00B8` / `00A5` / `009E` | 🟡 | |
| Viper Ultimate | `1532:007A` | 🟡 | |
| Basilisk V3 Pro / 35K / Phantom Green | `1532:00AA` / `00CC` / `00D6` | 🟡 | |
| Basilisk V3 X HyperSpeed / X HyperSpeed | `1532:00B9` / `0083` | 🟡 | |
| Basilisk Ultimate / Mobile | `1532:0086` / `00D3` | 🟡 | |
| Naga V2 Pro / V2 HyperSpeed / Pro / Epic | `1532:00A7` / `00B4` / `008F` / `001F` | 🟡 | |
| Naga Epic Chroma | `1532:003E` | 🟡 | |
| Mamba / Mamba Chroma / Mamba 2012 | `1532:0073` / `0045` / `0025` | 🟡 | |
| Lancehead / Wireless | `1532:0059` / `0070` | 🟡 | |
| Cobra Pro / HyperSpeed | `1532:00AF` / `00DA` | 🟡 | |
| Orochi V2 / Ouroboros | `1532:0094` / `0032` | 🟡 | |
| Atheris | `1532:0062` | 🟡 | |
| Pro Click / Mini / V2 / V2 Vertical | `1532:0077` / `009A` / `00D0` / `00C7` | 🟡 | |
| HyperPolling Wireless Dongle | `1532:00B3` | 🟡 | 8K 接收器；会转发设备的电量 |
| BlackWidow V3 Pro / Mini | `1532:025A` / `025C` / `0258` | 🟡 | 无线键盘 |
| BlackWidow V4 Mini / TKL | `1532:02B9` / `02D7` | 🟡 | 无线键盘 |
| DeathStalker V2 Pro / TKL | `1532:0292` / `0298` | 🟡 | 无线键盘 |
| Joro | `1532:02CD` | 🟡 | 无线键盘 |
| Kraken 7.1 / Chroma / V2 | `1532:0501` / `0504` / `0510` | 🟡 | 老款雷蛇耳机 |
| Kraken TE / Ultimate | `1532:0520` / `0527` | 🟡 | 雷蛇耳机 |
| Kraken Kitty / Kitty V2 | `1532:0F19` / `0560` | 🟡 | 猫耳耳机 |

共 60 个 PID 已入型号表（`Protocols.RazerModelNames`），覆盖鼠标 / 键盘 / 耳机三类，
来源是 [OpenRazer](https://github.com/openrazer/openrazer) 的
`daemon/openrazer_daemon/hardware/{mouse,headsets}.py` ——
它维护的雷蛇 PID 清单是目前公开最全的。

> **型号表只决定显示的名字，不决定能不能读。**
> 表里没有的 PID 会先读 `product_string`，读不到就显示「Razer 无线设备」，
> 电量照读不误。所以漏型号 ≠ 读不到，这也是这张表可以「宁缺勿错」的前提。

> ⚠️ **雷蛇的鼠标与耳机共用 PA/PI 报文，但 `0x21` 电量命令的语义在部分固件上
> 有差异。** 鼠标读不到时通常回 `0xFF`，本程序会如实报「电量查询无应答」
> 而不是显示 0%。遇到这种情况请附 `--trace` 日志提 issue。
>
> ⚠️ 雷蛇设备在蓝牙模式下**没有** `usagePage 0xFF00` 接口，会自动走蓝牙通道。

## VIA 族键盘（维咖 / 高斯 / 其它 QMK）

协议入口：`usagePage 0xFF60 / usage 0x61`，33 字节（1B report id + 32B 载荷）。

**这个族有两个已知布局**，差别只有命令号与电量偏移：

| 布局 | 命令 | 电量偏移 | 代表型号 | 性质 |
|---|:---:|:---:|---|---|
| A | `0x14` (cmd 20) | `[4]` | 维咖 MAX102 | **补丁命令**，原厂固件回 `0xFF` |
| B | `0x20` | `[3]` | 高斯 GS3108T-PRO | 原厂命令（FerrisPulse 静态分析 + 抓包） |

| 型号 | VID:PID | 等级 | 备注 |
|---|---|:---:|---|
| MAX102（2.4G / 蓝牙） | `342D:E537` | 🟢 | 布局 A；补丁固件见 [weikav-max102-re](https://github.com/Banxiaxiala/weikav-max102-re) |
| MAX102 原厂固件 | `342D:E537` | 🔵 | `cmd 20` 回 `0xFF`，自动回落蓝牙 |
| GS3108T-PRO（高斯） | `05AC:024F` | 🟡 | 布局 B；默认**不启用**，装了就在设置里打开 |
| 其它 QMK/VIA 键盘 | — | ⚪ | 需已知自己的电量命令；布局 A/B 都不通就只能走蓝牙 |

> ⚠️ 高斯的 VID 是 `05AC`（Apple 的厂商号），不是高斯自己的厂商号 ——
> 这是键盘固件自报的值，抓包时就是这么枚举出来的。**不要「纠正」它**，
> 改了反而枚举不到。

> ⚠️ VIA 读取要**真正写 HID 报文**，需要 `GENERIC_READ|WRITE` 权限。
> 键盘类 HID 集合有防监听独占锁，打开方式与鼠标不同
> （见 `docs/ARCHITECTURE.md` 第 8 节）。

> ⚠️ 两个布局的判定逻辑**必须完全一致**：`0xFF` = 没补丁、`state 0` = USB 有线、
> `percent > 100` = 不可信。那些判据是踩过坑得出的，不能因为换型号而放松，
> 测试里对两个变体分别做了断言。

## 移植实现的其他品牌（🟡 未在本仓库验证）

下面这些协议**移植自公开参考实现**，代码已经能吃进来并跑了断言，
但作者手上没有这些设备，**没有真机验证过**。全部默认关闭 ——
装了的用户在设置面板里打开。

| 品牌 | 型号 | VID | 协议 | 端点 / 帧 | 出处 |
|---|---|---|---|---|---|
| 高斯 | GS3108T-PRO | `05AC:024F` | VIA 布局 B | `0xFF60/0x61` 33B，cmd `0x20`，电量 `[3]` | FerrisPulse `GanssDeviceHelper` |
| ATK / 怒喵 | V75X | `320F:*` | ATK 私有 | `0xFF1C/0x92` 64B，cmd `0x0011`，电量 `[8]` 充电 `[9]` | FerrisPulse `AtkDeviceHelper` |
| 飞智 | GPA6 | `37D7:*` | Flydigi 私有 | `0xFFA0`，帧头 `5A A5`，cmd `0x01` | FerrisPulse `FlydigiDeviceHelper` |
| 漫步者 | 花再 Halo Space | `2D99:*` | Edifier EC | 64B，`2E AA EC`，XOR `0xA5` + 累加和 | FerrisPulse `EdifierDeviceHelper` |

### 移植过程中发现的坑

这些坑在原项目里不存在（它们用 hidapi），但**移植到 Win32 后必然踩到**：

**1. report id 会让帧头判定全错**

hidapi 的 `read()` 不返回 report id，Win32 的 `ReadFile` 会把它放在第 0 字节。
所以漫步者的应答应为 `00 2F BB EC …`（report id + 应答头），
而不是参考代码假设的 `2F BB EC …`。照搬原判定会让真实设备的应答全部解析失败。

**2. 飞智的电量只有 4 位**

`raw = report[payload+6] & 0x0F`，所以数值上限是 15。
参考实现里「`raw > 10` 直接当百分比」这一档实际只覆盖 11..15 ——
移植时若理解成「能读到 87%」就会算错。

**3. 漫步者只有载荷参与 XOR**

长度字段、校验和、填充字节都是明文。把整帧 XOR 会让长度算出来是天文数字。

**4. 漫步者缓冲区尾部有残留报文**

设备 64 字节缓冲会留着上一条报文，必须按长度字段截取，
不能整帧当载荷解析。

以上四条都有对应断言锁住（`tests/VendorProtocolTests.cs`）。

## 通用蓝牙（所有品牌）

**任何** 已配对且在 Windows「设置 → 蓝牙」里显示电量的外设，
本程序都能读到 —— 因为读的是系统属性，不走厂商协议。

| 类型 | 等级 | 说明 |
|---|:---:|---|
| 蓝牙耳机 / 音箱 | 🔵 | 电量挂在 `BTHENUM` 的音频服务子节点 |
| 蓝牙键鼠（BLE HID） | 🔵 | 电量挂在 `BTHLE` 的 `DEV_<MAC>` 节点 |
| 索尼 INZONE / PULSE 系列 | 🔵 | 系统能读到就能显示；拿不到左右耳分离电量 |
| AirPods | 🔵 | 同上；系统只报一个合并值 |
| Redmi Buds 等 TWS | 🔵 | 实测系统会报电量（作者机器上已出现） |

限制（蓝牙通道固有的，改不了）：

- **没有充电状态** —— 属性 `{104EA319-…} 3` 在实测设备上恒缺失
- **电量是系统缓存的** —— 设备离线后属性仍保留上次值，程序靠 `IsConnected` 区分
- **按名字前缀匹配** —— 所以自定义型号要填 `namePrefix`

## 添加不在表里的型号

不需要改代码。在 `data\devices.json` 里加一条即可（详见 [README](../README.md#添加设备型号)）：

```json
[
  {
    "key": "my-mouse",
    "label": "鼠标",
    "model": "G502 X PLUS",
    "channels": [{ "kind": "logitech", "vendorId": 1133 }]
  }
]
```

## 如何把型号升级成实测

请按顺序做，缺一步我们无法判断问题出在协议还是环境：

1. **确认通道被枚举到**

   ```bat
   外设电量.exe --probe --trace
   ```

   日志会列出所有 HID 接口的 `VID:PID up=… u=… io=…/…`。把你的型号那一行留下来。

2. **贴出对应设备段的输出**

   `--probe` 会按设备分节打印「协议 / 电量 / 状态 / 在线」。如果失败，
   会写明失败原因（「接收器上没有设备应答」还是「固件没有电量补丁」——
   这两者的排查方向完全不同）。

3. **提 issue**

   带上：型号 + VID:PID + 上面两段输出 + Windows 版本。
   提 issue 时请**不要**贴完整日志里的无关设备，只保留相关段落即可。

<details>
<summary>为什么我们把「未验证」和「已实测」分得这么清楚</summary>

这个项目最初的 bug 全部来自「看起来应该能用」的假设：

- 以为蓝牙电量在 `BTHLE` 树，实际耳机在 `BTHENUM`（读不到）
- 以为电量节点的 `IsConnected` 可信，实际它恒为 `False`（显示「离线」但设备在用）
- 以为 `cmd 20` 是原厂命令，实际是补丁命令（没刷固件的用户看到 0%）

每一次都是「协议查清楚了，但设备行为不一样」。所以表格里宁可多写几行
⚪ 未测试，也不把 🟡 写成 🟢。

</details>

## 数据来源

表格里的 VID/PID 来自这些公开仓库，不是猜的：

- [openrazer/openrazer](https://github.com/openrazer/openrazer) ——
  雷蛇 PID 清单（`daemon/openrazer_daemon/hardware/`），公开最全的一份；
  本文档的 40+ 个雷蛇 PID 来自这里
- [Ferris-echo/FerrisPulse](https://github.com/Ferris-echo/FerrisPulse) ——
  雷蛇 PA/PI、罗技 HID++、雷柏/瓦尔基里等品牌的实测型号与 PID；
  以及「哪些型号的 get_battery 实际被 daemon 支持」的交叉验证
- [yixing233/logi-tray](https://github.com/yixing233/logi-tray) ——
  罗技 HID++ 2.0 的两遍槽位探测与电量特性回退链
- [Banxiaxiala/weikav-max102-re](https://github.com/Banxiaxiala/weikav-max102-re) ——
  维咖 MAX102 固件逆向与 `cmd 20` 补丁

型号名与等级判断由本仓库维护，与上述项目无关。
