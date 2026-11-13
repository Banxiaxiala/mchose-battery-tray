// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [零开销] 未开启时 Enabled=false，所有调用点只是一次静态布尔判断，
//    不会因为日志拖慢 30 秒一次的轮询。
// 2. [十六进制原样输出] 抓包帧必须原样打印，不许做任何格式化加工 ——
//    接手人拿它跟官方驱动的抓包对比时，字形差一个空格就对不上。
// 3. [默认关闭] 只有带 --trace 参数才开。托盘模式下没有控制台，
//    乱写 Console 会拖慢甚至异常。
// 4. [日志落在程序目录] 开着 --trace 时同时写文件，路径在数据目录的
//    logs\ 下（便携模式下就是 exe 旁边的 data\logs\）。用户遇到问题要
//    发日志时，直接指给他「程序目录里的 data\logs」即可，不用去
//    %LOCALAPPDATA% 里翻。
// 5. [必须限长] 托盘版不带 --trace，正常情况下不会写；但一旦用户手动开了，
//    程序会常驻几天 —— 日志必须有大小上限，否则能把磁盘写满。
//    超过上限就滚动成 .1，只保留一份历史。
// 6. [写入失败不许影响主流程] 只读介质、权限不足都可能让写失败，
//    吞掉异常继续跑 —— 日志不该让程序崩。
// 7. [编码] UTF-8 with BOM。

using System;
using System.IO;
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
        /// <summary>单个日志文件的上限（字节）。超过就滚动一次</summary>
        private const long MaxLogBytes = 2 * 1024 * 1024;

        private static bool _enabled;
        private static string? _logPath;
        private static long _written;
        private static readonly object Gate = new();

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) StartFile();
            }
        }

        /// <summary>日志文件绝对路径；null 表示没启用或没开成</summary>
        public static string? LogPath { get { return _logPath; } }

        private static void StartFile()
        {
            try
            {
                string dir = Services.Settings.LogDirectory;
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "trace.log");
                _written = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;

                // 每次启动单独一段，方便把「这次运行」和上次切开
                Append("========== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " 启动 ==========");
            }
            catch (Exception)
            {
                // 写不了文件就退回纯控制台，不影响 --probe 的输出
                _logPath = null;
            }
        }

        public static void Line(string message)
        {
            if (!Enabled) return;
            try { Console.WriteLine(message); }
            catch (Exception) { }
            Append(message);
        }

        private static void Append(string message)
        {
            if (_logPath == null) return;
            lock (Gate)
            {
                try
                {
                    // 限长：写满就滚成 trace.log.1，只留一代。
                    // 不做「每行都查文件大小」，用累加的 _written 判断即可。
                    byte[] payload = Encoding.UTF8.GetBytes(message + Environment.NewLine);
                    if (_written + payload.Length > MaxLogBytes)
                    {
                        Roll();
                    }

                    using (var stream = new FileStream(_logPath, FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite))
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                    _written += payload.Length;
                }
                catch (Exception)
                {
                    // 磁盘满 / 只读介质：停止再尝试，免得每行都抛一次异常
                    _logPath = null;
                }
            }
        }

        private static void Roll()
        {
            try
            {
                string rotated = _logPath + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(_logPath!, rotated);
            }
            catch (Exception) { }
            _written = 0;
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
