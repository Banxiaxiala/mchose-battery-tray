// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [三重对齐] 按 AGENT_RULES.md 第 5 条，自启动必须同时写三处：
//    Run 键、清除 StartupApproved 的「已禁用」标志、启动文件夹快捷方式。
//    只写 Run 会被任务管理器禁用；只写启动文件夹在部分精简系统上不认。
// 2. [不静默建桌面快捷方式] 绿色纯净原则，桌面图标只能用户手动触发。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PeripheralBattery.Services
{
    public static class AutoStart
    {
        public const string AppName = "外设电量";

        /// <summary>
        /// 改名前的名字。升级时要把它留下的 Run 项、启动快捷方式、
        /// StartupApproved 禁用标志一并清掉，否则每次开机会有
        /// 两条自启动指向同一个 exe（后启动的那个会因互斥量直接退出，
        /// 看上去无异常，但用户会在任务管理器里看到两个同名项）。
        /// </summary>
        private const string LegacyAppName = "2.4G设备电量";
        private const string LegacyLnkName = LegacyAppName + ".lnk";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string LnkName = AppName + ".lnk";

        private static string StartupDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            }
        }

        private static string LnkPath { get { return Path.Combine(StartupDirectory, LnkName); } }

        private static string LaunchCommand
        {
            get
            {
                string exe = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
                return "\"" + exe + "\"";
            }
        }

        // ---------------------------------------------------------- 注册表
        public static bool RunKeyEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppName) != null;
            }
            catch (Exception) { return false; }
        }

        private static bool WriteRunKey(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return false;
                if (enable)
                {
                    key.SetValue(AppName, LaunchCommand, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 删掉 StartupApproved 里的「已禁用」标志，让 Run 项真正生效。
        /// 任务管理器把启动项禁用时会在这里写一条二进制值；只删 Run 项不会清它，
        /// 于是重新开启后系统仍然跳过本程序。
        /// </summary>
        public static void ClearStartupApproved()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------- 启动文件夹
        public static bool ShortcutEnabled()
        {
            return File.Exists(LnkPath);
        }

        private static bool WriteShortcut(bool enable)
        {
            try
            {
                if (enable)
                {
                    Directory.CreateDirectory(StartupDirectory);
                    string script =
                        "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + LnkPath + "');" +
                        "$s.TargetPath='" + LaunchCommand.Trim('"') + "';" +
                        "$s.WorkingDirectory='" + Path.GetDirectoryName(LaunchCommand.Trim('"')) + "';" +
                        "$s.Save()";

                    var info = new ProcessStartInfo("powershell",
                        "-NoProfile -NonInteractive -Command \"" + script + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    };
                    using var proc = Process.Start(info);
                    proc?.WaitForExit(15000);
                }
                else if (File.Exists(LnkPath))
                {
                    File.Delete(LnkPath);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------- 对外
        public static bool Enabled
        {
            get { return RunKeyEnabled() || ShortcutEnabled(); }
        }

        public static bool SetEnabled(bool enable)
        {
            bool ok = WriteRunKey(enable);
            if (enable) ClearStartupApproved();
            ok = WriteShortcut(enable) && ok;
            return ok;
        }

        /// <summary>
        /// 清掉旧名字（2.4G设备电量）遗留的自启动项。只需在启动时调一次，
        /// 幂等且静默失败 —— 清不掉也不影响新名字的自启动。
        /// </summary>
        public static void MigrateLegacyEntries()
        {
            try
            {
                if (!Enabled && LegacyEnabled())
                {
                    // 用户本来是开着的：改名后继续替他开着
                    SetEnabled(true);
                }

                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    key?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
                }
                using (var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
                {
                    key?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
                }

                string legacyLnk = Path.Combine(StartupDirectory, LegacyLnkName);
                if (File.Exists(legacyLnk)) File.Delete(legacyLnk);
            }
            catch (Exception) { }
        }

        /// <summary>旧名字的自启动项还在不在</summary>
        private static bool LegacyEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key?.GetValue(LegacyAppName) != null) return true;
                }
                return File.Exists(Path.Combine(StartupDirectory, LegacyLnkName));
            }
            catch (Exception) { return false; }
        }
    }
}
