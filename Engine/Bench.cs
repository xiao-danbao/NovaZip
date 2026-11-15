using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NovaZip.Engine
{
    /// <summary>基准测试时才开启的耗时统计，正常使用时所有分支只读一个静态 bool，开销可忽略。</summary>
    internal static class EngineTiming
    {
        public static volatile bool Enabled;
        public static long WriteTicks;
        public static long WriteBytes;
        public static long WriteCalls;
        public static long ReadBytes;
        public static long ReadCalls;
        public static long ReadTicks;
        public static long CallbackTicks;
        public static long CompletedCalls;
        public static long PropCalls;
        public static long PropTicks;
        public static long StreamCalls;
        public static long StreamTicks;
        public static long ResultCalls;
        public static long CloseTicks;
        public static long PreallocateTicks;
        public static long DirTicks;
        public static long DirCalls;
        public static long FileOpenTicks;
        public static long FileOpenCalls;
        public static long TimeTicks;

        public static void Reset()
        {
            WriteTicks = 0;
            WriteBytes = 0;
            WriteCalls = 0;
            ReadBytes = 0;
            ReadCalls = 0;
            ReadTicks = 0;
            CallbackTicks = 0;
            CompletedCalls = 0;
            PropCalls = 0;
            PropTicks = 0;
            StreamCalls = 0;
            StreamTicks = 0;
            ResultCalls = 0;
            CloseTicks = 0;
            PreallocateTicks = 0;
            DirTicks = 0;
            DirCalls = 0;
            FileOpenTicks = 0;
            FileOpenCalls = 0;
            TimeTicks = 0;
        }

        public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>无界面的性能基准：解压与压缩各阶段耗时、吞吐、CPU 占比。</summary>
    internal static class Bench
    {
        public static int RunExtract(string archivePath, string? outFile)
        {
            var log = new StringBuilder();
            var target = Path.Combine(Path.GetTempPath(), "NovaZipBench", Guid.NewGuid().ToString("N"));
            var ok = false;

            EngineTiming.Reset();
            EngineTiming.Enabled = true;

            var sw = Stopwatch.StartNew();
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var openMs = 0.0;

            try
            {
                using var session = SevenZipEngine.Open(archivePath, "");
                openMs = sw.Elapsed.TotalMilliseconds;

                var progress = new ProgressState();
                var result = SevenZipEngine.Extract(session, null, target, progress, CancellationToken.None);
                sw.Stop();

                var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
                var totalMs = Math.Max(1.0, sw.Elapsed.TotalMilliseconds);
                var writeMs = EngineTiming.ToMs(EngineTiming.WriteTicks);
                var closeMs = EngineTiming.ToMs(EngineTiming.CloseTicks);
                var preallocMs = EngineTiming.ToMs(EngineTiming.PreallocateTicks);
                var dirMs = EngineTiming.ToMs(EngineTiming.DirTicks);
                var openFileMs = EngineTiming.ToMs(EngineTiming.FileOpenTicks);
                var timeMs = EngineTiming.ToMs(EngineTiming.TimeTicks);
                var unpacked = (long)Math.Min(session.UnpackedSize, long.MaxValue);

                log.AppendLine("NovaZip Bench · " + SevenZipEngine.EngineVersion);
                log.AppendLine("archive       : " + archivePath);
                log.AppendLine("archive size  : " + FormatBytes(new FileInfo(archivePath).Length));
                log.AppendLine("entries       : " + session.Entries.Count);
                log.AppendLine("unpacked      : " + FormatBytes(unpacked));
                log.AppendLine("open ms       : " + openMs.ToString("0"));
                log.AppendLine("extract ms    : " + totalMs.ToString("0"));
                log.AppendLine("throughput    : " + MbPerSecond(unpacked, totalMs) + " MB/s");
                log.AppendLine("cpu ms        : " + cpu.TotalMilliseconds.ToString("0") +
                               " · 平均占用 " + (cpu.TotalMilliseconds / totalMs * 100).ToString("0") + "% 单核");
                log.AppendLine("write ms      : " + writeMs.ToString("0") + " (" + Percent(writeMs, totalMs) + "%)");
                log.AppendLine("close ms      : " + closeMs.ToString("0") + " (" + Percent(closeMs, totalMs) + "%)");
                log.AppendLine("prealloc ms   : " + preallocMs.ToString("0") + " (" + Percent(preallocMs, totalMs) + "%)");
                log.AppendLine("dircreate ms  : " + dirMs.ToString("0") + " (" + Percent(dirMs, totalMs) +
                               "%) · " + EngineTiming.DirCalls + " 次");
                log.AppendLine("fileopen ms   : " + openFileMs.ToString("0") + " (" + Percent(openFileMs, totalMs) +
                               "%) · " + EngineTiming.FileOpenCalls + " 次");
                log.AppendLine("timestamp ms  : " + timeMs.ToString("0") + " (" + Percent(timeMs, totalMs) + "%)");
                log.AppendLine("write calls   : " + EngineTiming.WriteCalls + " · 平均 " +
                               (EngineTiming.WriteBytes / Math.Max(1.0, EngineTiming.WriteCalls) / 1024).ToString("0.0") + " KB");

                var writtenFiles = 0;
                long writtenBytes = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                    {
                        writtenFiles++;
                        writtenBytes += new FileInfo(file).Length;
                    }
                }
                catch
                {
                    // 统计失败不影响基准结果
                }

                log.AppendLine("written       : " + writtenFiles + " 个文件 · " + FormatBytes(writtenBytes));
                log.AppendLine("result        : " + (result.Success ? "OK" : "FAIL " + FirstLine(result.Message)));
                ok = result.Success;
            }
            catch (Exception ex)
            {
                sw.Stop();
                log.AppendLine("error         : " + ex.Message);
            }
            finally
            {
                EngineTiming.Enabled = false;
            }

            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }

            Write(log, outFile);
            return ok ? 0 : 1;
        }

        public static int RunCreate(string outputArchive, IReadOnlyList<string> sourcePaths, string? outFile)
        {
            var log = new StringBuilder();
            var ok = false;

            EngineTiming.Reset();
            EngineTiming.Enabled = true;

            var sw = Stopwatch.StartNew();
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;

            try
            {
                var collectWatch = Stopwatch.StartNew();
                var sources = SevenZipEngine.CollectSources(sourcePaths);
                collectWatch.Stop();
                var inputBytes = sources.Where(s => !s.IsDir).Sum(s => s.Size);
                var files = sources.Count(s => !s.IsDir);

                var progress = new ProgressState();
                var engineWatch = Stopwatch.StartNew();
                // 与界面一致：优先 7z.exe 子进程，无法映射时回退内核
                var result = CompressProcess.TryCompress(sources, outputArchive, "", 1, false, progress, CancellationToken.None)
                             ?? SevenZipEngine.Create(sources, outputArchive, "", 1, progress, CancellationToken.None);
                engineWatch.Stop();
                sw.Stop();

                var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
                var totalMs = Math.Max(1.0, sw.Elapsed.TotalMilliseconds);
                var outSize = File.Exists(outputArchive) ? new FileInfo(outputArchive).Length : 0;

                log.AppendLine("NovaZip Bench (create) · " + SevenZipEngine.EngineVersion);
                log.AppendLine("output        : " + outputArchive);
                log.AppendLine("files         : " + files + " · 输入 " + FormatBytes(inputBytes));
                log.AppendLine("output size   : " + FormatBytes(outSize));
                log.AppendLine("create ms     : " + totalMs.ToString("0"));
                log.AppendLine("collect ms    : " + collectWatch.ElapsedMilliseconds +
                               " · engine ms " + engineWatch.ElapsedMilliseconds);
                log.AppendLine("throughput    : " + MbPerSecond(inputBytes, totalMs) + " MB/s (输入)");
                log.AppendLine("cpu ms        : " + cpu.TotalMilliseconds.ToString("0") +
                               " · 平均占用 " + (cpu.TotalMilliseconds / totalMs * 100).ToString("0") + "% 单核");
                var format = SevenZipEngine.FindCreateFormat(Path.GetExtension(outputArchive));
                log.AppendLine("format        : " + (format == null ? "(none)" : format.Name + " / " + format.ClassId));
                log.AppendLine("props         : " + SevenZipEngine.LastPropertyStatus);
                log.AppendLine("read calls    : " + EngineTiming.ReadCalls + " · 平均 " +
                               (EngineTiming.ReadBytes / Math.Max(1.0, EngineTiming.ReadCalls) / 1024).ToString("0.0") + " KB");
                log.AppendLine("write calls   : " + EngineTiming.WriteCalls + " · 平均 " +
                               (EngineTiming.WriteBytes / Math.Max(1.0, EngineTiming.WriteCalls) / 1024).ToString("0.0") + " KB");
                log.AppendLine("read ms       : " + EngineTiming.ToMs(EngineTiming.ReadTicks).ToString("0") +
                               " · write ms " + EngineTiming.ToMs(EngineTiming.WriteTicks).ToString("0") +
                               " · callback ms " + EngineTiming.ToMs(EngineTiming.CallbackTicks).ToString("0") +
                               " · SetCompleted " + EngineTiming.CompletedCalls + " 次");
                log.AppendLine("cb detail     : prop " + EngineTiming.ToMs(EngineTiming.PropTicks).ToString("0") + "ms/" +
                               EngineTiming.PropCalls + " · stream " + EngineTiming.ToMs(EngineTiming.StreamTicks).ToString("0") + "ms/" +
                               EngineTiming.StreamCalls + " · result " + EngineTiming.ResultCalls + " 次");
                log.AppendLine("result        : " + (result.Success ? "OK" : "FAIL " + FirstLine(result.Message)));
                ok = result.Success;
            }
            catch (Exception ex)
            {
                sw.Stop();
                log.AppendLine("error         : " + ex.Message);
            }
            finally
            {
                EngineTiming.Enabled = false;
            }

            Write(log, outFile);
            return ok ? 0 : 1;
        }

        private static void Write(StringBuilder log, string? outFile)
        {
            var path = outFile ?? Path.Combine(Path.GetTempPath(), "NovaZip-bench.log");
            try
            {
                File.AppendAllText(path, log.ToString() + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去时仍然返回执行结果
            }
        }

        private static string Percent(double part, double total) =>
            (part / Math.Max(1.0, total) * 100).ToString("0.0");

        private static string MbPerSecond(long bytes, double ms) =>
            (bytes / 1048576.0 / Math.Max(0.001, ms / 1000.0)).ToString("0.0");

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "未知错误";
            var idx = text.IndexOf('\n');
            return (idx < 0 ? text : text.Substring(0, idx)).Trim();
        }
    }
}
