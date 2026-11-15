using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NovaZip.Engine
{
    /// <summary>
    /// Headless verification of the 7-Zip kernel integration. Runs with: NovaZip.exe --selftest
    /// </summary>
    public static class SelfTest
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
                Info("NovaZip engine self-test @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Info("Step 1: load 7z.dll");
                Info("7z.dll: " + SevenZipEngine.DllPath);
                Info("Formats: " + SevenZipEngine.Formats.Count + " -> " +
                     string.Join(", ", SevenZipEngine.Formats.Take(12).Select(f => f.Name)));

                var root = Path.Combine(Path.GetTempPath(), "novazip-selftest-" + Guid.NewGuid().ToString("N"));
                var srcDir = Path.Combine(root, "src");
                var outDir = Path.Combine(root, "out");
                Directory.CreateDirectory(Path.Combine(srcDir, "素材", "嵌套"));
                Directory.CreateDirectory(outDir);

                var textFile = Path.Combine(srcDir, "说明.txt");
                File.WriteAllText(textFile, string.Join("\n", Enumerable.Range(1, 5000).Select(i => "NovaZip 第 " + i + " 行 · 现代解压工具")));
                var binFile = Path.Combine(srcDir, "素材", "random.bin");
                var rnd = new Random(42);
                var buffer = new byte[3 * 1024 * 1024];
                rnd.NextBytes(buffer);
                File.WriteAllBytes(binFile, buffer);
                File.WriteAllText(Path.Combine(srcDir, "素材", "嵌套", "deep.log"), string.Join("\n", Enumerable.Range(0, 20000).Select(i => "deep-" + i)));

                var sources = SevenZipEngine.CollectSources(new[] { srcDir });
                Info("Collected sources: " + sources.Count);
                Info("Step 2: create .7z via kernel");

                // --- create 7z ---
                var archive7z = Path.Combine(root, "bundle.7z");
                var progress = new ProgressState();
                var create = SevenZipEngine.Create(sources, archive7z, "", 5, progress, CancellationToken.None);
                if (create.Success && File.Exists(archive7z))
                    Pass("Create .7z (" + new FileInfo(archive7z).Length + " bytes) - " + create.Message);
                else
                {
                    Fail("Create .7z - " + create.Message);
                    var sevenZipExe = Path.Combine(AppContext.BaseDirectory, "runtime", "7z.exe");
                    if (File.Exists(sevenZipExe))
                    {
                        var psi = new ProcessStartInfo(sevenZipExe, "a \"" + archive7z + "\" \"" + srcDir + "\" -mx5")
                        { CreateNoWindow = true, UseShellExecute = false };
                        Process.Start(psi)?.WaitForExit();
                        if (File.Exists(archive7z)) Pass("Fallback CLI create .7z ok");
                    }
                }

                // --- open / list ---
                Info("Step 3: open archive");
                ArchiveSession? session = null;
                try
                {
                    session = SevenZipEngine.Open(archive7z);
                    var files = session.Entries.Where(e => !e.IsDir).ToList();
                    Info("Listed entries: " + session.Entries.Count + ", files: " + files.Count +
                         ", unpacked: " + session.UnpackedSize + ", format: " + session.Format.Name);
                    if (files.Count >= 3 && session.UnpackedSize >= 3 * 1024 * 1024)
                        Pass("List .7z entries & sizes");
                    else
                        Fail("List .7z entries & sizes (count=" + files.Count + ")");

                    // --- extract all ---
                    Info("Step 4: extract all");
                    var p2 = new ProgressState();
                    var extract = SevenZipEngine.Extract(session, null, Path.Combine(outDir, "all"), p2, CancellationToken.None);
                    if (extract.Success)
                        Pass("Extract .7z - " + extract.Message);
                    else
                        Fail("Extract .7z - " + extract.Message);

                    var extractedBin = Path.Combine(outDir, "all", "src", "素材", "random.bin");
                    if (!File.Exists(extractedBin))
                    {
                        // archive root naming may differ; search by name
                        var found = Directory.GetFiles(Path.Combine(outDir, "all"), "random.bin", SearchOption.AllDirectories);
                        extractedBin = found.FirstOrDefault() ?? extractedBin;
                    }

                    if (File.Exists(extractedBin) && Sha256(extractedBin) == Sha256(binFile))
                        Pass("Extracted binary hash matches (unicode paths ok)");
                    else
                        Fail("Extracted binary hash mismatch: " + extractedBin);

                    // --- test mode ---
                    var test = SevenZipEngine.Test(session, new ProgressState(), CancellationToken.None);
                    if (test.Success) Pass("Test command - " + test.Message);
                    else Fail("Test command - " + test.Message);
                }
                catch (Exception ex)
                {
                    Fail("Open/List/Extract pipeline", ex);
                }
                finally
                {
                    session?.Dispose();
                }

                // --- zip roundtrip ---
                try
                {
                    var zipPath = Path.Combine(root, "bundle.zip");
                    var zCreate = SevenZipEngine.Create(sources, zipPath, "", 5, new ProgressState(), CancellationToken.None);
                    if (!zCreate.Success) Fail("Create .zip - " + zCreate.Message);
                    else
                    {
                        using var zSession = SevenZipEngine.Open(zipPath);
                        var zOut = Path.Combine(outDir, "zip");
                        var zExtract = SevenZipEngine.Extract(zSession, null, zOut, new ProgressState(), CancellationToken.None);
                        if (zExtract.Success) Pass("Zip roundtrip - " + zSession.Entries.Count + " entries");
                        else Fail("Zip extract - " + zExtract.Message);
                    }
                }
                catch (Exception ex)
                {
                    Fail("Zip roundtrip", ex);
                }

                // --- password archive ---
                try
                {
                    var encPath = Path.Combine(root, "secret.7z");
                    var encCreate = SevenZipEngine.Create(sources, encPath, "p@ss123", 3, new ProgressState(), CancellationToken.None);
                    if (!encCreate.Success) Fail("Create encrypted .7z - " + encCreate.Message);
                    else
                    {
                        try
                        {
                            using var wrong = SevenZipEngine.Open(encPath, "bad-password");
                            wrong.Entries.ToList();
                            Info("Open with wrong password unexpectedly succeeded (header not encrypted?)");
                        }
                        catch (Exception ex)
                        {
                            Info("Wrong password blocked as expected: " + ex.Message.Split('\n')[0]);
                        }

                        using var right = SevenZipEngine.Open(encPath, "p@ss123");
                        var encOut = Path.Combine(outDir, "secret");
                        var encExtract = SevenZipEngine.Extract(right, null, encOut, new ProgressState(), CancellationToken.None);
                        if (encExtract.Success) Pass("Encrypted .7z extract with password");
                        else Fail("Encrypted .7z extract - " + encExtract.Message);
                    }
                }
                catch (Exception ex)
                {
                    Fail("Encrypted archive pipeline", ex);
                }

                // --- cancellation ---
                try
                {
                    using var s = SevenZipEngine.Open(archive7z);
                    using var cts = new CancellationTokenSource();
                    cts.Cancel();
                    var cancelled = SevenZipEngine.Extract(s, null, Path.Combine(outDir, "cancel"), new ProgressState(), cts.Token);
                    if (cancelled.Cancelled) Pass("Cancellation respected");
                    else Fail("Cancellation not respected");
                }
                catch (Exception ex)
                {
                    Fail("Cancellation test", ex);
                }

                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                Fail("Fatal self-test error", ex);
            }

            log.AppendLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            }
            catch { /* ignore */ }

            return failures;
        }

        private static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
    }
}
