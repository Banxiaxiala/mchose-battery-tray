// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [零开销] 未开启时 Enabled=false，所有调用点只是一次静态布尔判断，
//    不会因为日志拖慢 30 秒一次的轮询。
// 2. [十六进制原样输出] 抓包帧必须原样打印，不许做任何格式化加工 ——
//    接手人拿它跟官方驱动的抓包对比时，字形差一个空格就对不上。
// 3. [默认关闭] 只有带 --trace 参数才开。托盘模式下没有控制台，
//    乱写 Console 会拖慢甚至异常。
// 4. [编码] UTF-8 with BOM。

using System;
using System.Text;

namespace PeripheralBattery.Core
{
    /// <summary>
    /// 极简调试输出。用来排查「设备读不到」这类问题 ——
    /// 打开后能看到每一步发了什么、收到什么、在哪个槽位被拒。
    ///
    /// 用法：<c>外设电量.exe --probe --trace</c>
///
/// 命名刻意叫 Tracer 而不是 Trace：System.Diagnostics.Trace 同名，
/// readers 里同时 using 了 System.Diagnostics（为了 Stopwatch），
/// 会撞成 CS0104 二义性。
    /// </summary>
    public static class Tracer
    {
        public static bool Enabled { get; set; }

        private static readonly StringBuilder Buffer = new();

        public static void Line(string message)
        {
            if (!Enabled) return;
            try { Console.WriteLine(message); }
            catch (Exception) { }
        }

        public static void Section(string title)
        {
            if (!Enabled) return;
            Line("");
            Line("┌─ " + title + " " + new string('─', Math.Max(0, 58 - title.Length)));
        }

        /// <summary>把字节数组打成 11 02 8F 00 … 的形式</summary>
        public static string Hex(byte[]? data, int maxLength = 64)
        {
            if (data == null) return "(null)";
            if (data.Length == 0) return "(empty)";

            var builder = new StringBuilder(data.Length * 3);
            int count = Math.Min(data.Length, maxLength);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(data[i].ToString("X2"));
            }
            if (data.Length > maxLength)
            {
                builder.Append(" … (").Append(data.Length).Append(" bytes)");
            }
            return builder.ToString();
        }

        public static void Frame(string direction, byte[]? data)
        {
            if (!Enabled) return;
            Line("│  " + direction + "  " + Hex(data));
        }

        public static void Note(string message)
        {
            if (!Enabled) return;
            Line("│  " + message);
        }
    }
}
