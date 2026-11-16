using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NovaZip.Engine
{
    public sealed class FormatInfo
    {
        public string Name = "";
        public Guid ClassId;
        public string[] Extensions = Array.Empty<string>();
        public bool CanUpdate;

        public bool MatchesExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.TrimStart('.').ToLowerInvariant();
            return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class ArchiveEntry
    {
        public string Path = "";
        public string Name = "";
        public bool IsDir;
        public ulong Size;
        public ulong PackSize;
        public DateTime Modified;
        public uint Attrib;
        public bool Encrypted;
        public uint Crc;

        public string Directory
        {
            get
            {
                var idx = Path.LastIndexOfAny(new[] { '\\', '/' });
                return idx > 0 ? Path.Substring(0, idx) : "";
            }
        }

        public string Extension
        {
            get
            {
                var idx = Name.LastIndexOf('.');
                return idx > 0 ? Name.Substring(idx + 1).ToLowerInvariant() : "";
            }
        }
    }

    public sealed class ProgressState
    {
        public long Completed;
        public long Total;
        public long CurrentItemBytes;
        public string CurrentFile = "";
        public int ItemIndex;
        public int ItemCount;
        public int LastResult;
        public volatile bool Cancelled;

        /// <summary>断点续传：已完成批次（含大文件已写偏移）的累计字节，由协调器设置。</summary>
        public long BaseCompleted;

        /// <summary>大于 0 时 SetTotal 不覆盖 Total，保证跨批次进度条总量稳定。</summary>
        public long FixedTotal;
    }

    public sealed class OperationResult
    {
        public bool Success;
        public bool Cancelled;
        public bool WrongPassword;
        public string Message = "";
        public int Items;
        public int Errors;
        public List<string> ErrorItems { get; } = new();
    }

    public sealed class ArchiveSession : IDisposable
    {
        internal IInArchive Archive = null!;
        internal FileInStream Stream = null!;
        internal object ComObject = null!;
        internal List<ArchiveEntry> EntryList = new();

        public string FilePath = "";
        public FormatInfo Format = new();
        public string Password = "";
        public IReadOnlyList<ArchiveEntry> Entries => EntryList;
        public ulong UnpackedSize { get; internal set; }
        public ulong PackedSize { get; internal set; }
        public bool HasEncrypted { get; internal set; }

        public void Close()
        {
            try { Archive?.Close(); } catch { /* ignore */ }
            try { Stream?.Close(); } catch { /* ignore */ }
            if (ComObject != null)
            {
                try { Marshal.FinalReleaseComObject(ComObject); } catch { /* ignore */ }
                ComObject = null!;
            }
            Archive = null!;
        }

        public void Dispose() => Close();
    }

    public static class SevenZipEngine
    {
        public static Action<string>? Trace;

        /// <summary>最近一次压缩属性设置的结果，供基准测试诊断使用。</summary>
        public static string LastPropertyStatus = "";

        private static readonly object Sync = new();
        private static bool _initialized;
        private static string? _dllPath;
        private static Native.CreateObjectDelegate? _createObject;
        private static Native.GetNumberOfFormatsDelegate? _getNumberOfFormats;
        private static Native.GetHandlerProperty2Delegate? _getHandlerProperty2;
        private static Native.GetHandlerProperty2Delegate? _getHandlerProperty;
        private static List<FormatInfo>? _formats;

        public static string DllPath => _dllPath ?? "";

        public static string EngineVersion => "7-Zip 26.06 (x64)";

        public static IReadOnlyList<FormatInfo> Formats
        {
            get
            {
                EnsureInitialized();
                return _formats!;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (Sync)
            {
                if (_initialized) return;

                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "7z.dll"),
                    Path.Combine(AppContext.BaseDirectory, "runtime", "7z.dll"),
                    @"C:\Program Files\7-Zip\7z.dll"
                };

                _dllPath = candidates.FirstOrDefault(File.Exists);
                if (_dllPath == null)
                    throw new FileNotFoundException("未找到 7z.dll 内核文件，请确认程序目录完整。");

                var module = Native.LoadLibraryW(_dllPath);
                if (module == IntPtr.Zero)
                    throw new InvalidOperationException("无法加载 7z.dll 内核 (错误码 " + Marshal.GetLastWin32Error() + ")");

                var createPtr = Native.GetProcAddress(module, "CreateObject");
                if (createPtr == IntPtr.Zero)
                    throw new InvalidOperationException("7z.dll 缺少 CreateObject 导出函数。");
                _createObject = Marshal.GetDelegateForFunctionPointer<Native.CreateObjectDelegate>(createPtr);

                var numPtr = Native.GetProcAddress(module, "GetNumberOfFormats");
                if (numPtr != IntPtr.Zero)
                    _getNumberOfFormats = Marshal.GetDelegateForFunctionPointer<Native.GetNumberOfFormatsDelegate>(numPtr);

                var propPtr = Native.GetProcAddress(module, "GetHandlerProperty2");
                if (propPtr != IntPtr.Zero)
                    _getHandlerProperty2 = Marshal.GetDelegateForFunctionPointer<Native.GetHandlerProperty2Delegate>(propPtr);

                var prop1Ptr = Native.GetProcAddress(module, "GetHandlerProperty");
                if (prop1Ptr != IntPtr.Zero)
                    _getHandlerProperty = Marshal.GetDelegateForFunctionPointer<Native.GetHandlerProperty2Delegate>(prop1Ptr);

                _formats = LoadFormats();
                _initialized = true;
            }
        }

        private static List<FormatInfo> LoadFormats()
        {
            var list = new List<FormatInfo>();
            if (_getNumberOfFormats == null || _getHandlerProperty2 == null) return list;

            if (_getNumberOfFormats(out var count) != Native.S_OK) return list;

            for (uint i = 0; i < count; i++)
            {
                var info = new FormatInfo();
                var name = GetHandlerString(i, HandlerPropIds.Name);
                if (name != null) info.Name = name;

                var ext = GetHandlerString(i, HandlerPropIds.Extension);
                if (!string.IsNullOrWhiteSpace(ext))
                    info.Extensions = ext.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var classIdPtr = GetHandlerStringPtr(i, HandlerPropIds.ClassId);
                if (classIdPtr != IntPtr.Zero)
                {
                    var bytes = new byte[16];
                    Marshal.Copy(classIdPtr, bytes, 0, 16);
                    info.ClassId = new Guid(bytes);
                }

                if (_getHandlerProperty2(i, HandlerPropIds.Update, out var pv) == Native.S_OK)
                {
                    info.CanUpdate = pv.VarType == VarTypes.VT_BOOL && pv.BoolValue != 0;
                    Native.PropVariantClear(ref pv);
                }

                if (info.ClassId != Guid.Empty && !string.IsNullOrEmpty(info.Name))
                    list.Add(info);
            }

            return list;
        }

        private static string? GetHandlerString(uint index, uint propId)
        {
            var ptr = GetHandlerStringPtr(index, propId);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(ptr);
        }

        private static IntPtr GetHandlerStringPtr(uint index, uint propId)
        {
            var fn = _getHandlerProperty2 ?? _getHandlerProperty;
            if (fn == null) return IntPtr.Zero;
            if (fn(index, propId, out var pv) != Native.S_OK) return IntPtr.Zero;
            if (pv.VarType != VarTypes.VT_BSTR)
            {
                Native.PropVariantClear(ref pv);
                return IntPtr.Zero;
            }
            var ptr = pv.PointerValue;
            pv.VarType = VarTypes.VT_EMPTY;
            pv.PointerValue = IntPtr.Zero;
            return ptr;
        }

        private static object CreateComObject(Guid classId, Guid interfaceId)
        {
            EnsureInitialized();
            var hr = _createObject!(ref classId, ref interfaceId, out var obj);
            if (hr != Native.S_OK || obj == null)
                throw new COMException("创建 7-Zip 处理程序失败 (HRESULT 0x" + hr.ToString("X8") + ")", hr);
            return obj;
        }

        private static readonly string[] PopularOrder =
        {
            "7z", "zip", "rar", "tar", "gz", "xz", "bz2", "zst", "cab", "iso", "wim", "arj", "lzh",
            "z", "cpio", "rpm", "deb", "chm", "msi", "nsis", "udf", "dmg", "vhdx", "vhd", "squashfs",
            "ext", "apfs", "lzma", "ppmd", "br", "lz4", "rar5", "zipx", "jar", "apk", "docx", "xlsx"
        };

        private static IEnumerable<FormatInfo> OrderedFormats(string? extension)
        {
            var all = Formats.ToList();
            var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();

            var result = new List<FormatInfo>();
            if (ext.Length > 0)
                result.AddRange(all.Where(f => f.MatchesExtension(ext)));

            foreach (var name in PopularOrder)
            {
                var match = all.FirstOrDefault(f => f.MatchesExtension(name) && !result.Contains(f));
                if (match != null) result.Add(match);
            }

            result.AddRange(all.Where(f => !result.Contains(f)));
            return result;
        }

        public static ArchiveSession Open(string filePath, string password = "")
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("文件不存在：" + filePath);

            var fullPath = Path.GetFullPath(filePath);
            var extension = Path.GetExtension(fullPath);
            var failures = new List<string>();

            foreach (var format in OrderedFormats(extension))
            {
                if (format.Name is "Exe" or "Compound" && extension.Length > 0 &&
                    !format.MatchesExtension(extension) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Executable / OLE handlers are only used when nothing else matched.
                }

                object comObject;
                try
                {
                    comObject = CreateComObject(format.ClassId, InterfaceIds.IInArchive);
                }
                catch
                {
                    continue;
                }

                var archive = (IInArchive)comObject;
                var stream = new FileInStream(fullPath);
                var callback = new ArchiveOpenCallback(password);

                int hr;
                try
                {
                    hr = archive.Open(stream, IntPtr.Zero, callback);
                    Trace?.Invoke("[open] " + format.Name + " -> 0x" + hr.ToString("X8"));
                }
                catch (Exception ex)
                {
                    Trace?.Invoke("[open] " + format.Name + " threw " + ex.GetType().Name + ": " + ex.Message);
                    failures.Add(format.Name + ": " + ex.Message);
                    stream.Close();
                    try { Marshal.FinalReleaseComObject(comObject); } catch { /* ignore */ }
                    continue;
                }

                if (hr != Native.S_OK)
                {
                    stream.Close();
                    try { Marshal.FinalReleaseComObject(comObject); } catch { /* ignore */ }
                    continue;
                }

                var session = new ArchiveSession
                {
                    Archive = archive,
                    Stream = stream,
                    ComObject = comObject,
                    FilePath = fullPath,
                    Format = format,
                    Password = password
                };

                try
                {
                    LoadEntries(session);
                }
                catch
                {
                    session.Close();
                    throw;
                }

                return session;
            }

            var detail = failures.Count > 0 ? "\n" + string.Join("\n", failures.Take(3)) : "";
            throw new InvalidDataException("无法识别或打开该压缩包，可能格式不受支持、文件损坏或密码错误。" + detail);
        }

        private static void LoadEntries(ArchiveSession session)
        {
            var archive = session.Archive;
            Trace?.Invoke("[list] GetNumberOfItems ...");
            if (archive.GetNumberOfItems(out var count) != Native.S_OK)
                throw new InvalidDataException("无法读取压缩包目录。");
            Trace?.Invoke("[list] items = " + count);

            var list = new List<ArchiveEntry>((int)Math.Min(count, 100000));
            ulong unpacked = 0, packed = 0;
            var encrypted = false;

            // 复用同一个 PROPVARIANT，避免每个属性都申请 / 释放非托管内存
            using var properties = new PropertyReader();

            for (uint i = 0; i < count; i++)
            {
                var path = properties.GetString(archive, i, PropIds.Path) ?? "";
                var isDir = properties.GetBool(archive, i, PropIds.IsDir);
                var size = properties.GetUInt64(archive, i, PropIds.Size);
                var packSize = properties.GetUInt64(archive, i, PropIds.PackSize);
                var modified = properties.GetTime(archive, i, PropIds.MTime);
                var attrib = properties.GetUInt32(archive, i, PropIds.Attrib);
                var isEncrypted = properties.GetBool(archive, i, PropIds.Encrypted);
                var crc = properties.GetUInt32(archive, i, PropIds.Crc);

                if (string.IsNullOrEmpty(path)) continue;
                if (path.EndsWith(":")) continue; // alt-stream group markers

                var name = path;
                var slash = path.LastIndexOfAny(new[] { '\\', '/' });
                if (slash >= 0 && slash < path.Length - 1) name = path.Substring(slash + 1);

                var entry = new ArchiveEntry
                {
                    Path = path.Replace('/', '\\'),
                    Name = name.TrimEnd('\\', '/'),
                    IsDir = isDir,
                    Size = size,
                    PackSize = packSize,
                    Modified = modified,
                    Attrib = attrib,
                    Encrypted = isEncrypted,
                    Crc = crc
                };

                if (entry.IsDir && entry.Name.Length == 0) continue;

                list.Add(entry);
                if (!isDir) unpacked += size;
                packed += packSize;
                if (isEncrypted) encrypted = true;
            }

            session.EntryList = list;
            session.UnpackedSize = unpacked;
            session.PackedSize = packed;
            session.HasEncrypted = encrypted;
        }

        public static OperationResult Extract(ArchiveSession session, IReadOnlyList<uint>? indices, string targetDir,
            ProgressState progress, CancellationToken token, ManualResetEventSlim? pauseGate = null,
            ExtractHooks? hooks = null)
        {
            var result = new OperationResult();
            Directory.CreateDirectory(targetDir);

            // 个别格式不会回调 SetTotal，先用条目大小兜底，避免进度条一直停在 0
            if (progress.Total <= 0)
            {
                long total = 0;
                if (indices == null)
                {
                    foreach (var entry in session.EntryList)
                        if (!entry.IsDir) total += (long)entry.Size;
                }
                else
                {
                    foreach (var index in indices)
                    {
                        if (index >= session.EntryList.Count) continue;
                        var entry = session.EntryList[(int)index];
                        if (!entry.IsDir) total += (long)entry.Size;
                    }
                }
                progress.Total = total;
            }

            var callback = new ArchiveExtractCallback(session, targetDir, progress, token, false, pauseGate, hooks);
            uint numItems = 0xFFFFFFFF;
            IntPtr indicesPtr = IntPtr.Zero;

            try
            {
                if (indices != null)
                {
                    numItems = (uint)indices.Count;
                    if (numItems == 0)
                    {
                        result.Success = true;
                        return result;
                    }
                    indicesPtr = Marshal.AllocHGlobal(sizeof(uint) * indices.Count);
                    var arr = indices.ToArray();
                    for (var i = 0; i < arr.Length; i++)
                        Marshal.WriteInt32(indicesPtr, i * sizeof(uint), unchecked((int)arr[i]));
                }

                var hr = session.Archive.Extract(indicesPtr, numItems, 0, callback);
                result.Errors = callback.Errors;
                result.Items = callback.ExtractedCount;
                result.ErrorItems.AddRange(callback.ErrorItems);
                result.Cancelled = callback.CancelTriggered || hr == Native.E_ABORT;
                result.WrongPassword = callback.WrongPassword;

                if (result.WrongPassword)
                {
                    result.Success = false;
                    result.Message = "密码错误，无法解密文件。";
                }
                else if (result.Cancelled)
                {
                    result.Success = false;
                    result.Message = "操作已取消。";
                }
                else if (hr != Native.S_OK && hr != Native.S_FALSE)
                {
                    result.Success = false;
                    result.Message = "解压过程中出现错误 (0x" + hr.ToString("X8") + ")。" +
                                     (result.Errors > 0 ? "\n" + string.Join("\n", result.ErrorItems.Take(5)) : "");
                }
                else
                {
                    result.Success = true;
                    result.Message = result.Errors > 0
                        ? "已解压 " + result.Items + " 项，其中 " + result.Errors + " 项出现问题。"
                        : "已成功解压 " + result.Items + " 项。";
                }
            }
            catch (COMException ex)
            {
                result.Success = false;
                result.Cancelled = ex.HResult == Native.E_ABORT;
                result.Message = ex.Message;
            }
            finally
            {
                if (indicesPtr != IntPtr.Zero) Marshal.FreeHGlobal(indicesPtr);

                // 等待后台写盘完成，让真实写入错误能反馈给调用方
                callback.CompleteWrites();
                if (callback.WriteError != null && result.Success)
                {
                    result.Success = false;
                    result.Message = "写入文件失败：" + callback.WriteError.Message;
                }
            }

            return result;
        }

        public static OperationResult Test(ArchiveSession session, ProgressState progress, CancellationToken token)
        {
            var result = new OperationResult();

            // 与解压一致：先用条目大小兜底总进度
            if (progress.Total <= 0)
            {
                long total = 0;
                foreach (var entry in session.EntryList)
                    if (!entry.IsDir) total += (long)entry.Size;
                progress.Total = total;
            }

            var callback = new ArchiveExtractCallback(session, null, progress, token, true);

            var hr = session.Archive.Extract(IntPtr.Zero, 0xFFFFFFFF, 1, callback);
            result.Cancelled = callback.CancelTriggered || hr == Native.E_ABORT;
            result.Errors = callback.Errors;
            result.Items = callback.ExtractedCount;
            result.ErrorItems.AddRange(callback.ErrorItems);
            result.WrongPassword = callback.WrongPassword;

            if (result.Cancelled)
            {
                result.Message = "测试已取消。";
            }
            else if (result.WrongPassword)
            {
                result.Message = "密码错误。";
            }
            else if (hr != Native.S_OK || result.Errors > 0)
            {
                result.Success = false;
                result.Message = "压缩包校验未通过，发现 " + result.Errors + " 个问题。";
            }
            else
            {
                result.Success = true;
                result.Message = "校验完成，所有 " + result.Items + " 个项目均完好。";
            }
            return result;
        }

        public sealed class SourceItem
        {
            public string DiskPath = "";
            public string ArcPath = "";
            public bool IsDir;
            public long Size;
            public DateTime Modified;
            public uint Attrib;
        }

        public static List<SourceItem> CollectSources(IEnumerable<string> paths, bool excludeJunk = false)
        {
            var items = new List<SourceItem>();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0
            };

            // 直接用 EnumerationOptions 返回的 FileSystemInfo：它已缓存名字/大小/时间/属性，
            // 不用再为每个条目做 Directory.Exists + File.Exists + new FileInfo（三倍系统调用）。
            // 上万个文件时这一步曾占到总耗时的大头。
            void Add(FileSystemInfo info, string arcPath, bool isRoot)
            {
                if (!isRoot && excludeJunk)
                {
                    uint attributes = 0;
                    try { attributes = (uint)info.Attributes; } catch { /* ignore */ }
                    if (IsJunk(info.Name, attributes)) return;
                }

                try
                {
                    if (info is DirectoryInfo dir)
                    {
                        items.Add(new SourceItem
                        {
                            DiskPath = dir.FullName,
                            ArcPath = arcPath.Replace('\\', '/'),
                            IsDir = true,
                            Modified = dir.LastWriteTime,
                            Attrib = (uint)dir.Attributes
                        });

                        foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                            Add(entry, arcPath + "/" + entry.Name, false);
                    }
                    else
                    {
                        var file = (FileInfo)info;
                        items.Add(new SourceItem
                        {
                            DiskPath = file.FullName,
                            ArcPath = arcPath.Replace('\\', '/'),
                            IsDir = false,
                            Size = file.Length,
                            Modified = file.LastWriteTime,
                            Attrib = (uint)file.Attributes
                        });
                    }
                }
                catch (Exception)
                {
                    // 枚举中途目录被删或权限不足：跳过该条目，不影响其余文件
                }
            }

            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var full = Path.GetFullPath(p);
                var isDir = Directory.Exists(full);
                var rootName = isDir ? new DirectoryInfo(full).Name : Path.GetFileName(full);
                if (string.IsNullOrEmpty(rootName)) continue;

                FileSystemInfo root = isDir ? new DirectoryInfo(full) : new FileInfo(full);
                Add(root, rootName, true);
            }

            return items;
        }

        private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Thumbs.db", "desktop.ini", "ehthumbs.db", "ehthumbs_vista.db", ".DS_Store", "$RECYCLE.BIN"
        };

        /// <summary>判断隐藏 / 系统 / 临时文件，用于压缩时自动排除。</summary>
        private static bool IsJunk(string name, uint attributes)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                name.StartsWith("~$", StringComparison.Ordinal)) return true;
            if (JunkNames.Contains(name)) return true;

            const uint hidden = (uint)FileAttributes.Hidden;
            const uint system = (uint)FileAttributes.System;
            return (attributes & (hidden | system)) != 0;
        }

        public static FormatInfo? FindCreateFormat(string extension)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            return Formats.FirstOrDefault(f => f.CanUpdate && f.MatchesExtension(ext));
        }

        /// <summary>用 7z.dll 内核创建压缩包，完全按调用方给定的压缩等级执行。</summary>
        public static OperationResult Create(IReadOnlyList<SourceItem> sources, string outputPath, string password,
            int level, ProgressState progress, CancellationToken token)
        {
            var result = new OperationResult();
            var ext = Path.GetExtension(outputPath);
            var format = FindCreateFormat(ext);
            if (format == null)
            {
                result.Message = "当前内核不支持创建 ." + ext.TrimStart('.') + " 格式。";
                return result;
            }

            var comObject = CreateComObject(format.ClassId, InterfaceIds.IOutArchive);
            var outArchive = (IOutArchive)comObject;
            var totalSize = sources.Where(s => !s.IsDir)
                .Aggregate(0UL, (acc, s) => acc + (ulong)Math.Max(0, s.Size));

            var callback = new ArchiveUpdateCallback(sources, password, progress, token, totalSize);
            var outStream = new FileOutStream(outputPath);

            try
            {
                TrySetProperties(comObject, format, level, password, totalSize);

                var hr = outArchive.UpdateItems(outStream, (uint)sources.Count, callback);
                Trace?.Invoke("[create] UpdateItems 0x" + hr.ToString("X8") + " callbackErrors=" + callback.Errors);
                outStream.Close();

                result.Cancelled = callback.CancelTriggered || hr == Native.E_ABORT;
                result.Errors = callback.Errors;
                result.Items = callback.CompletedItems;

                if (result.Cancelled)
                {
                    result.Message = "压缩已取消。";
                }
                else if (hr != Native.S_OK)
                {
                    result.Success = false;
                    result.Message = "压缩失败 (0x" + hr.ToString("X8") + ")。";
                }
                else
                {
                    result.Success = true;
                    result.Message = "已创建压缩包，共 " + sources.Count + " 个项目。";
                }
            }
            catch (Exception ex)
            {
                outStream.Close();
                result.Success = false;
                result.Message = "压缩失败：" + ex.Message;
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(comObject); } catch { /* ignore */ }
            }

            return result;
        }

        private static void TrySetProperties(object comObject, FormatInfo format, int level, string password,
            ulong totalBytes)
        {
            if (comObject is not ISetProperties setter)
            {
                LastPropertyStatus = "no ISetProperties";
                return;
            }

            // 小任务降低压缩级别，避免为几 MB 数据付出高压缩的时间成本
            var effectiveLevel = totalBytes > 0 && totalBytes < 8UL * 1024 * 1024
                ? Math.Min(level, 5)
                : level;

            // 7z/zip 都是按数据块并行压缩，线程数不能按文件数限制：
            // 单个大文件同样能拆块并行，限制线程数会让 ZIP 大文件退回单核（实测慢 10 倍）
            var threads = Math.Max(1, Environment.ProcessorCount);

            var names = new List<string>();
            var types = new List<ushort>();
            var numbers = new List<int>();
            var texts = new List<string?>();

            // 数值项必须用 VT_UI4 传递：7-Zip 的 SetProperties 只认数值类型的 "x"/"mt"，
            // 传 VT_BSTR 会让整个调用返回 E_INVALIDARG，压缩等级因此完全失效（一直按默认等级压）
            names.Add("x"); types.Add(VarTypes.VT_UI4); numbers.Add(effectiveLevel); texts.Add(null);
            names.Add("mt"); types.Add(VarTypes.VT_UI4); numbers.Add(threads); texts.Add(null);

            if (format.Name.Contains("7z", StringComparison.OrdinalIgnoreCase))
            {
                names.Add("s"); types.Add(VarTypes.VT_BSTR); numbers.Add(0); texts.Add("on");
                if (!string.IsNullOrEmpty(password))
                {
                    names.Add("he"); types.Add(VarTypes.VT_BSTR); numbers.Add(0); texts.Add("on");
                }
            }

            var namePtrs = new IntPtr[names.Count];
            var valuePtrs = new IntPtr[texts.Count];

            try
            {
                for (var i = 0; i < names.Count; i++)
                    namePtrs[i] = Marshal.StringToHGlobalUni(names[i]);

                for (var i = 0; i < texts.Count; i++)
                    if (texts[i] != null) valuePtrs[i] = Marshal.StringToBSTR(texts[i]!);

                var namesMem = Marshal.AllocHGlobal(IntPtr.Size * names.Count);
                for (var i = 0; i < names.Count; i++)
                    Marshal.WriteIntPtr(namesMem, i * IntPtr.Size, namePtrs[i]);

                var valuesMem = Marshal.AllocHGlobal(Native.PropVariantSize * names.Count);
                for (var i = 0; i < names.Count; i++)
                {
                    var cell = valuesMem + i * Native.PropVariantSize;
                    for (var b = 0; b < Native.PropVariantSize; b++) Marshal.WriteByte(cell, b, 0);
                    Marshal.WriteInt16(cell, 0, (short)types[i]);
                    if (types[i] == VarTypes.VT_BSTR)
                        Marshal.WriteIntPtr(cell, 8, valuePtrs[i]);
                    else
                        Marshal.WriteInt32(cell, 8, numbers[i]);
                }

                var hr = setter.SetProperties(namesMem, valuesMem, (uint)names.Count);
                LastPropertyStatus = "0x" + hr.ToString("X8") + " x=" + effectiveLevel + " mt=" + threads;

                Marshal.FreeHGlobal(valuesMem);
                Marshal.FreeHGlobal(namesMem);
            }
            catch
            {
                // Property support is optional; ignore unsupported switches.
            }
            finally
            {
                foreach (var ptr in valuePtrs)
                    if (ptr != IntPtr.Zero) Marshal.FreeBSTR(ptr);
                foreach (var ptr in namePtrs)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>把托管值写入调用方提供的 PROPVARIANT 缓冲区。</summary>
        internal static int WritePropVariant(IntPtr pv, ushort varType, IntPtr pointerOrValue)
        {
            for (var i = 0; i < Native.PropVariantSize; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, (short)varType);
            Marshal.WriteInt64(pv, 8, pointerOrValue.ToInt64());
            return Native.S_OK;
        }
    }

    /// <summary>
    /// 复用同一个 PROPVARIANT 缓冲区读取条目属性。
    /// 大压缩包（数万条目 × 8 个属性）可省去大量 AllocHGlobal / FreeHGlobal。
    /// </summary>
    internal sealed class PropertyReader : IDisposable
    {
        private readonly IntPtr _pv = Native.AllocPropVariant();

        public string? GetString(IInArchive archive, uint index, uint propId)
        {
            if (archive.GetProperty(index, propId, _pv) != Native.S_OK) return null;

            var result = (ushort)Marshal.ReadInt16(_pv, 0) == VarTypes.VT_BSTR
                ? Marshal.PtrToStringBSTR(Marshal.ReadIntPtr(_pv, 8))
                : null;

            Native.PropVariantClearPtr(_pv);
            return result;
        }

        public bool GetBool(IInArchive archive, uint index, uint propId)
        {
            if (archive.GetProperty(index, propId, _pv) != Native.S_OK) return false;

            var result = (ushort)Marshal.ReadInt16(_pv, 0) == VarTypes.VT_BOOL &&
                         Marshal.ReadInt16(_pv, 8) != 0;

            Native.PropVariantClearPtr(_pv);
            return result;
        }

        public ulong GetUInt64(IInArchive archive, uint index, uint propId)
        {
            if (archive.GetProperty(index, propId, _pv) != Native.S_OK) return 0;

            var result = (ushort)Marshal.ReadInt16(_pv, 0) switch
            {
                VarTypes.VT_UI8 => (ulong)Marshal.ReadInt64(_pv, 8),
                VarTypes.VT_I8 => (ulong)Math.Max(0, Marshal.ReadInt64(_pv, 8)),
                VarTypes.VT_UI4 => (uint)Marshal.ReadInt32(_pv, 8),
                _ => 0UL
            };

            Native.PropVariantClearPtr(_pv);
            return result;
        }

        public uint GetUInt32(IInArchive archive, uint index, uint propId)
        {
            if (archive.GetProperty(index, propId, _pv) != Native.S_OK) return 0;

            var result = (ushort)Marshal.ReadInt16(_pv, 0) switch
            {
                VarTypes.VT_UI4 => (uint)Marshal.ReadInt32(_pv, 8),
                VarTypes.VT_UI8 => (uint)Marshal.ReadInt64(_pv, 8),
                VarTypes.VT_I8 => (uint)Math.Max(0, Marshal.ReadInt64(_pv, 8)),
                _ => 0u
            };

            Native.PropVariantClearPtr(_pv);
            return result;
        }

        public DateTime GetTime(IInArchive archive, uint index, uint propId)
        {
            if (archive.GetProperty(index, propId, _pv) != Native.S_OK) return DateTime.MinValue;
            if ((ushort)Marshal.ReadInt16(_pv, 0) != VarTypes.VT_FILETIME) return DateTime.MinValue;

            var ticks = Marshal.ReadInt64(_pv, 8);
            Native.PropVariantClearPtr(_pv);

            if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(ticks).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }

        public void Dispose() => Marshal.FreeHGlobal(_pv);
    }

    public sealed class FileInStream : IInStream, IDisposable
    {
        private readonly FileStream _stream;

        public FileInStream(string path)
        {
            // 压缩包顺序读取用 1MB 缓冲，减少 GB 级压缩包的系统调用次数
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20,
                FileOptions.SequentialScan);
        }

        /// <summary>直接把内核请求的数据读入它的非托管缓冲区，省去一次中转拷贝。</summary>
        public unsafe int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                var read = _stream.Read(new Span<byte>((void*)data, (int)size));
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, read);
                return Native.S_OK;
            }
            catch
            {
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                return Native.S_FALSE;
            }
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            try
            {
                var origin = seekOrigin switch
                {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    2 => SeekOrigin.End,
                    _ => SeekOrigin.Begin
                };
                var pos = _stream.Seek(offset, origin);
                if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, pos);
                return Native.S_OK;
            }
            catch
            {
                return Native.S_FALSE;
            }
        }

        public void Close() => _stream.Dispose();

        public void Dispose() => _stream.Dispose();
    }

    public sealed class FileOutStream : ISequentialOutStream, IOutStream, IDisposable
    {
        private readonly FileStream _stream;
        private bool _closed;
        private long _skip;
        private readonly DiskBudget? _budget;
        private readonly Action? _onBudgetExhausted;

        public FileOutStream(string path) : this(path, 1 << 16)
        {
        }

        /// <summary>按文件大小选择缓冲区：大文件用 1 关闭缓冲，小文件用 16KB 合并零碎写。</summary>
        public FileOutStream(string path, int bufferSize) : this(path, bufferSize, 0)
        {
        }

        /// <summary>
        /// 断点续写构造：resumeOffset 大于 0 时从已有 .part 文件的指定偏移继续追加，
        /// 之前的字节由解码器重新生成但在 Write 中丢弃（_skip），保证最终内容完整。
        /// </summary>
        public FileOutStream(string path, int bufferSize, long resumeOffset,
            DiskBudget? budget = null, Action? onBudgetExhausted = null)
        {
            _budget = budget;
            _onBudgetExhausted = onBudgetExhausted;
            _skip = Math.Max(0, resumeOffset);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var append = resumeOffset > 0 && File.Exists(path);
            _stream = new FileStream(path, append ? FileMode.Open : FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize, FileOptions.SequentialScan);

            if (append)
            {
                // .part 比记录偏移长时截断到记录位置；短于时由协调器在规划阶段拦截
                if (_stream.Length > resumeOffset) _stream.SetLength(resumeOffset);
                _stream.Seek(resumeOffset, SeekOrigin.Begin);
            }
        }

        /// <summary>解码器重新生成但需要丢弃的续写前缀字节数（诊断用）。</summary>
        public long SkipRemaining => _skip;

        /// <summary>预算耗尽被中止时置位，供回调区分取消与磁盘分批暂停。</summary>
        public bool BudgetStopped { get; private set; }

        /// <summary>直接从内核的非托管缓冲区写入文件，省去一次中转拷贝。</summary>
        public unsafe int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                var length = (int)size;
                var span = new ReadOnlySpan<byte>((void*)data, length);

                // 续写时丢弃已落盘的前缀字节，这部分由解码器重算但不再写盘
                if (_skip > 0)
                {
                    if (length <= _skip)
                    {
                        _skip -= length;
                        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, length);
                        return Native.S_OK;
                    }

                    span = span.Slice((int)_skip);
                    length = span.Length;
                    _skip = 0;
                }

                if (_budget != null && _budget.Remaining <= 0)
                {
                    BudgetStopped = true;
                    _onBudgetExhausted?.Invoke();
                    if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                    return Native.E_ABORT;
                }

                // 基准测试时统计写盘耗时，正常使用时只多一次静态字段判断
                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    _stream.Write(span);
                    EngineTiming.WriteTicks += Stopwatch.GetTimestamp() - start;
                    EngineTiming.WriteBytes += length;
                    EngineTiming.WriteCalls++;
                }
                else
                {
                    _stream.Write(span);
                }

                _budget?.TryConsume(length);

                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, length);
                return Native.S_OK;
            }
            catch
            {
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                return Native.S_FALSE;
            }
        }

        public int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
            return Native.E_NOTIMPL;
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            try
            {
                var origin = seekOrigin switch
                {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    2 => SeekOrigin.End,
                    _ => SeekOrigin.Begin
                };
                var pos = _stream.Seek(offset, origin);
                if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, pos);
                return Native.S_OK;
            }
            catch
            {
                return Native.S_FALSE;
            }
        }

        public int SetSize(long newSize)
        {
            try
            {
                _stream.SetLength(newSize);
                return Native.S_OK;
            }
            catch
            {
                return Native.S_FALSE;
            }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _stream.Dispose();
        }

        public void Dispose() => Close();

        /// <summary>按条目声明大小预分配文件，减少大文件解压时的增长与碎片开销。</summary>
        public void Preallocate(long size)
        {
            if (size <= 0) return;

            try
            {
                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    _stream.SetLength(size);
                    _stream.Position = 0;
                    EngineTiming.PreallocateTicks += Stopwatch.GetTimestamp() - start;
                }
                else
                {
                    _stream.SetLength(size);
                    _stream.Position = 0;
                }
            }
            catch
            {
                // 预分配失败不影响正常写入
            }
        }
    }

    /// <summary>把内存中的小文件交给固定线程池并行落盘，摊薄 CreateFile/Close/设置时间戳的系统调用耗时。</summary>
    internal sealed class ParallelFileWriter : IDisposable
    {
        private readonly BlockingCollection<Action> _jobs;
        private readonly Thread[] _workers;
        private Exception? _error;

        public ParallelFileWriter(int workerCount)
        {
            workerCount = Math.Clamp(workerCount, 1, 6);
            _jobs = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), workerCount * 3);
            _workers = new Thread[workerCount];

            for (var i = 0; i < workerCount; i++)
            {
                _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "NovaZip-SmallFile" };
                _workers[i].Start();
            }
        }

        public Exception? Error => _error;

        public void Enqueue(Action job)
        {
            if (_error != null) return;

            try
            {
                _jobs.Add(job);
            }
            catch (InvalidOperationException)
            {
                // 队列已关闭（异常或收尾阶段），忽略新任务
            }
        }

        public void Complete()
        {
            try
            {
                _jobs.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            foreach (var worker in _workers) worker.Join();
        }

        public void Dispose()
        {
            Complete();
            _jobs.Dispose();
        }

        private void WorkerLoop()
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ex, null);
                }
            }
        }
    }

    /// <summary>
    /// 把小文件数据先收在内存里，等 7z 回调返回后再交给后台线程写盘。
    /// 按条目声明大小一次性分配，避免 MemoryStream 反复扩容和 ToArray 再复制一份。
    /// </summary>
    internal sealed class MemoryOutStream : ISequentialOutStream
    {
        private byte[] _buffer;
        private int _length;

        public MemoryOutStream(int capacity)
        {
            _buffer = capacity > 0 ? new byte[capacity] : Array.Empty<byte>();
        }

        public long Length => _length;

        /// <summary>正常情况下长度与声明大小一致，直接交出同一份缓冲，不再复制。</summary>
        public byte[] Bytes
        {
            get
            {
                if (_length == _buffer.Length) return _buffer;
                if (_length == 0) return Array.Empty<byte>();
                var exact = new byte[_length];
                Buffer.BlockCopy(_buffer, 0, exact, 0, _length);
                return exact;
            }
        }

        public unsafe int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            var length = (int)size;
            if (_length + length > _buffer.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _length + length)];
                Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
                _buffer = grown;
            }

            new ReadOnlySpan<byte>((void*)data, length).CopyTo(_buffer.AsSpan(_length));
            _length += length;
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, length);
            return Native.S_OK;
        }
    }

    [ComVisible(true)]
    public sealed class ArchiveOpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword
    {
        private readonly string _password;

        public ArchiveOpenCallback(string password) => _password = password;

        public int SetTotal(IntPtr files, IntPtr bytes) => Native.S_OK;

        public int SetCompleted(IntPtr files, IntPtr bytes) => Native.S_OK;

        public int CryptoGetTextPassword(out IntPtr password)
        {
            password = Marshal.StringToBSTR(_password ?? "");
            return Native.S_OK;
        }
    }

    [ComVisible(true)]
    public sealed class ArchiveExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private readonly ArchiveSession _session;
        private readonly string? _targetDir;
        private readonly ProgressState _progress;
        private readonly CancellationToken _token;
        private readonly bool _testMode;
        private readonly ManualResetEventSlim? _pauseGate;
        private readonly ExtractHooks? _hooks;
        private readonly List<string> _errors = new();
        private readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ParallelFileWriter? _smallWriter;
        private const ulong SmallFileLimit = 4UL * 1024 * 1024;
        private int _currentIndex = -1;
        private string? _currentPath;
        private DateTime _currentTime;
        private FileOutStream? _currentStream;
        private MemoryOutStream? _currentBuffer;
        private Exception? _writeError;
        private bool _entryFailed;
        private (string PartPath, long Offset)? _resumePlan;
        private long _resumeEntrySize;

        public int Errors => _errors.Count;
        public List<string> ErrorItems => _errors;
        public Exception? WriteError => _writeError ?? _smallWriter?.Error;
        public int ExtractedCount;
        public bool CancelTriggered;
        public bool WrongPassword;

        public ArchiveExtractCallback(ArchiveSession session, string? targetDir, ProgressState progress,
            CancellationToken token, bool testMode, ManualResetEventSlim? pauseGate = null,
            ExtractHooks? hooks = null)
        {
            _session = session;
            _targetDir = targetDir;
            _progress = progress;
            _token = token;
            _testMode = testMode;
            _pauseGate = pauseGate;
            _hooks = hooks;
            _smallWriter = testMode
                ? null
                // 建文件受杀毒过滤/目录锁影响，6 个并发已能压满，再多无收益（实测 12 与 6 相同）
                : new ParallelFileWriter(Math.Clamp(Environment.ProcessorCount - 1, 4, 6));
        }

        public int SetTotal(ulong total)
        {
            // 断点续传时由协调器锁定总字节，避免每批重置进度条
            _progress.Total = _progress.FixedTotal > 0 ? _progress.FixedTotal : (long)total;
            return Native.S_OK;
        }

        public int SetCompleted(IntPtr completeValue)
        {
            if (completeValue != IntPtr.Zero)
                _progress.Completed = _progress.BaseCompleted + Marshal.ReadInt64(completeValue);
            if (_token.IsCancellationRequested)
            {
                CancelTriggered = true;
                _progress.Cancelled = true;
                return Native.E_ABORT;
            }
            return WaitIfPaused() ? Native.S_OK : Native.E_ABORT;
        }

        public int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode)
        {
            outStream = null!;
            if (_token.IsCancellationRequested)
            {
                CancelTriggered = true;
                _progress.Cancelled = true;
                return Native.E_ABORT;
            }

            // 暂停时停在文件边界，继续后从下一个文件接着解压
            if (!WaitIfPaused()) return Native.E_ABORT;

            CloseCurrentStream();
            _currentBuffer = null;
            _entryFailed = false;
            _resumePlan = null;

            _currentIndex = (int)index;
            _progress.ItemIndex = _currentIndex;

            if (askExtractMode != 0) return Native.S_OK; // test / skip

            if (index >= _session.EntryList.Count) return Native.S_OK;
            var entry = _session.EntryList[(int)index];

            if (_targetDir == null) return Native.S_OK;

            string relative;
            try
            {
                relative = SanitizeRelativePath(entry.Path);
            }
            catch
            {
                _errors.Add("非法路径：" + entry.Path);
                _progress.CurrentFile = entry.Path;
                _hooks?.OnEntryFinished?.Invoke((int)index, "", false);
                return Native.S_OK;
            }

            var fullPath = Path.Combine(_targetDir, relative);

            // SanitizeRelativePath 已剔除 .. 与盘符前缀，这里只拦住空路径与绝对路径
            if (relative.Length == 0 || Path.IsPathRooted(relative))
            {
                _errors.Add("路径越界：" + entry.Path);
                _progress.CurrentFile = entry.Path;
                _hooks?.OnEntryFinished?.Invoke((int)index, "", false);
                return Native.S_OK;
            }

            _currentPath = fullPath;
            _currentTime = entry.Modified;
            _progress.CurrentFile = entry.Path;

            if (entry.IsDir)
            {
                EnsureDirectory(fullPath);
                return Native.S_OK;
            }

            try
            {
                EnsureDirectory(Path.GetDirectoryName(fullPath) ?? "");

                if (entry.Size < SmallFileLimit)
                {
                    // 小文件先收进内存，交给后台线程并行建文件，摊薄 CreateFile/Close/时间戳开销
                    _currentBuffer = new MemoryOutStream((int)entry.Size);
                    outStream = _currentBuffer;
                }
                else
                {
                    // 断点续传：协调器对该条目给出 .part 续写计划时走追加模式
                    var plan = _hooks?.GetResumePlan?.Invoke((int)index) ?? null;
                    if (plan != null && !string.IsNullOrEmpty(plan.Value.PartPath))
                    {
                        _resumePlan = plan;
                        _resumeEntrySize = (long)entry.Size;
                        _currentStream = new FileOutStream(plan.Value.PartPath, 1, plan.Value.Offset,
                            _hooks?.Budget, () => _hooks!.BudgetExhausted = true);
                        outStream = _currentStream;
                        return Native.S_OK;
                    }

                    // 大文件直接写盘：实测再叠一层写线程反而抢 CPU，磁盘带宽才是上限
                    if (EngineTiming.Enabled)
                    {
                        var start = Stopwatch.GetTimestamp();
                        _currentStream = new FileOutStream(fullPath, 1, 0, _hooks?.Budget,
                            () => _hooks!.BudgetExhausted = true);
                        EngineTiming.FileOpenTicks += Stopwatch.GetTimestamp() - start;
                        EngineTiming.FileOpenCalls++;
                    }
                    else
                    {
                        _currentStream = new FileOutStream(fullPath, 1, 0, _hooks?.Budget,
                            () => _hooks!.BudgetExhausted = true);
                    }

                    // 续写流不做预分配（会破坏已写内容）；磁盘分批时也跳过预分配，
                    // 否则预留空间会提前吃光预算导致无法分批
                    if (_hooks?.Budget == null && entry.Size >= 8UL * 1024 * 1024 && entry.Size <= int.MaxValue)
                        _currentStream.Preallocate((long)entry.Size);
                    outStream = _currentStream;
                }
            }
            catch (Exception ex)
            {
                _errors.Add(entry.Path + " (" + ex.Message + ")");
                _currentPath = null;
                _entryFailed = true;
                _hooks?.OnEntryFinished?.Invoke((int)index, "", false);
                return Native.S_OK;
            }

            return Native.S_OK;
        }

        public int PrepareOperation(int askExtractMode) => Native.S_OK;

        public int SetOperationResult(int opRes)
        {
            _progress.LastResult = opRes;
            var budgetPaused = _hooks?.BudgetExhausted == true;

            if (opRes != ArchiveResults.Ok)
            {
                if (opRes == ArchiveResults.WrongPassword) WrongPassword = true;
                var name = _currentIndex >= 0 && _currentIndex < _session.EntryList.Count
                    ? _session.EntryList[_currentIndex].Path
                    : "未知项目";
                _errors.Add(name + "：" + ArchiveResults.Describe(opRes));
            }
            else if (opRes == ArchiveResults.Ok)
            {
                ExtractedCount++;
            }

            CloseCurrentStream();

            var buffer = _currentBuffer;
            _currentBuffer = null;
            var path = _currentPath;
            var time = _currentTime;
            var index = _currentIndex;
            var resumePlan = _resumePlan;
            _resumePlan = null;

            if (path != null && !_testMode)
            {
                if (resumePlan != null && (ulong)_resumeEntrySize >= SmallFileLimit)
                {
                    // 续写流：成功时校验大小并把 .part 改名成最终文件；失败时删掉
                    // .part 让协调器下轮从头重解。预算中止时保留 .part 等待继续。
                    HandleResumeStreamResult(index, path, resumePlan.Value, opRes, budgetPaused, time);
                    _currentPath = null;
                    return Native.S_OK;
                }

                if (buffer != null)
                {
                    if (opRes == ArchiveResults.Ok && _smallWriter != null)
                    {
                        var bytes = buffer.Bytes;
                        _smallWriter.Enqueue(() =>
                        {
                            var t0 = EngineTiming.Enabled ? Stopwatch.GetTimestamp() : 0L;
                            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                bytes.Length <= 4096 ? 1 : 1 << 14, FileOptions.SequentialScan);

                            if (EngineTiming.Enabled)
                            {
                                Interlocked.Add(ref EngineTiming.FileOpenTicks, Stopwatch.GetTimestamp() - t0);
                                Interlocked.Increment(ref EngineTiming.FileOpenCalls);

                                var tw = Stopwatch.GetTimestamp();
                                fs.Write(bytes, 0, bytes.Length);
                                Interlocked.Add(ref EngineTiming.WriteTicks, Stopwatch.GetTimestamp() - tw);
                                Interlocked.Add(ref EngineTiming.WriteBytes, bytes.Length);
                                Interlocked.Increment(ref EngineTiming.WriteCalls);
                            }
                            else
                            {
                                fs.Write(bytes, 0, bytes.Length);
                            }

                            ApplyTimestamp(fs, time);
                        });
                    }
                }
                else if (time > DateTime.MinValue)
                {
                    ApplyTimestamp(path, time);
                }
            }

            // 通知协调器条目结果：预算中止时不标记失败，保留断点等待继续
            if (!_entryFailed && !budgetPaused)
                _hooks?.OnEntryFinished?.Invoke(index, path ?? "", opRes == ArchiveResults.Ok);
            if (_entryFailed && budgetPaused) _entryFailed = false;

            _currentPath = null;
            return Native.S_OK;
        }

        private void HandleResumeStreamResult(int index, string finalPath,
            (string PartPath, long Offset) plan, int opRes, bool budgetPaused, DateTime time)
        {
            if (opRes != ArchiveResults.Ok)
            {
                if (budgetPaused) return; // 磁盘分批中止：保留 .part 续写

                try { if (File.Exists(plan.PartPath)) File.Delete(plan.PartPath); } catch { /* ignore */ }
                _hooks?.OnEntryFinished?.Invoke(index, finalPath, false);
                return;
            }

            try
            {
                var length = new FileInfo(plan.PartPath).Length;
                if (length != _resumeEntrySize)
                {
                    // 续写结果不完整（例如前缀损坏）：删掉 .part 交协调器重解
                    try { File.Delete(plan.PartPath); } catch { /* ignore */ }
                    _hooks?.OnEntryFinished?.Invoke(index, finalPath, false);
                    return;
                }

                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(plan.PartPath, finalPath);
                if (time > DateTime.MinValue) ApplyTimestamp(finalPath, time);
                _hooks?.OnEntryFinished?.Invoke(index, finalPath, true);
            }
            catch (Exception ex)
            {
                _errors.Add(finalPath + " (" + ex.Message + ")");
                _hooks?.OnEntryFinished?.Invoke(index, finalPath, false);
            }
        }

        /// <summary>等待后台小文件写入全部完成，用于收尾与错误回传。</summary>
        public void CompleteWrites()
        {
            _smallWriter?.Dispose();
            if (_writeError == null && _smallWriter?.Error != null) _writeError = _smallWriter.Error;

            // 中止（取消/磁盘分批）时 7z 不会回调 SetOperationResult，
            // 大文件流残留打开会让下一批的 .part 续写遇到文件占用
            CloseCurrentStream();
        }

        private static void ApplyTimestamp(string path, DateTime time)
        {
            if (time <= DateTime.MinValue) return;

            try
            {
                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    File.SetLastWriteTime(path, time);
                    Interlocked.Add(ref EngineTiming.TimeTicks, Stopwatch.GetTimestamp() - start);
                }
                else
                {
                    File.SetLastWriteTime(path, time);
                }
            }
            catch { /* 文件不存在或时间无效时忽略 */ }
        }

        /// <summary>
        /// 在刚写完的文件句柄上直接设置修改时间。
        /// 海量小文件时 File.SetLastWriteTime 要为每个文件再打开/关闭一次，
        /// 实测这部分占小文件解压总耗时的大半。
        /// </summary>
        private static void ApplyTimestamp(FileStream stream, DateTime time)
        {
            if (time <= DateTime.MinValue) return;

            try
            {
                // 有缓冲的文件要先落盘，否则关闭时的写盘会把刚设置的时间覆盖掉
                stream.Flush();
                var fileTime = time.ToUniversalTime().ToFileTimeUtc();

                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    Native.SetFileTime(stream.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, ref fileTime);
                    Interlocked.Add(ref EngineTiming.TimeTicks, Stopwatch.GetTimestamp() - start);
                }
                else
                {
                    Native.SetFileTime(stream.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, ref fileTime);
                }
            }
            catch { /* 设置失败不影响文件内容 */ }
        }

        public int CryptoGetTextPassword(out IntPtr password)
        {
            password = Marshal.StringToBSTR(_session.Password ?? "");
            return Native.S_OK;
        }

        private void CloseCurrentStream()
        {
            if (_currentStream != null)
            {
                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    _currentStream.Close();
                    EngineTiming.CloseTicks += Stopwatch.GetTimestamp() - start;
                }
                else
                {
                    _currentStream.Close();
                }

                _currentStream = null;
            }
        }

        /// <summary>目录创建结果做缓存，海量小文件时避免对同一目录重复调用系统 API。</summary>
        private void EnsureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !_createdDirs.Add(dir)) return;
            try
            {
                if (EngineTiming.Enabled)
                {
                    var start = Stopwatch.GetTimestamp();
                    Directory.CreateDirectory(dir);
                    EngineTiming.DirTicks += Stopwatch.GetTimestamp() - start;
                    EngineTiming.DirCalls++;
                }
                else
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch
            {
                _createdDirs.Remove(dir);
            }
        }

        /// <summary>暂停时阻塞 7-Zip 回调线程；取消令牌触发时立即返回 false 中止解压。</summary>
        private bool WaitIfPaused()
        {
            if (_pauseGate == null || _pauseGate.IsSet) return true;

            try
            {
                _pauseGate.Wait(_token);
                return true;
            }
            catch (OperationCanceledException)
            {
                CancelTriggered = true;
                _progress.Cancelled = true;
                return false;
            }
        }

        private static string SanitizeRelativePath(string path)
        {
            var p = path.Replace('/', '\\').TrimStart('\\');

            // 常见路径不含可疑片段，直接返回，避免每个条目都分配分段数组
            if (p.IndexOf("..", StringComparison.Ordinal) < 0 &&
                p.IndexOf("\\\\", StringComparison.Ordinal) < 0 &&
                !(p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])))
                return p;

            var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part != "." && part != "..")
                .ToArray();
            if (parts.Length == 0) return "";
            var result = string.Join("\\", parts);
            if (result.Length >= 2 && result[1] == ':' && char.IsLetter(result[0]))
                result = result.Substring(2).TrimStart('\\');
            return result;
        }
    }

    [ComVisible(true)]
    public sealed class ArchiveUpdateCallback : IArchiveUpdateCallback, IArchiveUpdateCallback2,
        ICryptoGetTextPassword, ICryptoGetTextPassword2
    {
        private readonly IReadOnlyList<SevenZipEngine.SourceItem> _items;
        private readonly string _password;
        private readonly ProgressState _progress;
        private readonly CancellationToken _token;
        private readonly ulong _totalBytes;

        public int Errors;
        public int CompletedItems;
        public bool CancelTriggered;

        public ArchiveUpdateCallback(IReadOnlyList<SevenZipEngine.SourceItem> items, string password,
            ProgressState progress, CancellationToken token, ulong totalBytes)
        {
            _items = items;
            _password = password;
            _progress = progress;
            _token = token;
            _totalBytes = totalBytes;
        }

        public int SetTotal(ulong total)
        {
            _progress.Total = total > 0 ? (long)total : (long)_totalBytes;
            return Native.S_OK;
        }

        public int SetCompleted(IntPtr completeValue)
        {
            var t0 = EngineTiming.Enabled ? Stopwatch.GetTimestamp() : 0L;
            if (completeValue != IntPtr.Zero)
                _progress.Completed = Marshal.ReadInt64(completeValue);
            if (EngineTiming.Enabled)
            {
                EngineTiming.CompletedCalls++;
                EngineTiming.CallbackTicks += Stopwatch.GetTimestamp() - t0;
            }
            if (_token.IsCancellationRequested)
            {
                CancelTriggered = true;
                _progress.Cancelled = true;
                return Native.E_ABORT;
            }
            return Native.S_OK;
        }

        public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive)
        {
            if (newData != IntPtr.Zero) Marshal.WriteInt32(newData, 1);
            if (newProps != IntPtr.Zero) Marshal.WriteInt32(newProps, 1);
            if (indexInArchive != IntPtr.Zero) Marshal.WriteInt32(indexInArchive, -1);
            return Native.S_OK;
        }

        public int GetProperty(uint index, uint propId, IntPtr value)
        {
            var t0 = EngineTiming.Enabled ? Stopwatch.GetTimestamp() : 0L;
            if (value == IntPtr.Zero) return Native.E_POINTER;
            for (var i = 0; i < Native.PropVariantSize; i++) Marshal.WriteByte(value, i, 0);
            if (index >= _items.Count) return Native.S_OK;
            var item = _items[(int)index];

            switch (propId)
            {
                case PropIds.Path:
                    Marshal.WriteInt16(value, 0, (short)VarTypes.VT_BSTR);
                    Marshal.WriteIntPtr(value, 8, Marshal.StringToBSTR(item.ArcPath));
                    break;
                case PropIds.IsDir:
                    Marshal.WriteInt16(value, 0, (short)VarTypes.VT_BOOL);
                    Marshal.WriteInt16(value, 8, item.IsDir ? (short)-1 : (short)0);
                    break;
                case PropIds.Size:
                    Marshal.WriteInt16(value, 0, (short)VarTypes.VT_UI8);
                    Marshal.WriteInt64(value, 8, item.IsDir ? 0L : (long)Math.Max(0, item.Size));
                    break;
                case PropIds.Attrib:
                    Marshal.WriteInt16(value, 0, (short)VarTypes.VT_UI4);
                    Marshal.WriteInt32(value, 8, (int)item.Attrib);
                    break;
                case PropIds.MTime:
                {
                    var ft = item.Modified.ToUniversalTime().ToFileTimeUtc();
                    Marshal.WriteInt16(value, 0, (short)VarTypes.VT_FILETIME);
                    Marshal.WriteInt64(value, 8, ft);
                    break;
                }
            }

            if (EngineTiming.Enabled)
            {
                EngineTiming.PropCalls++;
                EngineTiming.PropTicks += Stopwatch.GetTimestamp() - t0;
            }

            return Native.S_OK;
        }

        public int GetStream(uint index, out ISequentialInStream inStream)
        {
            var t0 = EngineTiming.Enabled ? Stopwatch.GetTimestamp() : 0L;
            inStream = null!;
            if (_token.IsCancellationRequested)
            {
                CancelTriggered = true;
                _progress.Cancelled = true;
                return Native.E_ABORT;
            }

            if (index >= _items.Count) return Native.S_FALSE;
            var item = _items[(int)index];
            if (item.IsDir) return Native.S_OK;

            _progress.CurrentFile = item.ArcPath;
            _progress.ItemIndex = (int)index;

            try
            {
                // 中继缓冲会为每个文件额外分配并拷贝一次，这里直接无缓冲读取（Read 已写入内核缓冲区）
                inStream = new FileSequentialInStream(item.DiskPath);
                CompletedItems++;
                if (EngineTiming.Enabled)
                {
                    EngineTiming.StreamCalls++;
                    EngineTiming.StreamTicks += Stopwatch.GetTimestamp() - t0;
                }
                return Native.S_OK;
            }
            catch
            {
                Errors++;
                return Native.S_FALSE;
            }
        }

        public int SetOperationResult(int operationResult)
        {
            if (EngineTiming.Enabled) EngineTiming.ResultCalls++;
            if (operationResult != 0) Errors++;
            return Native.S_OK;
        }

        public int GetVolumeSize(uint index, out ulong size)
        {
            size = 0;
            return Native.S_FALSE;
        }

        public int GetVolumeStream(uint index, out ISequentialOutStream volumeStream)
        {
            volumeStream = null!;
            return Native.S_FALSE;
        }

        public int CryptoGetTextPassword(out IntPtr password)
        {
            password = Marshal.StringToBSTR(_password ?? "");
            return Native.S_OK;
        }

        public int CryptoGetTextPassword2(out int passwordIsDefined, out IntPtr password)
        {
            passwordIsDefined = string.IsNullOrEmpty(_password) ? 0 : 1;
            password = Marshal.StringToBSTR(_password ?? "");
            return Native.S_OK;
        }
    }

    public sealed class FileSequentialInStream : ISequentialInStream, IDisposable
    {
        private readonly FileStream _stream;

        public FileSequentialInStream(string path)
        {
            // bufferSize=1 表示不做用户态缓冲：数据由 Read 直接写入内核提供的缓冲区，
            // 省掉每个文件一次 16KB 分配与拷贝（海量小文件时这部分开销很可观）
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1,
                FileOptions.SequentialScan);
        }

        /// <summary>直接把源文件读入内核的非托管缓冲区，省去一次中转拷贝。</summary>
        public unsafe int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                var t0 = EngineTiming.Enabled ? Stopwatch.GetTimestamp() : 0L;
                var read = _stream.Read(new Span<byte>((void*)data, (int)size));
                if (EngineTiming.Enabled)
                {
                    EngineTiming.ReadCalls++;
                    EngineTiming.ReadBytes += read;
                    EngineTiming.ReadTicks += Stopwatch.GetTimestamp() - t0;
                }
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, read);
                return Native.S_OK;
            }
            catch
            {
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
                return Native.S_FALSE;
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
