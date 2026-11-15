using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NovaZip.Engine
{
    /// <summary>
    /// 用随程序分发的 7z.exe 完成压缩。
    /// 走子进程的原因：7z.dll 的回调要从内核线程回到托管代码，每个文件约 0.65ms 的固定开销，
    /// 上万个文件时这部分比压缩本身还贵；7z.exe 是原生回调，同等数据快 3 倍以上。
    /// </summary>
    internal static class CompressProcess
    {
        private static readonly Regex PercentRx = new(@"^\s*(\d{1,3})%", RegexOptions.Compiled);

        /// <summary>
        /// 尝试用 7z.exe 压缩。源路径无法安全映射成压缩包内路径时返回 null，由调用方回退到内核实现。
        /// </summary>
        public static OperationResult? TryCompress(IReadOnlyList<SevenZipEngine.SourceItem> sources, string outputPath,
            string password, int level, bool excludeJunk, ProgressState progress, CancellationToken token)
        {
            var exe = ArchiveEditor.Find7z();
            if (exe == null) return null;

            var root = FindCommonRoot(sources);
            if (root == null) return null;

            var format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
            if (format.Length == 0) return null;

            long totalSize = 0;
            var fileCount = 0;
            foreach (var source in sources)
            {
                if (source.IsDir) continue;
                totalSize += Math.Max(0, source.Size);
                fileCount++;
            }

            progress.Total = totalSize;
            progress.Completed = 0;
            progress.ItemCount = fileCount;
            progress.ItemIndex = 0;

            // 用 @列表文件传源路径：折叠后通常只有几行目录，同时也避开命令行长度上限
            var listFile = Path.Combine(Path.GetTempPath(), "novazip-" + Guid.NewGuid().ToString("N") + ".txt");
            var result = new OperationResult();

            try
            {
                var lines = CollapseToTopLevel(sources, root);
                if (EngineTiming.Enabled)
                    SevenZipEngine.LastPropertyStatus = "7z.exe 列表 " + lines.Count + " 项（源 " + sources.Count + "）";

                File.WriteAllLines(listFile, lines, new UTF8Encoding(false));

                if (File.Exists(outputPath))
                {
                    try { File.Delete(outputPath); } catch { /* 交给 7z.exe 报错 */ }
                }

                var args = new StringBuilder();
                args.Append("a \"").Append(outputPath).Append("\" @\"").Append(listFile).Append('"');
                args.Append(" -t").Append(format);
                args.Append(" -mx=").Append(level);
                // 用满所有核心：小文件受 I/O 限制不明显，但大文件/多块数据时线程上限会直接拖慢速度
                args.Append(" -mmt=").Append(Math.Max(1, Environment.ProcessorCount));
                if (format == "7z") args.Append(" -ms=on");
                if (!string.IsNullOrEmpty(password))
                {
                    args.Append(" -p\"").Append(password).Append('"');
                    if (format == "7z") args.Append(" -mhe=on");
                }

                // -bsp1 把百分比进度输出到 stdout，-bb1 带上文件名；-scsUTF-8 保证中文路径不乱码
                args.Append(" -scsUTF-8 -bsp1 -bb1 -y");

                // 折叠成目录后，源列表里已经滤掉的垃圾文件会被 7z.exe 重新扫进来，
                // 这里按同样的规则排除。7z.exe 没有「排除隐藏/系统属性」的开关，
                // 常见的 Thumbs.db / desktop.ini / 点开头文件 / ~$ 临时文件都能按名字覆盖。
                if (excludeJunk)
                {
                    args.Append(" -xr!Thumbs.db -xr!desktop.ini -xr!ehthumbs.db -xr!ehthumbs_vista.db");
                    args.Append(" -xr!.DS_Store -xr!$RECYCLE.BIN -xr!.* -xr!~$*");
                }

                var psi = new ProcessStartInfo(exe, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = root
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    result.Message = "无法启动 7z.exe。";
                    return result;
                }

                var childWatch = Stopwatch.StartNew();

                using var registration = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { /* 进程可能刚好退出 */ }
                });

                var tail = new StringBuilder();
                var buffer = new char[4096];
                var line = new StringBuilder(256);

                // 7z.exe 的进度行以 \r 刷新，按 \r 和 \n 分别断行
                while (true)
                {
                    var read = process.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    for (var i = 0; i < read; i++)
                    {
                        var ch = buffer[i];
                        if (ch == '\r' || ch == '\n')
                        {
                            if (line.Length > 0) ConsumeLine(line.ToString(), progress, totalSize);
                            line.Clear();
                            continue;
                        }

                        line.Append(ch);
                        if (line.Length > 512) line.Clear();
                    }
                }

                if (line.Length > 0) ConsumeLine(line.ToString(), progress, totalSize);

                tail.Append(process.StandardError.ReadToEnd());
                process.WaitForExit();
                childWatch.Stop();

                if (EngineTiming.Enabled)
                {
                    SevenZipEngine.LastPropertyStatus += " · 子进程 " + childWatch.ElapsedMilliseconds + "ms cpu " +
                        process.TotalProcessorTime.TotalMilliseconds.ToString("0") + "ms";
                }

                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    result.Message = "压缩已取消。";
                    TryDelete(outputPath);
                    return result;
                }

                if (process.ExitCode != 0)
                {
                    result.Message = string.IsNullOrWhiteSpace(tail.ToString())
                        ? "7z.exe 返回错误码 " + process.ExitCode
                        : FirstLine(tail.ToString());
                    TryDelete(outputPath);
                    return result;
                }

                if (!File.Exists(outputPath))
                {
                    result.Message = "压缩未生成文件。";
                    return result;
                }

                result.Success = true;
                result.Items = fileCount;
                progress.Completed = totalSize;
                result.Message = "已压缩 " + fileCount + " 个文件（" +
                                 FormatSize((ulong)new FileInfo(outputPath).Length) + "）";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                return result;
            }
            finally
            {
                try { File.Delete(listFile); } catch { /* 临时列表文件删不掉不影响结果 */ }
            }
        }

        private static void ConsumeLine(string text, ProgressState progress, long totalSize)
        {
            var match = PercentRx.Match(text);
            if (!match.Success) return;

            if (int.TryParse(match.Groups[1].Value, out var percent))
            {
                percent = Math.Clamp(percent, 0, 100);
                progress.Completed = totalSize * percent / 100;
            }

            var dash = text.IndexOf(" - ", match.Index + match.Length, StringComparison.Ordinal);
            if (dash >= 0)
            {
                var name = text.Substring(dash + 3).Trim();
                if (name.Length > 0) progress.CurrentFile = name;
            }
        }

        /// <summary>所有源文件必须满足 DiskPath = 根目录 + 压缩包内路径，否则压缩包内结构会变。</summary>
        private static string? FindCommonRoot(IReadOnlyList<SevenZipEngine.SourceItem> sources)
        {
            string? root = null;
            foreach (var source in sources)
            {
                var arc = source.ArcPath.Replace('/', '\\').TrimStart('\\');
                var disk = Path.GetFullPath(source.DiskPath).TrimEnd('\\', '/');
                if (arc.Length == 0 || !disk.EndsWith(arc, StringComparison.OrdinalIgnoreCase)) return null;

                var candidate = disk.Substring(0, disk.Length - arc.Length).TrimEnd('\\', '/');
                if (candidate.Length == 0) return null;
                if (root == null) root = candidate;
                else if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return null;
            }

            return root;
        }

        private static string RelativeTo(string root, string diskPath)
        {
            var full = Path.GetFullPath(diskPath);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length + 1);
            return Path.GetFileName(full.TrimEnd('\\', '/'));
        }

        /// <summary>
        /// 折叠成顶层项：被某个源目录覆盖的子项全部去掉。
        /// 7z.exe 对列表里的每条路径都要单独解析（实测 2000 条约 1.2s，而一条目录只要 0.37s），
        /// 所以上万个小文件时只喂目录，让 7z.exe 自己扫描，速度能回到原生水平。
        /// </summary>
        private static List<string> CollapseToTopLevel(IReadOnlyList<SevenZipEngine.SourceItem> sources, string root)
        {
            // 按路径长度升序：父目录一定排在子项之前，只需和已保留的目录比对一次
            var ordered = new List<SevenZipEngine.SourceItem>(sources);
            ordered.Sort((a, b) => a.DiskPath.Length.CompareTo(b.DiskPath.Length));

            var keptDirs = new List<string>();
            var result = new List<string>();
            foreach (var source in ordered)
            {
                var full = Path.GetFullPath(source.DiskPath).TrimEnd('\\', '/');

                var covered = false;
                foreach (var dir in keptDirs)
                {
                    if (full.Length > dir.Length &&
                        full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        covered = true;
                        break;
                    }
                }
                if (covered) continue;

                if (source.IsDir) keptDirs.Add(full);
                result.Add(RelativeTo(root, full));
            }

            return result;
        }

        private static string FirstLine(string text)
        {
            var idx = text.IndexOfAny(new[] { '\r', '\n' });
            return (idx < 0 ? text : text.Substring(0, idx)).Trim();
        }

        private static string FormatSize(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响结果 */ }
        }
    }
}
