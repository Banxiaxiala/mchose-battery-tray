# -*- coding: utf-8 -*-
"""⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️

分层根包。目录归属（禁止把所有东西塞回一个文件）：

    src/models/     纯数据与纯换算（无 I/O，可脱机单测）
    src/core/       Win32 底层：HID 句柄、窗口消息、注册表
    src/hardware/   品牌私有协议：罗技 HID++ / 雷蛇 PA-PI
    src/services/   后台长效服务：历史存储、续航预测
    src/ui/         托盘、图标绘制、卡片窗口

2. [导入方式] 各层之间只允许「下层不依赖上层」：hardware 不许 import ui，
   models 不许 import 其它任何层。
3. [编码] UTF-8 with BOM。
"""
