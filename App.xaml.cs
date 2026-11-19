using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using NovaZip.Engine;

namespace NovaZip
{
    public partial class App : Application
    {
        private const string SingleInstanceName = @"Local\NovaZip.SingleInstance";

        private Mutex? _instanceMutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 全局滚轮平滑滚动：所有窗口通用
            SmoothScroller.Attach();

            if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                var code = SelfTest.Run(Path.Combine(AppContext.BaseDirectory, "selftest.log"));
                Shutdown(code);
                return;
            }

            // 无界面验证断点续传链路：NovaZip.exe --resumetest
            if (e.Args.Any(a => string.Equals(a, "--resumetest", StringComparison.OrdinalIgnoreCase)))
            {
                var code = ResumeTest.Run(Path.Combine(AppContext.BaseDirectory, "resumetest.log"));
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

            // 每次启动自检：已启用右键菜单时把菜单重新指向当前 exe，避免移动程序后菜单失效。
            // 注册表读写放到线程池，避免每次冷启动都为它等待磁盘/注册表，拖慢首帧显示。
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (ContextMenuIntegration.IsEnabled())
                        ContextMenuIntegration.Enable(out _);
                }
                catch
                {
                    // 权限不足或注册表被占用时忽略，不影响主程序启动
                }
            });

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
