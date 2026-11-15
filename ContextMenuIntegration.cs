using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NovaZip
{
    /// <summary>
    /// 在 HKCU 下注册 / 移除资源管理器右键菜单，无需管理员权限。
    /// 使用单一级联项 + ExtendedSubCommandsKey 子菜单（与系统 BitLocker 菜单相同结构）。
    /// </summary>
    internal static class ContextMenuIntegration
    {
        private const string ClassesRoot = @"Software\Classes";
        private const string MenuKeyName = "NovaZip";
        private const string LegacyExtractKey = "NovaZip.Extract";
        private const string LegacyCompressKey = "NovaZip.Compress";
        private const string ProgId = "NovaZip.Archive";

        private static readonly (string Key, string Label, string Argument)[] FileVerbs =
        {
            ("01", "打开 NovaZip", "--open"),
            ("02", "解压到当前文件夹", "--extract-here"),
            ("03", "解压到同名文件夹", "--extract"),
            ("04", "智能解压（自动去除重复目录）", "--extract-smart"),
            ("05", "压缩为 7Z", "--compress-7z"),
            ("06", "压缩为 ZIP", "--compress-zip"),
            ("07", "压缩为 TAR", "--compress-tar"),
        };

        private static readonly (string Key, string Label, string Argument)[] FolderVerbs =
        {
            ("01", "打开 NovaZip", "--open"),
            ("02", "压缩为 7Z", "--compress-7z"),
            ("03", "压缩为 ZIP", "--compress-zip"),
            ("04", "压缩为 TAR", "--compress-tar"),
        };

        /// <summary>Windows 11 经典右键菜单开关：写入该 CLSID 后资源管理器回退到旧版菜单。</summary>
        private const string ClassicMenuKey =
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

        private const uint ShcneAssocChanged = 0x08000000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>通知资源管理器刷新外壳缓存，让新写入的菜单立即生效。</summary>
        public static void RefreshShell()
        {
            try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); }
            catch
            {
                // 旧系统或受限环境忽略
            }
        }

        public static readonly string[] ArchiveExtensions =
        {
            ".7z", ".zip", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".zst", ".iso", ".cab", ".wim",
            ".arj", ".lzh", ".jar", ".apk", ".msi", ".rpm", ".deb", ".zipx", ".xapk", ".epub", ".lzma", ".z", ".001"
        };

        public static string ExecutablePath
        {
            get
            {
                var process = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(process) &&
                    File.Exists(process) &&
                    !string.Equals(Path.GetFileName(process), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    return process;

                return Path.Combine(AppContext.BaseDirectory, "NovaZip.exe");
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    ClassesRoot + @"\*\shell\" + MenuKeyName + @"\shell\01");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool Enable(out string message)
        {
            try
            {
                var exe = ExecutablePath;

                CleanupLegacy();
                DeleteFlatVerbs();

                WriteMenu("*", exe, FileVerbs, "%1", @"*\shell\" + MenuKeyName);
                WriteMenu("Directory", exe, FolderVerbs, "%1", @"Directory\shell\" + MenuKeyName);
                // 文件夹空白处右键：压缩当前文件夹
                WriteMenu(@"Directory\Background", exe, FolderVerbs, "%V",
                    @"Directory\Background\shell\" + MenuKeyName);

                RefreshShell();

                message = "已启用 NovaZip 右键菜单";
                return true;
            }
            catch (Exception ex)
            {
                message = "无法写入注册表：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 写入一级 NovaZip 菜单 + 二级子菜单。
        /// 采用 Windows 自带 BitLocker 菜单相同的 ExtendedSubCommandsKey 结构，
        /// 静态 SubCommands 级联在部分 Windows 11 版本上不会显示。
        /// </summary>
        private static void WriteMenu(string root, string exe,
            (string Key, string Label, string Argument)[] verbs, string pathToken, string subCommandsKey)
        {
            var menuPath = ClassesRoot + "\\" + root + @"\shell\" + MenuKeyName;
            using var menu = Registry.CurrentUser.CreateSubKey(menuPath);
            if (menu == null) return;

            menu.SetValue("MUIVerb", "NovaZip");
            menu.SetValue("Icon", exe);
            menu.SetValue("MultiSelectModel", "Single");
            menu.SetValue("ExtendedSubCommandsKey", subCommandsKey);

            // 先清空旧子项，避免升级后残留上一次版本的菜单
            try { menu.DeleteSubKeyTree("shell", false); } catch { /* ignore */ }

            using var shell = menu.CreateSubKey("shell");
            if (shell == null) return;

            foreach (var verb in verbs)
            {
                using var item = shell.CreateSubKey(verb.Key);
                if (item == null) continue;

                item.SetValue("MUIVerb", verb.Label);
                item.SetValue("Icon", exe);
                item.SetValue("MultiSelectModel", "Single");

                using var command = item.CreateSubKey("command");
                // 打开主界面不需要附带文件路径
                command?.SetValue(null, verb.Argument == "--open"
                    ? "\"" + exe + "\" " + verb.Argument
                    : "\"" + exe + "\" " + verb.Argument + " \"" + pathToken + "\"");
            }
        }

        public static bool Disable(out string message)
        {
            try
            {
                DeleteCascadeMenu();
                DeleteFlatVerbs();
                CleanupLegacy();
                RefreshShell();

                message = "已移除 NovaZip 右键菜单";
                return true;
            }
            catch (Exception ex)
            {
                message = "无法移除右键菜单：" + ex.Message;
                return false;
            }
        }

        /// <summary>删除一级 NovaZip 菜单（含二级子菜单）。</summary>
        private static void DeleteCascadeMenu()
        {
            foreach (var root in new[] { "*", "Directory", @"Directory\Background" })
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    ClassesRoot + "\\" + root + @"\shell\" + MenuKeyName, false);
            }
        }

        /// <summary>删除 1.2.0 注册的扁平菜单项。</summary>
        private static void DeleteFlatVerbs()
        {
            foreach (var root in new[] { "*", "Directory", @"Directory\Background" })
            {
                var shellPath = ClassesRoot + "\\" + root + @"\shell\";
                foreach (var index in new[] { "01", "02", "03", "04", "05", "06" })
                    Registry.CurrentUser.DeleteSubKeyTree(shellPath + "NovaZip" + index, false);
            }
        }

        // ==================== Windows 11 经典右键菜单 ====================

        /// <summary>Windows 11 的新式右键菜单不显示第三方旧式菜单项。</summary>
        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        public static bool IsClassicMenuEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuKey);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>回退到 Windows 10 经典右键菜单，让 NovaZip 直接出现在一级菜单中。</summary>
        public static bool EnableClassicMenu(out string message)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuKey);
                key?.SetValue(null, "");
                RefreshShell();
                message = "已切换为经典右键菜单，正在重启资源管理器…";
                return true;
            }
            catch (Exception ex)
            {
                message = "无法切换经典菜单：" + ex.Message;
                return false;
            }
        }

        public static bool DisableClassicMenu(out string message)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuKey, false);
                RefreshShell();
                message = "已恢复 Windows 11 新式右键菜单，正在重启资源管理器…";
                return true;
            }
            catch (Exception ex)
            {
                message = "无法恢复新式菜单：" + ex.Message;
                return false;
            }
        }

        /// <summary>重启资源管理器，让菜单外观的改动立即生效。</summary>
        public static void RestartExplorer()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    try { process.Kill(); } catch { }
                }

                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch
            {
                // 重启失败时由系统下次登录生效
            }
        }

        /// <summary>清理 1.0 版本按扩展名 / ProgID / SystemFileAssociations 分散写入的菜单项。</summary>
        private static void CleanupLegacy()
        {
            foreach (var path in LegacyExtractKeyPaths())
                Registry.CurrentUser.DeleteSubKeyTree(path, false);

            foreach (var root in new[] { "*", "Directory" })
                Registry.CurrentUser.DeleteSubKeyTree(
                    ClassesRoot + "\\" + root + @"\shell\" + LegacyCompressKey, false);
        }

        private static IEnumerable<string> LegacyExtractKeyPaths()
        {
            foreach (var ext in ArchiveExtensions)
            {
                yield return ClassesRoot + "\\" + ext + @"\shell\" + LegacyExtractKey;
                yield return ClassesRoot + @"\SystemFileAssociations\" + ext + @"\shell\" + LegacyExtractKey;

                foreach (var progId in ResolveProgIds(ext))
                    yield return ClassesRoot + "\\" + progId + @"\shell\" + LegacyExtractKey;
            }
        }

        private static IEnumerable<string> ResolveProgIds(string ext)
        {
            var progIds = new List<string>();

            try
            {
                using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
                var progId = (extKey?.GetValue(null) as string)?.Trim();
                if (!string.IsNullOrEmpty(progId) && !progId.StartsWith(".", StringComparison.Ordinal))
                    progIds.Add(progId);
            }
            catch
            {
                // 读不到 HKCR 时忽略
            }

            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice");
                var chosen = (choice?.GetValue("ProgId") as string)?.Trim();
                if (!string.IsNullOrEmpty(chosen) && !chosen.StartsWith(".", StringComparison.Ordinal) &&
                    !progIds.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                    progIds.Add(chosen);
            }
            catch
            {
                // 没有 UserChoice 时忽略
            }

            foreach (var id in progIds)
                yield return id;
        }

        // ==================== 文件关联 ====================

        public static bool IsFileAssociationEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + ProgId);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 注册 ProgID 并加入各扩展名的 OpenWithProgids，
        /// 让 NovaZip 出现在「打开方式」列表中（默认程序仍需用户在系统设置中选择）。
        /// </summary>
        public static bool EnableFileAssociation(out string message)
        {
            try
            {
                var exe = ExecutablePath;

                using (var prog = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + ProgId))
                {
                    prog?.SetValue(null, "NovaZip 压缩包");
                    using var icon = prog?.CreateSubKey("DefaultIcon");
                    icon?.SetValue(null, exe + ",0");
                    using var command = prog?.CreateSubKey(@"shell\open\command");
                    command?.SetValue(null, "\"" + exe + "\" \"%1\"");
                }

                foreach (var ext in ArchiveExtensions)
                {
                    using var openWith = Registry.CurrentUser.CreateSubKey(
                        ClassesRoot + "\\" + ext + @"\OpenWithProgids");
                    openWith?.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                }

                message = "已加入「打开方式」列表，可在右键 → 打开方式中选择 NovaZip";
                RefreshShell();
                return true;
            }
            catch (Exception ex)
            {
                message = "无法注册文件关联：" + ex.Message;
                return false;
            }
        }

        public static bool DisableFileAssociation(out string message)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + "\\" + ProgId, false);

                foreach (var ext in ArchiveExtensions)
                {
                    using var openWith = Registry.CurrentUser.OpenSubKey(
                        ClassesRoot + "\\" + ext + @"\OpenWithProgids", true);
                    openWith?.DeleteValue(ProgId, false);
                }

                message = "已从「打开方式」列表移除 NovaZip";
                RefreshShell();
                return true;
            }
            catch (Exception ex)
            {
                message = "无法移除文件关联：" + ex.Message;
                return false;
            }
        }
    }
}
