# 外设电量

罗技 LIGHTSPEED 鼠标 + 雷蛇耳机（2.4G / 蓝牙）+ 维咖 MAX102 键盘（2.4G / 蓝牙）
的电量托盘监控。只占一个托盘图标（三圈同心环），左键单击弹出亚克力卡片看
历史趋势（柱状 / 折线，最长 2 年）与续航预测。

C# + WPF，单文件 exe **431 KB**，零第三方运行时依赖。

数据写在 exe 旁边的 `data\`，整个文件夹可整体搬走。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/README.md](docs/README.md) | 功能与用法、历史数据分层、便携模式、协议 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 分层、关键设计决策、线程模型、性能 |
| [docs/HANDOVER.md](docs/HANDOVER.md) | **第一次接手先读这个**：装环境、改代码、排查手册 |
| [docs/AGENTS.md](docs/AGENTS.md) | AI / 开发者的维护铁律 |

## 快速开始

```bat
build.bat
```

产物 `dist\外设电量.exe`，双击即用。

## 目录

```
build.bat   一键构建（自动定位装了 SDK 的 dotnet）
src/        生产代码（Models / Core / Hardware / Services / UI）
tests/      88 项脱机断言，零硬件、零第三方框架
docs/       文档
assets/     README 用图
```

## 授权

与罗技、雷蛇、维咖官方无关，不保证适配其他型号或固件版本。
