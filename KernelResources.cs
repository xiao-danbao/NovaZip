using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NovaZip
{
    /// <summary>
    /// 7-Zip 内核（7z.dll / 7z.exe）定位与自恢复。
    /// 优先使用程序目录旁的 runtime 副本（安装包/绿色版），
    /// 缺失时（例如应用内更新只替换了 exe）从 EXE 内嵌资源解出到本机缓存目录，
    /// 保证「只有一个 exe 也能正常解压」。
    /// </summary>
    internal static class KernelResources
    {
        private const string DllName = "7z.dll";
        private const string ExeName = "7z.exe";
        private const string LicenseName = "7-Zip-License.txt";

        private const string DllResource = "NovaZip.runtime.7z.dll";
        private const string ExeResource = "NovaZip.runtime.7z.exe";
        private const string LicenseResource = "NovaZip.runtime.7-Zip-License.txt";

        private static readonly object Sync = new();
        private static string? _dir;
        private static bool _resolved;

        /// <summary>内核所在目录（含 7z.dll / 7z.exe）；彻底不可用时返回 null。</summary>
        public static string? Directory
        {
            get
            {
                if (_resolved) return _dir;
                lock (Sync)
                {
                    if (_resolved) return _dir;
                    _dir = Resolve();
                    _resolved = true;
                    return _dir;
                }
            }
        }

        /// <summary>7z.dll 完整路径；找不到时返回 null。</summary>
        public static string? DllPath => Existing(DllName);

        /// <summary>7z.exe 完整路径；找不到时返回 null。</summary>
        public static string? ExePath => Existing(ExeName);

        /// <summary>当前内核目录的来源说明，用于自检日志诊断。</summary>
        public static string SourceText { get; private set; } = "unknown";

        private static string? Existing(string fileName)
        {
            var dir = Directory;
            if (dir == null) return null;
            var path = Path.Combine(dir, fileName);
            return File.Exists(path) ? path : null;
        }

        private static string? Resolve()
        {
            foreach (var dir in LocalCandidates())
            {
                if (File.Exists(Path.Combine(dir, DllName)))
                {
                    SourceText = "app-dir: " + dir;
                    return dir;
                }
            }

            var cache = ExtractEmbedded();
            if (cache != null && File.Exists(Path.Combine(cache, DllName)))
            {
                SourceText = "embedded-cache: " + cache;
                return cache;
            }

            // 最后兜底：系统安装的 7-Zip（版本可能不同，仅保证可用）
            foreach (var fallback in new[] { @"C:\Program Files\7-Zip", @"C:\Program Files (x86)\7-Zip" })
            {
                if (File.Exists(Path.Combine(fallback, DllName)))
                {
                    SourceText = "system-7zip: " + fallback;
                    return fallback;
                }
            }

            SourceText = "not-found";
            return null;
        }

        private static IEnumerable<string> LocalCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(List<string> list, string? dir)
            {
                if (string.IsNullOrWhiteSpace(dir)) return;
                if (!seen.Add(dir)) return;
                list.Add(dir);
            }

            var list = new List<string>();
            var baseDir = AppContext.BaseDirectory;
            Add(list, baseDir);
            Add(list, Path.Combine(baseDir, "runtime"));

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            if (!string.IsNullOrEmpty(exeDir))
            {
                Add(list, exeDir);
                Add(list, Path.Combine(exeDir, "runtime"));
            }

            return list;
        }

        /// <summary>把 EXE 内嵌的内核解到 %LOCALAPPDATA%\NovaZip\kernel\&lt;版本&gt;；失败返回 null。</summary>
        private static string? ExtractEmbedded()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var probe = assembly.GetManifestResourceStream(DllResource);
                if (probe == null) return null; // 该构建未内嵌内核

                var version = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NovaZip", "kernel", version);

                System.IO.Directory.CreateDirectory(root);

                ExtractOne(assembly, DllResource, Path.Combine(root, DllName));
                ExtractOne(assembly, ExeResource, Path.Combine(root, ExeName));
                ExtractOne(assembly, LicenseResource, Path.Combine(root, LicenseName));

                return root;
            }
            catch
            {
                return null;
            }
        }

        private static void ExtractOne(Assembly assembly, string resource, string targetPath)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null) return;

            // 已存在且大小一致则直接复用，避免每次启动重写
            try
            {
                var info = new FileInfo(targetPath);
                if (info.Exists && info.Length == stream.Length) return;
            }
            catch
            {
                // 大小读不到时继续走重写流程
            }

            var temp = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(file);
                }

                if (File.Exists(targetPath))
                {
                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { /* ignore */ }
                    File.Move(temp, targetPath, overwrite: true);
                }
                else
                {
                    File.Move(temp, targetPath);
                }
            }
            catch
            {
                // 目标被占用（另一实例正在使用）时保留已有文件
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }
}
