using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaZip
{
    /// <summary>GitHub Releases 中发现的可用更新。</summary>
    internal sealed class UpdateInfo
    {
        public Version Version = new(0, 0);
        public string TagName = "";
        public string Notes = "";
        public string FileName = "";
        public string DownloadUrl = "";
        public string Sha256Url = "";
        public long Size;
    }

    /// <summary>
    /// 软件更新：检查 GitHub Releases、下载安装包并按 SHA-256 校验、退出后覆盖重启。
    /// 发布时需把 NovaZip-&lt;版本&gt;-win-x64.exe 与同名 .sha256 一起上传到 Releases。
    /// </summary>
    internal static class UpdateService
    {
        // 更新源仓库：Releases 需上传 NovaZip-<版本>-win-x64.exe 与同名 .sha256
        private const string RepoOwner = "xiao-danbao";
        private const string RepoName = "NovaZip";

        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

        public static string CurrentVersionText => CurrentVersion.ToString(3);

        public static bool IsConfigured =>
            !RepoOwner.Contains("TODO", StringComparison.OrdinalIgnoreCase) &&
            !RepoName.Contains("TODO", StringComparison.OrdinalIgnoreCase);

        /// <summary>查询最新版本；已是最新时返回 null。</summary>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("更新源尚未配置");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            request.Headers.UserAgent.ParseAdd("NovaZip-Updater");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"更新服务器返回 {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            var version = ParseVersion(tag);
            if (version <= CurrentVersion) return null;

            var assets = new Dictionary<string, (string Url, long Size)>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var assetArray))
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString() ?? "" : "";
                    var size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                    if (name.Length > 0 && url.Length > 0) assets[name] = (url, size);
                }
            }

            // 优先选择 win-x64 单文件安装包
            string? exeName = null;
            foreach (var name in assets.Keys)
            {
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (exeName == null) exeName = name;
                if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                {
                    exeName = name;
                    break;
                }
            }

            if (exeName == null)
                throw new InvalidOperationException("该版本没有可用的安装包");

            if (!assets.TryGetValue(exeName + ".sha256", out var shaAsset))
                throw new InvalidOperationException("该版本缺少 SHA-256 校验文件");

            return new UpdateInfo
            {
                Version = version,
                TagName = tag,
                Notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "",
                FileName = exeName,
                DownloadUrl = assets[exeName].Url,
                Size = assets[exeName].Size,
                Sha256Url = shaAsset.Url
            };
        }

        /// <summary>下载安装包并校验 SHA-256，返回本地文件路径。</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
            CancellationToken token = default)
        {
            var dir = Path.Combine(Path.GetTempPath(), "NovaZipUpdate");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, info.FileName);

            using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? info.Size;

                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total);
                }
            }

            try
            {
                await VerifyAsync(info, target, token);
            }
            catch
            {
                TryDelete(target);
                throw;
            }

            return target;
        }

        private static async Task VerifyAsync(UpdateInfo info, string path, CancellationToken token)
        {
            var expectedText = await Http.GetStringAsync(info.Sha256Url, token);
            var expected = ExtractHash(expectedText);
            if (expected.Length != 64)
                throw new InvalidOperationException("校验文件格式不正确");

            string actual;
            await using (var stream = File.OpenRead(path))
            {
                var hash = await SHA256.HashDataAsync(stream, token);
                actual = Convert.ToHexString(hash);
            }

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包校验失败，已中止更新");
        }

        /// <summary>从 “&lt;hash&gt;  NovaZip-x.y.z-win-x64.exe” 或纯 hash 中取出 64 位十六进制串。</summary>
        private static string ExtractHash(string text)
        {
            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 64 && IsHex(token)) return token;
            }

            return "";
        }

        private static bool IsHex(string value)
        {
            foreach (var ch in value)
            {
                if (!Uri.IsHexDigit(ch)) return false;
            }

            return true;
        }

        /// <summary>
        /// 生成等待本进程退出 → 覆盖 exe → 重新启动的批处理并执行。
        /// 批处理只含 ASCII 并通过参数传入路径，避免中文路径的编码问题。
        /// </summary>
        public static void ApplyAndRestart(string downloadedExe)
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current) || !File.Exists(current))
                throw new InvalidOperationException("无法定位当前程序文件");
            if (!File.Exists(downloadedExe))
                throw new InvalidOperationException("安装包不存在，请重新下载");

            var dir = Path.GetDirectoryName(downloadedExe) ?? Path.GetTempPath();
            var script = Path.Combine(dir, "apply-update.cmd");

            File.WriteAllText(script, """
@echo off
setlocal
set "TARGET=%~1"
set "SOURCE=%~2"
set "OLDPID=%~3"
:wait
tasklist /FI "PID eq %OLDPID%" 2>nul | find "%OLDPID%" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
for /l %%i in (1,1,30) do (
  copy /y "%SOURCE%" "%TARGET%" >nul 2>nul
  if not errorlevel 1 goto relaunch
  timeout /t 1 /nobreak >nul
)
:relaunch
start "" "%TARGET%"
timeout /t 3 /nobreak >nul
del "%SOURCE%" >nul 2>nul
(goto) 2>nul & del "%~f0"
""", new UTF8Encoding(false));

            var arguments = $"/c \"\"{script}\" \"{current}\" \"{downloadedExe}\" {Environment.ProcessId}\"";
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir
            });
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除失败时保留临时文件，不影响错误提示
            }
        }

        /// <summary>支持 v1.4.1 / 1.4.1-beta 等 tag，取前面的数字部分。</summary>
        private static Version ParseVersion(string text)
        {
            var builder = new StringBuilder();
            foreach (var ch in text.Trim().TrimStart('v', 'V'))
            {
                if (char.IsDigit(ch) || ch == '.') builder.Append(ch);
                else break;
            }

            return Version.TryParse(builder.ToString().Trim('.'), out var version) ? version : new Version(0, 0);
        }
    }
}
