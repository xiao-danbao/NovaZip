using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NovaZip.Engine;

namespace NovaZip
{
    /// <summary>
    /// Headless verification of extract resumability (file-level skip, byte-level
    /// .part continuation, disk-budget batching, cross-session restore).
    /// Runs with: NovaZip.exe --resumetest
    /// </summary>
    public static class ResumeTest
    {
        public static int Run(string logPath)
        {
            var log = new StringBuilder();
            var failures = 0;

            void Flush(string line)
            {
                log.AppendLine(line);
                try
                {
                    var dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* ignore */ }
            }

            void Info(string msg) => Flush(msg);
            void Pass(string msg) => Flush("[PASS] " + msg);
            void Fail(string msg, Exception? ex = null)
            {
                failures++;
                Flush("[FAIL] " + msg + (ex != null ? " -> " + ex.GetType().Name + ": " + ex.Message : ""));
            }

            try
            {
                try { File.Delete(logPath); } catch { /* ignore */ }
                SevenZipEngine.Trace = Flush;
                Info("NovaZip resume test @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Info("7z.dll: " + SevenZipEngine.DllPath);

                var root = Path.Combine(Path.GetTempPath(), "novazip-resumetest-" + Guid.NewGuid().ToString("N"));
                var srcDir = Path.Combine(root, "src");
                Directory.CreateDirectory(srcDir);

                // 进度数据写入临时目录：测试隔离，不碰真实应用数据
                ExtractCoordinator.ProgressDirOverride = Path.Combine(root, "progress");

                // 文件名让小文件排在大文件前面，先完成文件级批次再进入分批大文件
                var rnd = new Random(7);
                var big = new byte[120 * 1024 * 1024];
                rnd.NextBytes(big);
                File.WriteAllBytes(Path.Combine(srcDir, "z-big.bin"), big);
                File.WriteAllText(Path.Combine(srcDir, "a-small1.txt"),
                    string.Join("\n", Enumerable.Range(0, 40000).Select(i => "line-" + i)));
                File.WriteAllText(Path.Combine(srcDir, "a-small2.txt"),
                    string.Join("\n", Enumerable.Range(0, 40000).Select(i => "row-" + i)));

                Info("Step 1: create .7z (store level, 120MB payload)");
                var archive = Path.Combine(root, "test.7z");
                var create = SevenZipEngine.Create(
                    SevenZipEngine.CollectSources(new[] { srcDir }), archive, "", 0,
                    new ProgressState(), CancellationToken.None);
                if (!create.Success || !File.Exists(archive))
                {
                    Fail("Create .7z - " + create.Message);
                    return 1;
                }
                Pass("Create .7z (" + new FileInfo(archive).Length / (1024 * 1024) + "MB)");

                Info("Step 2: extract via coordinator with 8MB budget, resume after each pause");
                var target = Path.Combine(root, "out");
                Directory.CreateDirectory(target);

                var coordinator = ExtractCoordinator.Create(archive);
                coordinator.ForceBudgetBytes = 8L * 1024 * 1024;

                var pauses = 0;
                var maxPartAtPause = 0L;
                var doneAtFirstPause = -1;
                coordinator.DiskPaused += () =>
                {
                    pauses++;
                    var record = coordinator.TestRecord;
                    if (record != null)
                    {
                        if (record.PartialIndex >= 0 && record.PartialBytes > 0)
                        {
                            maxPartAtPause = Math.Max(maxPartAtPause, record.PartialBytes);
                            Info("pause #" + pauses + ": partial " + record.PartialIndex +
                                 " @" + record.PartialBytes / (1024 * 1024) + "MB");
                        }
                        if (doneAtFirstPause < 0) doneAtFirstPause = record.DoneIndices.Count;
                    }

                    // 模拟用户清理磁盘后点「继续」
                    coordinator.RequestResume();
                };

                OperationResult result;
                using (var session = SevenZipEngine.Open(archive))
                {
                    coordinator.Initialize(target, session.Entries);
                    result = coordinator.Run(session, new ProgressState(), CancellationToken.None, null);
                }

                if (result.Success) Pass("Phase1 extract - " + result.Message);
                else Fail("Phase1 extract - " + result.Message);
                foreach (var err in result.ErrorItems) Info("phase1 error: " + err);

                if (pauses >= 5 && maxPartAtPause >= 112L * 1024 * 1024)
                    Pass("Phase1 paused " + pauses + " time(s), .part grew to " +
                         maxPartAtPause / (1024 * 1024) + "MB before final resume");
                else
                    Fail("Phase1 disk batching (pauses=" + pauses + ", maxPart=" + maxPartAtPause + ")");

                if (doneAtFirstPause >= 2)
                    Pass("Phase1 file-level skip: " + doneAtFirstPause + " small file(s) done before big file");
                else
                    Fail("Phase1 file-level skip (done=" + doneAtFirstPause + ")");

                VerifyOutput(root, target, "z-big.bin", big, Pass, m => Fail(m));

                if (!ExtractCoordinator.HasRecordFile(archive))
                    Pass("Phase1 record deleted after completion");
                else
                    Fail("Phase1 record still present after completion");

                // ---------- 阶段 2：中断并保留断点，再模拟重启后从记录恢复 ----------
                Info("Step 3: interrupt after first pause, keep record, then restore from it");
                var target2 = Path.Combine(root, "out2");
                Directory.CreateDirectory(target2);

                var coordinator2 = ExtractCoordinator.Create(archive);
                coordinator2.ForceBudgetBytes = 8L * 1024 * 1024;
                var firstPauseSeen = false;
                coordinator2.DiskPaused += () =>
                {
                    if (firstPauseSeen)
                    {
                        coordinator2.RequestResume();
                        return;
                    }
                    firstPauseSeen = true;
                    // 模拟进程在磁盘暂停期间被关闭/崩溃：直接取消
                };

                OperationResult result2;
                using (var session = SevenZipEngine.Open(archive))
                {
                    coordinator2.Initialize(target2, session.Entries);
                    var cts = new CancellationTokenSource();
                    var worker = new Thread(() =>
                    {
                        Thread.Sleep(400);
                        cts.Cancel();
                    });
                    worker.Start();
                    result2 = coordinator2.Run(session, new ProgressState(), cts.Token, null);
                    worker.Join();
                }

                if (result2.Cancelled && firstPauseSeen)
                    Pass("Phase2 interrupted at disk pause as expected");
                else
                    Fail("Phase2 interrupt (cancelled=" + result2.Cancelled + ", paused=" + firstPauseSeen + ")");

                Info("phase2 recordFileExists=" + File.Exists(ExtractCoordinator.TestRecordFileFor(archive)));
                if (ExtractCoordinator.HasRecordFile(archive))
                {
                    var record = coordinator2.TestRecord;
                    if (record != null && record.PartialIndex >= 0 && record.PartialBytes > 0 &&
                        record.DoneIndices.Count >= 2)
                        Pass("Phase2 record kept with " + record.DoneIndices.Count +
                             " done file(s) and partial @" + record.PartialBytes / (1024 * 1024) + "MB");
                    else if (record != null)
                        Fail("Phase2 record incomplete (done=" + record.DoneIndices.Count +
                             ", partial=" + record.PartialIndex + ")");
                }
                else
                {
                    Fail("Phase2 record missing after interruption");
                }

                // ---------- 阶段 3：重启后 Begin() 恢复记录并完成 ----------
                var coordinator3 = ExtractCoordinator.Begin(archive);
                if (coordinator3 == null)
                {
                    Fail("Phase3 Begin() returned null");
                }
                else
                {
                    OperationResult result3;
                    using (var session = SevenZipEngine.Open(archive))
                    {
                        coordinator3.Initialize(target2, session.Entries);
                        // 真实磁盘预算充足：不暂停，一次性完成剩余部分
                        result3 = coordinator3.Run(session, new ProgressState(), CancellationToken.None, null);
                    }

                    if (result3.Success) Pass("Phase3 restored extract - " + result3.Message);
                    else Fail("Phase3 restored extract - " + result3.Message);

                    VerifyOutput(root, target2, "z-big.bin", big, Pass, m => Fail(m));

                    if (!ExtractCoordinator.HasRecordFile(archive))
                        Pass("Phase3 record deleted after completion");
                    else
                        Fail("Phase3 record still present after completion");

                    var parts3 = Directory.GetFiles(target2, "*.novapart", SearchOption.AllDirectories);
                    if (parts3.Length == 0) Pass("Phase3 no .novapart leftovers");
                    else Fail("Phase3 .novapart leftovers: " + parts3.Length);
                }

                try { Directory.Delete(root, true); } catch { /* ignore */ }

                Info(failures == 0 ? "ALL RESUME TESTS PASSED" : "RESUME TEST FAILURES: " + failures);
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Fail("Unhandled", ex);
                return 1;
            }
        }

        private static void VerifyOutput(string root, string target, string fileName, byte[] expected,
            Action<string> pass, Action<string> fail)
        {
            var found = Directory.GetFiles(target, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (found == null)
            {
                fail(fileName + " missing in " + Path.GetFileName(target));
                return;
            }

            var hashA = SHA256.HashData(File.ReadAllBytes(found));
            var hashB = SHA256.HashData(expected);
            if (hashA.SequenceEqual(hashB)) pass(fileName + " hash matches in " + Path.GetFileName(target));
            else fail(fileName + " hash mismatch in " + Path.GetFileName(target));
        }
    }
}
