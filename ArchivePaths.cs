using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NovaZip.Engine;

namespace NovaZip
{
    /// <summary>解压目标文件夹处理与资源管理器定位的公共逻辑。</summary>
    internal static class ArchivePaths
    {
        private const uint GbkCodePage = 936;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] multiByteStr,
            int multiByteCount, [Out] char[] wideCharStr, int wideCharCount);

        /// <summary>在压缩包旁边生成同名文件夹，避免重名冲突。</summary>
        public static string BuildExtractFolder(string archivePath)
        {
            var dir = Path.GetDirectoryName(archivePath) ?? "";
            var name = Path.GetFileNameWithoutExtension(archivePath);
            var target = Path.Combine(dir, name);

            for (var i = 2; i < 1000 && Directory.Exists(target); i++)
            {
                var probe = Path.Combine(dir, name + " (" + i + ")");
                if (!Directory.Exists(probe)) return probe;
            }

            return target;
        }

        /// <summary>压缩包内所有条目是否共享同一个顶层目录。</summary>
        public static string? SingleRootFolder(ArchiveSession session)
        {
            string? root = null;

            foreach (var entry in session.Entries)
            {
                var path = entry.Path.Replace('/', '\\').Trim('\\');
                if (path.Length == 0) continue;

                var slash = path.IndexOf('\\');
                if (slash < 0)
                {
                    // 顶层目录条目（如 "资料包"）与同名根目录属于同一层；顶层文件则说明有散落条目
                    if (entry.IsDir) slash = path.Length;
                    else return null;
                }

                var first = path.Substring(0, slash);
                if (root == null) root = first;
                else if (!string.Equals(root, first, StringComparison.OrdinalIgnoreCase)) return null;
            }

            return root;
        }

        /// <summary>智能解压：当压缩包只有一层与目标同名的目录时，把内容上提一层。</summary>
        public static void FlattenSingleRoot(ArchiveSession session, string target)
        {
            try
            {
                var root = SingleRootFolder(session);
                if (string.IsNullOrEmpty(root)) return;
                if (!string.Equals(root, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)) return;

                var inner = Path.Combine(target, root);
                if (!Directory.Exists(inner)) return;

                foreach (var item in Directory.GetFileSystemEntries(inner))
                {
                    var destination = Path.Combine(target, Path.GetFileName(item));
                    if (Directory.Exists(item))
                    {
                        if (!Directory.Exists(destination)) Directory.Move(item, destination);
                    }
                    else if (!File.Exists(destination))
                    {
                        File.Move(item, destination);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(inner).Any())
                    Directory.Delete(inner, true);
            }
            catch
            {
                // 智能解压只是优化，失败不影响解压结果
            }
        }

        /// <summary>在资源管理器中定位文件或目录。</summary>
        public static void OpenInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch
            {
                // 忽略资源管理器启动失败
            }
        }

        /// <summary>
        /// 判断条目名称里是否存在疑似「GBK 被当作 Latin-1」的字符（U+0080–U+00FF）。
        /// 没有任何此类字符时无需扫描磁盘修复，可省去一次完整的目录遍历。
        /// </summary>
        public static bool MayNeedRepair(IEnumerable<string> entryPaths)
        {
            foreach (var path in entryPaths)
            {
                foreach (var ch in path)
                {
                    if (ch >= 0x80 && ch <= 0xFF) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 修复旧式 ZIP 以 GBK 存储、被内核按 Latin-1 解读造成的中文乱码文件名。
        /// 返回成功重命名的条目数量。
        /// </summary>
        public static int RepairLegacyNames(string target, CancellationToken token = default)
        {
            if (!Directory.Exists(target)) return 0;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories);
            }
            catch
            {
                return 0;
            }

            var repaired = 0;

            foreach (var file in entries.Where(File.Exists))
            {
                if (token.IsCancellationRequested) break;
                repaired += RenameIfMojibake(file, false);
            }

            // 目录从最深层开始改名，避免父目录改名后子路径失效
            var directories = entries
                .Where(Directory.Exists)
                .OrderByDescending(p => p.Count(c => c == '\\'))
                .ThenByDescending(p => p.Length);

            foreach (var dir in directories)
            {
                if (token.IsCancellationRequested) break;
                repaired += RenameIfMojibake(dir, true);
            }

            return repaired;
        }

        private static int RenameIfMojibake(string path, bool isDirectory)
        {
            var name = Path.GetFileName(path);
            var fixedName = TryDecodeMojibake(name);
            if (fixedName == null) return 0;

            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return 0;

            var destination = Path.Combine(parent, fixedName);
            if (string.Equals(destination, path, StringComparison.Ordinal)) return 0;

            try
            {
                if (File.Exists(destination) || Directory.Exists(destination)) return 0;

                if (isDirectory) Directory.Move(path, destination);
                else File.Move(path, destination);

                return 1;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>把疑似「GBK 字节被当作 Latin-1」的名称还原为中文；无法判定时返回 null。</summary>
        private static string? TryDecodeMojibake(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var bytes = new byte[name.Length];
            var hasHigh = false;

            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (ch > 0xFF) return null;
                if (ch >= 0x80) hasHigh = true;
                bytes[i] = (byte)ch;
            }

            if (!hasHigh) return null;

            var buffer = new char[bytes.Length * 2 + 2];
            var length = MultiByteToWideChar(GbkCodePage, 0, bytes, bytes.Length, buffer, buffer.Length);
            if (length <= 0) return null;

            var decoded = new string(buffer, 0, length);
            if (string.Equals(decoded, name, StringComparison.Ordinal)) return null;
            if (decoded.IndexOf('\uFFFD') >= 0) return null;
            if (decoded.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            // 必须确实还原出中文字符，避免把正常西文文件名改坏
            return decoded.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? decoded : null;
        }
    }
}
