// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [纯函数] 本文件只做字符串拼装与裁剪，零 I/O、零 Win32、零 WPF。
//    这样它才能进 src-cs.Tests 做脱机断言。
// 2. [63 字符上限] NotifyIcon.Text 超过 63 字符会抛异常（不是静默截断），
//    所以裁剪必须在这里做完，绝不能交给调用方「试试看」。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;

namespace PeripheralBattery.Models
{
    /// <summary>托盘悬停提示的拼装与降级</summary>
    public static class TooltipText
    {
        /// <summary>
        /// NotifyIcon.Text 的硬上限（字符，含换行）。超过会抛 ArgumentException。
        /// </summary>
        public const int MaxLength = 63;

        /// <summary>提示标题</summary>
        public const string Title = "外设电量";

        /// <summary>一行提示：标量数据 + 可选后缀</summary>
        public sealed class Entry
        {
            /// <summary>卡片/提示上的标签，如「鼠标」</summary>
            public string Label = "";
            /// <summary>完整行（含型号、续航预测），可能很长</summary>
            public string Full = "";
            /// <summary>紧凑行（仅标签 + 电量 + 状态），保证很短</summary>
            public string Compact = "";
        }

        /// <summary>
        /// 按「信息量从多到少」逐级降级，返回不超过 MaxLength 的提示文本：
        ///
        ///   1. 完整行（带型号 + 续航预测）—— 只有一两台设备时能放下
        ///   2. 紧凑行（`鼠标 51%  放电中`）—— 三台设备时就靠这级
        ///   3. 只留标题 + 尽量多的紧凑行
        ///   4. 实在还超就硬截断（最后手段，正常永远到不了）
        ///
        /// **绝不能逐行 RemoveAt 循环到只剩标题** —— 旧实现就是这么干的，
        /// 结果 4 行 × 60 字符的输入被裁成只剩「外设电量」4 个字符，
        /// 悬浮时看不到任何电量。
        /// </summary>
        public static string Build(IReadOnlyList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return Title;

            string full = Join(entries, useCompact: false);
            if (full.Length <= MaxLength) return full;

            string compact = Join(entries, useCompact: true);
            if (compact.Length <= MaxLength) return compact;

            // 只保留能放下的前 N 台
            var kept = new List<string> { Title };
            for (int i = 0; i < entries.Count; i++)
            {
                string line = string.IsNullOrEmpty(entries[i].Compact)
                    ? entries[i].Label
                    : entries[i].Compact;
                var candidate = new List<string>(kept) { line };
                if (string.Join("\n", candidate).Length > MaxLength) break;
                kept.Add(line);
            }
            string result = string.Join("\n", kept);
            return result.Length > MaxLength ? result.Substring(0, MaxLength) : result;
        }

        private static string Join(IReadOnlyList<Entry> entries, bool useCompact)
        {
            var lines = new List<string> { Title };
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string line = useCompact
                    ? (string.IsNullOrEmpty(e.Compact) ? e.Label : e.Compact)
                    : (string.IsNullOrEmpty(e.Full) ? e.Label : e.Full);
                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

        /// <summary>紧凑行：`鼠标 51%  放电中`。无型号、无续航，尽量短。</summary>
        public static string CompactOf(string label, int? percent, bool connected,
            bool charging)
        {
            string pct = percent.HasValue ? percent.Value + "%" : "--";
            string suffix = "";
            if (!connected) suffix = "  (离线)";
            else if (charging) suffix = "  充电中";
            return label + "  " + pct + suffix;
        }
    }
}
