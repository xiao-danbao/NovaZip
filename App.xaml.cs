using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using NovaZip.Engine;

namespace NovaZip
{
    public partial class App : Application
    {
        private const string VersionKeyPath = @"Software\NovaZip";
        private const string SingleInstanceName = @"Local\NovaZip.SingleInstance";

        private Mutex? _instanceMutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                var code = SelfTest.Run(Path.Combine(AppContext.BaseDirectory, "selftest.log"));
                Shutdown(code);
                return;
            }

            // 无界面性能基准：--bench <压缩包> 或 --bench-create <输出包> <源...>，可加 --bench-out <日志>
            var benchOutIndex = OptionIndex(e.Args, "--bench-out");
            var benchOut = benchOutIndex >= 0 && benchOutIndex + 1 < e.Args.Length
                ? e.Args[benchOutIndex + 1]
                : null;

            var benchIndex = OptionIndex(e.Args, "--bench");
            if (benchIndex >= 0 && benchIndex + 1 < e.Args.Length)
            {
                Shutdown(Bench.RunExtract(e.Args[benchIndex + 1], benchOut));
                return;
            }

            var benchCreateIndex = OptionIndex(e.Args, "--bench-create");
            if (benchCreateIndex >= 0 && benchCreateIndex + 2 < e.Args.Length)
            {
                var sources = e.Args.Skip(benchCreateIndex + 2)
                    .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();

                // 基准跑在线程池（MTA）线程上：与界面里 Task.Run 的实际调用方式一致，
                // 否则会退化成每次回调都跨套间调度，测出的小文件开销偏高
                var code = 0;
                var worker = new Thread(() => code = Bench.RunCreate(e.Args[benchCreateIndex + 1], sources, benchOut));
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
                worker.Join();
                Shutdown(code);
                return;
            }

            // 一台电脑存在多个版本时，一律使用最新版本
            if (RedirectToNewerVersion(e.Args))
            {
                Shutdown(0);
                return;
            }

            ThemeManager.Initialize();

            // 单实例：主界面只允许一个进程，重复启动时激活已有窗口
            if (IsPrimaryLaunch(e.Args))
            {
                var mutex = new Mutex(true, SingleInstanceName, out var created);
                if (created)
                {
                    _instanceMutex = mutex;
                }
                else
                {
                    mutex.Dispose();
                    if (ActivateExistingWindow())
                    {
                        Shutdown(0);
                        return;
                    }

                    // 已登记的实例没有可见窗口（例如仍在启动中），继续启动本次进程
                }
            }

            // 静默维护右键菜单（安装/移动 exe 后可用它重新指向当前程序）
            if (e.Args.Any(a => string.Equals(a, "--register-menu", StringComparison.OrdinalIgnoreCase)))
            {
                Shutdown(ContextMenuIntegration.Enable(out _) ? 0 : 1);
                return;
            }

            if (e.Args.Any(a => string.Equals(a, "--unregister-menu", StringComparison.OrdinalIgnoreCase)))
            {
                Shutdown(ContextMenuIntegration.Disable(out _) ? 0 : 1);
                return;
            }

            // 每次启动自检：已启用右键菜单时把菜单重新指向当前 exe，避免移动程序后菜单失效
            if (ContextMenuIntegration.IsEnabled())
                ContextMenuIntegration.Enable(out _);

            // 资源管理器右键解压：使用独立的进度窗口
            var extractHere = OptionIndex(e.Args, "--extract-here");
            var extractSmart = OptionIndex(e.Args, "--extract-smart");
            var extractFolder = OptionIndex(e.Args, "--extract");

            var extractIndex = extractHere >= 0 ? extractHere
                : extractSmart >= 0 ? extractSmart
                : extractFolder;

            if (extractIndex >= 0 && extractIndex + 1 < e.Args.Length)
            {
                var archive = e.Args[extractIndex + 1];
                if (File.Exists(archive))
                {
                    var mode = extractHere >= 0 ? QuickExtractMode.CurrentFolder
                        : extractSmart >= 0 ? QuickExtractMode.Smart
                        : QuickExtractMode.Folder;
                    new QuickExtractWindow(archive, mode).Show();
                    return;
                }
            }

            // 右键压缩：独立的快速压缩窗口，与解压共用同一套进度界面
            var format = "";
            var compressIndex = -1;

            foreach (var candidate in new[] { "7z", "zip", "tar" })
            {
                compressIndex = OptionIndex(e.Args, "--compress-" + candidate);
                if (compressIndex >= 0)
                {
                    format = candidate;
                    break;
                }
            }

            if (compressIndex < 0)
            {
                compressIndex = OptionIndex(e.Args, "--compress");
                if (compressIndex >= 0) format = "7z";
            }

            if (compressIndex >= 0)
            {
                var paths = ValuesAfter(e.Args, compressIndex).Where(p => !p.StartsWith("--")).ToArray();
                if (paths.Length > 0)
                {
                    new QuickExtractWindow(paths, format).Show();
                    return;
                }
            }

            var window = new MainWindow();

            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            {
                window.QueueArchive(e.Args[0]);
            }

            window.Show();
        }

        private static int OptionIndex(string[] args, string name) =>
            Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>主界面启动（无参数、仅 --open 或直接带文件路径）需要抢占单实例。</summary>
        private static bool IsPrimaryLaunch(string[] args) =>
            args.All(a => string.Equals(a, "--open", StringComparison.OrdinalIgnoreCase) ||
                          !a.StartsWith("--", StringComparison.Ordinal));

        private static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

        /// <summary>
        /// 一台电脑存在多个版本时，只允许最新版本运行：转发启动参数给更新的版本后退出。
        /// </summary>
        private static bool RedirectToNewerVersion(string[] args)
        {
            var currentPath = Environment.ProcessPath ?? "";
            var currentVersion = CurrentVersion;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(VersionKeyPath);
                var latestPath = key?.GetValue("LatestPath") as string;

                if (!string.IsNullOrEmpty(latestPath) &&
                    File.Exists(latestPath) &&
                    !string.Equals(Path.GetFullPath(latestPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    var info = FileVersionInfo.GetVersionInfo(latestPath);
                    if (Version.TryParse(info.FileVersion, out var latest) && latest > currentVersion)
                    {
                        Process.Start(new ProcessStartInfo(latestPath, BuildArgumentLine(args))
                        {
                            UseShellExecute = true
                        });
                        return true;
                    }
                }
            }
            catch
            {
                // 检测失败时按当前版本继续运行
            }

            RememberCurrentVersion(currentPath, currentVersion);
            return false;
        }

        private static void RememberCurrentVersion(string path, Version version)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(VersionKeyPath);
                key?.SetValue("LatestPath", path);
                key?.SetValue("LatestVersion", version.ToString());
            }
            catch
            {
                // 无权限时忽略
            }
        }

        private static string BuildArgumentLine(string[] args) =>
            string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

        /// <summary>
        /// 激活已在运行的 NovaZip 主窗口。
        /// 不同版本的 exe 文件名不同（NovaZip-1.2.1-win-x64.exe 等），因此按进程名前缀匹配。
        /// </summary>
        private static bool ActivateExistingWindow()
        {
            try
            {
                IntPtr handle = IntPtr.Zero;

                foreach (var process in Process.GetProcesses())
                {
                    if (process.Id == Environment.ProcessId) continue;
                    if (!process.ProcessName.StartsWith("NovaZip", StringComparison.OrdinalIgnoreCase)) continue;

                    var candidate = process.MainWindowHandle;
                    if (candidate == IntPtr.Zero) continue;
                    handle = candidate;
                }

                if (handle == IntPtr.Zero) return false;

                ShowWindow(handle, 9); // SW_RESTORE
                SetForegroundWindow(handle);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> ValuesAfter(string[] args, int index)
        {
            for (var i = index + 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal)) yield break;
                yield return args[i];
            }
        }
    }
}
