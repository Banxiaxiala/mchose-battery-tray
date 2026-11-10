// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [消除 WPF 与 WinForms 的命名冲突] 本文件关掉两者的隐式 using，
//    改为在各自的命名空间里显式引用。
//    否则 System.Drawing.Color 与 System.Windows.Media.Color、
//    System.Drawing.Point 与 System.Windows.Point、System.Drawing.Brush 与
//    System.Windows.Media.Brush 会全部撞成 CS0104 二义性错误。
// 2. [编码] UTF-8 with BOM。

global using System;
global using System.Collections.Generic;
global using System.Linq;

// WPF 侧（卡片 / 图表 / 图标）统一用这些别名
global using WpfColor = System.Windows.Media.Color;
global using WpfBrush = System.Windows.Media.Brush;
global using WpfPoint = System.Windows.Point;
global using WpfPath = System.Windows.Shapes.Path;

// WinForms 侧（托盘）统一用这些别名
global using WinFormsPoint = System.Drawing.Point;
global using WinFormsColor = System.Drawing.Color;
