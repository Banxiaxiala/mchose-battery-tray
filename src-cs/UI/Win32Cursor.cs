// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [薄封装] 这里只做「给卡片判断鼠标是否在窗口内」这一件事。
//    真正的 P/Invoke 声明在 Core/Win32.cs，不要在这里重复声明。
// 2. [编码] UTF-8 with BOM。

using PeripheralBattery.Core;

namespace PeripheralBattery.UI
{
    internal static class Win32Cursor
    {
        /// <summary>取光标屏幕坐标；失败返回 false</summary>
        public static bool TryGetCursorPos(out int x, out int y)
        {
            if (Win32.GetCursorPos(out Win32.POINT point))
            {
                x = point.X;
                y = point.Y;
                return true;
            }
            x = 0;
            y = 0;
            return false;
        }
    }
}
