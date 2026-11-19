using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NovaZip
{
    /// <summary>调用随程序分发的 7z.exe 完成压缩包内编辑（追加 / 删除 / 重命名）。</summary>
    internal static class ArchiveEditor
    {
        /// <summary>7z.exe 路径：统一走 KernelResources（程序目录缺失时使用内嵌内核缓存）。</summary>
        public static string? Find7z()
        {
            return KernelResources.ExePath;
        }

        public static (bool Ok, string Message) Add(string archive, IReadOnlyList<string> files, string password)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法编辑压缩包。");

            var parent = files.Count > 0 ? Path.GetDirectoryName(files[0].TrimEnd('\\', '/')) : null;
            var sameParent = parent != null && files.All(f =>
                string.Equals(Path.GetDirectoryName(f.TrimEnd('\\', '/')), parent, StringComparison.OrdinalIgnoreCase));

            var args = new StringBuilder("a \"").Append(archive).Append('"');
            foreach (var file in files)
            {
                var name = sameParent ? Path.GetFileName(file.TrimEnd('\\', '/')) : file;
                args.Append(" \"").Append(name).Append('"');
            }
            AppendPassword(args, password);
            args.Append(" -y");

            return Execute(exe, args.ToString(), sameParent ? parent : null);
        }

        public static bool IsSplitVolume(string path) =>
            path.EndsWith(".001", StringComparison.OrdinalIgnoreCase);

        /// <summary>把单个文件按压缩包内原路径写回（同名条目覆盖），用于预览就地编辑保存。</summary>
        public static (bool Ok, string Message) UpdateEntry(string archive, string entryPath, string sourceFile,
            string password)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法编辑压缩包。");

            var relative = entryPath.Replace('/', '\\').TrimStart('\\');
            if (relative.Length == 0) return (false, "条目路径无效。");

            var sourceFull = Path.GetFullPath(sourceFile);
            var root = sourceFull.EndsWith(relative, StringComparison.OrdinalIgnoreCase)
                ? sourceFull.Substring(0, sourceFull.Length - relative.Length).TrimEnd('\\')
                : Path.GetDirectoryName(sourceFull) ?? "";

            var args = new StringBuilder("a \"").Append(archive).Append("\" \"").Append(relative).Append('"');
            AppendPassword(args, password);
            args.Append(" -y");

            return Execute(exe, args.ToString(), root);
        }

        /// <summary>分卷压缩包（.001）由 7z.exe 直接展开，7z.dll 无法按分卷集合打开。</summary>
        public static (bool Ok, string Message) ExtractVolume(string archive, string target, string password)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法解压分卷。");

            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }

            var args = new StringBuilder("x \"").Append(archive).Append("\" -o\"").Append(target).Append("\" -y");
            AppendPassword(args, password);

            return Execute(exe, args.ToString(), null);
        }

        public static (bool Ok, string Message) CreateVolumes(string archive, IReadOnlyList<string> sources,
            string password, string format, int level, int volumeMb)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法进行分卷压缩。");

            var args = new StringBuilder("a \"").Append(archive).Append('"');
            foreach (var source in sources)
                args.Append(" \"").Append(source).Append('"');

            args.Append(" -t").Append(format);
            args.Append(" -mx=").Append(level);
            args.Append(" -mmt=").Append(Math.Max(1, Environment.ProcessorCount));
            args.Append(" -v").Append(volumeMb).Append("m");
            AppendPassword(args, password);
            args.Append(" -y");

            return Execute(exe, args.ToString(), null);
        }

        public static (bool Ok, string Message) Delete(string archive, IReadOnlyList<string> entries, string password)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法编辑压缩包。");

            var args = new StringBuilder("d \"").Append(archive).Append('"');
            foreach (var entry in entries)
                args.Append(" \"").Append(entry).Append('"');
            AppendPassword(args, password);
            args.Append(" -y");

            return Execute(exe, args.ToString(), null);
        }

        public static (bool Ok, string Message) Rename(string archive, string oldPath, string newPath, string password)
        {
            var exe = Find7z();
            if (exe == null) return (false, "未找到 7z.exe 组件，无法编辑压缩包。");

            var args = new StringBuilder("rn \"").Append(archive).Append("\" \"")
                .Append(oldPath).Append("\" \"").Append(newPath).Append('"');
            AppendPassword(args, password);
            args.Append(" -y");

            return Execute(exe, args.ToString(), null);
        }

        private static void AppendPassword(StringBuilder args, string password)
        {
            if (!string.IsNullOrEmpty(password))
                args.Append(" -p\"").Append(password).Append('"');
        }

        private static (bool Ok, string Message) Execute(string exe, string arguments, string? workingDirectory)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                using var process = Process.Start(psi);
                if (process == null) return (false, "无法启动 7z.exe。");

                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();

                var ok = process.ExitCode == 0;
                var message = string.IsNullOrWhiteSpace(output)
                    ? (ok ? "已完成" : "操作失败")
                    : output.Trim();

                return (ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
