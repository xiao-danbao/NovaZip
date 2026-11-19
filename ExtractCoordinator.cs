using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NovaZip.Engine;

namespace NovaZip
{
    /// <summary>磁盘空间预算：分批解压时每批最多写这么多字节，超出即中止等待续批。</summary>
    public sealed class DiskBudget
    {
        public long Remaining;

        public DiskBudget(long remaining) => Remaining = remaining;

        public bool TryConsume(long bytes)
        {
            if (Remaining <= 0) return false;
            Remaining = Math.Max(0, Remaining - bytes);
            return true;
        }
    }

    /// <summary>解压引擎钩子：断点续写计划、逐条目完成通知与磁盘预算。</summary>
    public sealed class ExtractHooks
    {
        /// <summary>仅用于超预算的大文件单独成批：写满预算即中止，等待下一批续写。</summary>
        public DiskBudget? Budget;

        /// <summary>预算耗尽后由写流回调置位，用于区分「用户取消」与「磁盘分批中止」。</summary>
        public volatile bool BudgetExhausted;

        /// <summary>返回指定条目的续写计划（.part 路径与已写字节偏移），null 表示直接写最终文件。</summary>
        public Func<int, (string PartPath, long Offset)?>? GetResumePlan;

        /// <summary>条目输出流关闭后回调：ok=false 表示该条目失败，需要重试或标记错误。</summary>
        public Action<int, string, bool>? OnEntryFinished;
    }

    /// <summary>断点进度记录，持久化到本地应用数据目录，完成即删、中断保留。</summary>
    internal sealed class ExtractRecord
    {
        public string Archive { get; set; } = "";
        public long ArchiveSize { get; set; }
        public DateTime ArchiveWriteUtc { get; set; }
        public string Target { get; set; } = "";
        public long TotalBytes { get; set; }
        public List<int> DoneIndices { get; set; } = new();
        public long DoneBytes { get; set; }
        public int PartialIndex { get; set; } = -1;
        public long PartialBytes { get; set; }
        public string PartialPartPath { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>
    /// 解压断点续传协调器：文件级跳过 + 大文件字节级续写 + 磁盘空间分批。
    /// 进度文件保存在 %LOCALAPPDATA%\NovaZip\progress，全部完成后自动删除，
    /// 中断或失败时保留，直到用户在任务面板手动清理。
    /// </summary>
    internal sealed class ExtractCoordinator
    {
        /// <summary>继续下一批前要求的最小可用预算，避免刚清出的空间不够写一个块就再次暂停。</summary>
        private const long MinBatchBudget = 64L * 1024 * 1024;

        /// <summary>
        /// 可用空间低于 MinBatchBudget 但仍能写下一小块时，不再一律暂停：
        /// 按可用空间给最小批次继续推进，只有低于该下限才暂停等待清理。
        /// </summary>
        private const long MinDiskSliver = 8L * 1024 * 1024;

        private static readonly string ProgressDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaZip", "progress");

        private static string QueueSnapshotFileActual => Path.Combine(ProgressDirActual, "queue.json");

        /// <summary>测试钩子：重定向进度目录（--resumetest 写入临时目录，避免干扰真实数据）。</summary>
        internal static string? ProgressDirOverride;

        private static string ProgressDirActual => ProgressDirOverride ?? ProgressDir;

        private readonly string _archivePath;
        private readonly long _archiveSize;
        private readonly DateTime _archiveWriteUtc;
        private readonly List<ArchiveEntry> _entries = new();
        private readonly ManualResetEventSlim _resumeGate = new(false);
        private readonly object _sync = new();

        private ExtractRecord? _record;
        private readonly HashSet<int> _hardFailed = new();
        private readonly HashSet<int> _retried = new();
        private readonly HashSet<int> _verifiedDone = new();
        private readonly HashSet<int> _failedThisBatch = new();

        private int _giantIndex = -1;
        private bool _resumeRequested;
        private bool _subscribed;
        private long _lastSaveTime;
        private bool _recordDirty;

        /// <summary>磁盘分批暂停时触发（后台线程），供 UI 提示清理磁盘后继续。</summary>
        public event Action? DiskPaused;

        /// <summary>最近一次磁盘暂停的原因，供界面展示具体数值（为空时用通用文案）。</summary>
        public string LastDiskPauseReason { get; private set; } = "";

        private ExtractCoordinator(string archivePath, ExtractRecord? record)
        {
            _archivePath = archivePath;
            _record = record;
            _archiveSize = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
            _archiveWriteUtc = File.Exists(archivePath)
                ? File.GetLastWriteTimeUtc(archivePath)
                : DateTime.MinValue;
        }

        /// <summary>已有断点记录的目标目录；无记录或目录为空时返回 null。</summary>
        public string? RecordTarget =>
            _record != null && !string.IsNullOrEmpty(_record.Target) ? _record.Target : null;

        public bool HasRecord => _record != null;

        // ==================== 测试钩子（仅 --resumetest 使用） ====================

        /// <summary>大于 0 时强制磁盘预算字节数，用于无界面验证分批与续写链路。</summary>
        internal long ForceBudgetBytes;

        internal ExtractRecord? TestRecord => _record;

        internal static string TestRecordFileFor(string archivePath) => RecordFileFor(archivePath);

        // ==================== 记录查找 ====================

        private static string RecordFileFor(string archivePath)
        {
            // 记录按压缩包路径定位；恢复时校验内容一致性，不匹配即视为失效
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(archivePath.ToLowerInvariant()));
            return Path.Combine(ProgressDirActual, Convert.ToHexString(hash)[..24] + ".json");
        }

        /// <summary>检查指定压缩包是否存在断点记录文件（不校验内容）。</summary>
        public static bool HasRecordFile(string archivePath) => File.Exists(RecordFileFor(archivePath));

        /// <summary>载入指定压缩包的断点记录；记录损坏或无有效内容时返回 null 并清理。</summary>
        public static ExtractCoordinator? Begin(string archivePath)
        {
            try
            {
                var file = RecordFileFor(archivePath);
                if (!File.Exists(file)) return null;

                var record = JsonSerializer.Deserialize<ExtractRecord>(File.ReadAllText(file));
                if (record == null || !File.Exists(archivePath))
                {
                    TryDelete(file);
                    return null;
                }

                if (record.DoneIndices.Count == 0 && record.PartialIndex < 0)
                {
                    // 空记录没有可恢复内容，直接清掉避免反复提示
                    TryDelete(file);
                    DeletePartFile(record.PartialPartPath);
                    return null;
                }

                return new ExtractCoordinator(archivePath, record);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>创建协调器：有断点则接管，否则以全新记录开始（用于随时保存进度）。</summary>
        public static ExtractCoordinator Create(string archivePath)
        {
            return Begin(archivePath) ?? new ExtractCoordinator(archivePath, null);
        }

        /// <summary>扫描进度目录，返回仍有效的断点记录（压缩包在位且已有已完成内容）。</summary>
        public static List<ExtractRecord> LoadInterruptedRecords()
        {
            var list = new List<ExtractRecord>();
            try
            {
                if (!Directory.Exists(ProgressDirActual)) return list;
                foreach (var file in Directory.GetFiles(ProgressDirActual, "*.json"))
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<ExtractRecord>(File.ReadAllText(file));
                        if (record == null || string.IsNullOrEmpty(record.Archive)) continue;
                        if (!File.Exists(record.Archive)) continue;
                        if (record.DoneIndices.Count == 0 && record.PartialIndex < 0) continue;
                        list.Add(record);
                    }
                    catch { /* 单条记录损坏时跳过 */ }
                }
            }
            catch { /* 目录不可读时静默返回 */ }
            return list;
        }

        // ==================== 初始化 ====================

        /// <summary>绑定解压目标目录与条目列表。已有断点且目标一致时自动接管，否则新建记录。</summary>
        public void Initialize(string target, IReadOnlyList<ArchiveEntry> entries)
        {
            lock (_sync)
            {
                _entries.Clear();
                _entries.AddRange(entries);

                if (_record != null && (string.IsNullOrEmpty(_record.Target) ||
                                        string.Equals(_record.Target, target, StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrEmpty(_record.Target)) _record.Target = target;

                    // 条目总大小与记录不一致（包内容变了）时弃用旧进度
                    var total = _entries.Where(e => !e.IsDir).Sum(e => (long)e.Size);
                    if (_record.TotalBytes > 0 && total != _record.TotalBytes)
                    {
                        DeletePartFile(_record.PartialPartPath);
                        _record = null;
                    }
                }
                else if (_record != null)
                {
                    DeletePartFile(_record.PartialPartPath);
                    _record = null;
                }

                if (_record == null)
                {
                    _record = new ExtractRecord
                    {
                        Archive = _archivePath,
                        ArchiveSize = _archiveSize,
                        ArchiveWriteUtc = _archiveWriteUtc,
                        Target = target,
                        TotalBytes = _entries.Where(e => !e.IsDir).Sum(e => (long)e.Size)
                    };
                }

                _recordDirty = true;
                SaveLocked(force: true);
            }
        }

        // ==================== 批次执行 ====================

        /// <summary>
        /// 分批执行解压直到全部完成。磁盘空间不足时触发 DiskPaused 并阻塞等待
        /// 继续信号；用户取消时返回 Cancelled 结果并保留断点记录。
        /// diskBatching=false 时关闭磁盘分批（用于无「继续」入口的主界面解压），
        /// 空间不足时由写入错误自然报失败。
        /// </summary>
        public OperationResult Run(ArchiveSession session, ProgressState progress,
            CancellationToken token, ManualResetEventSlim? pauseGate, bool diskBatching = true)
        {
            if (!_subscribed)
            {
                TaskQueue.DiskResumeRequested += OnDiskResume;
                _subscribed = true;
            }

            try
            {
                return RunCore(session, progress, token, pauseGate, diskBatching);
            }
            catch (OperationCanceledException)
            {
                SaveProgress(force: true);
                return new OperationResult { Cancelled = true, Message = "操作已取消。" };
            }
            catch (Exception ex)
            {
                SaveProgress(force: true);
                return new OperationResult { Success = false, Message = ex.Message };
            }
            finally
            {
                TaskQueue.DiskResumeRequested -= OnDiskResume;
                _subscribed = false;
            }
        }

        private void OnDiskResume() => RequestResume();

        public void RequestResume()
        {
            _resumeRequested = true;
            _resumeGate.Set();
        }

        /// <summary>磁盘空间不足：保存进度、通知 UI 并阻塞等待继续；返回 true 表示已取消。</summary>
        private bool DiskPauseAndWait(CancellationToken token, string reason = "")
        {
            SaveProgress(force: true);
            LastDiskPauseReason = reason;
            _resumeRequested = false;
            _resumeGate.Reset();
            DiskPaused?.Invoke();

            while (!_resumeGate.Wait(200, token))
            {
                if (token.IsCancellationRequested) return true;
                if (_resumeRequested)
                {
                    _resumeRequested = false;
                    return false;
                }
            }

            _resumeRequested = false;
            return false;
        }

        private OperationResult RunCore(ArchiveSession session, ProgressState progress,
            CancellationToken token, ManualResetEventSlim? pauseGate, bool diskBatching)
        {
            var result = new OperationResult();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    SaveProgress(force: true);
                    result.Cancelled = true;
                    result.Message = "操作已取消。";
                    return result;
                }

                var remaining = ComputeRemaining();
                if (remaining.Count == 0 && _record?.PartialIndex < 0)
                {
                    if (_hardFailed.Count == 0)
                    {
                        CompleteSuccessfully(progress);
                        result.Success = true;
                        result.Items = _verifiedDone.Count;
                        result.Message = "已成功解压 " + result.Items + " 项。";
                        return result;
                    }

                    // 有条目重试后仍失败：报告失败并保留记录（已完成部分可日后继续）
                    SaveProgress(force: true);
                    result.Success = false;
                    result.Message = "有 " + _hardFailed.Count + " 个文件解压失败，其余已完成。";
                    foreach (var index in _hardFailed.Take(5))
                        if (index < _entries.Count) result.ErrorItems.Add(_entries[index].Path);
                    return result;
                }

                var budget = diskBatching
                    ? (ForceBudgetBytes > 0 ? ForceBudgetBytes : ComputeBudget())
                    : long.MaxValue;

                // 真实磁盘可用空间不足下限时暂停等待；强制预算（测试）跳过该闸门，
                // 由批次写满预算后的中止路径负责暂停。
                // 介于「最小块」与「64MB 批次」之间时不再空等：按可用空间给一个小批次继续推进。
                if (diskBatching && ForceBudgetBytes <= 0 && budget < MinBatchBudget)
                {
                    if (budget < MinDiskSliver)
                    {
                        var freeMb = budget == long.MaxValue ? -1 : Math.Max(0, budget * 100 / 80) / (1024 * 1024);
                        var reason = freeMb >= 0
                            ? "磁盘可用空间仅剩约 " + freeMb + " MB，已暂停；清理磁盘后点「继续」即可接着解压"
                            : "磁盘可用空间不足，已暂停；清理磁盘后点「继续」即可接着解压";
                        if (DiskPauseAndWait(token, reason))
                        {
                            result.Cancelled = true;
                            result.Message = "操作已取消。";
                            return result;
                        }
                        continue;
                    }

                    budget = Math.Max(budget, MinDiskSliver);
                }

                var batch = BuildBatch(remaining, budget, out var giantResume);
                if (batch.Count == 0)
                {
                    SaveProgress(force: true);
                    result.Success = false;
                    result.Message = "无法规划解压批次。";
                    return result;
                }

                progress.BaseCompleted = giantResume ? DoneBytes + PartialBytes : DoneBytes;
                progress.FixedTotal = TotalBytes;

                var hooks = new ExtractHooks();
                if (giantResume) hooks.Budget = new DiskBudget(budget);
                hooks.GetResumePlan = GetPartialPlan;
                hooks.OnEntryFinished = OnEntryFinished;

                var extract = SevenZipEngine.Extract(session, batch, Target, progress, token, pauseGate, hooks);
                SevenZipEngine.Trace?.Invoke("[resume] batch=" + batch.Count + " giant=" + giantResume +
                    " cancelled=" + extract.Cancelled + " budgetStop=" + hooks.BudgetExhausted +
                    " ok=" + extract.Success + " wrongPwd=" + extract.WrongPassword +
                    " errors=" + extract.ErrorItems.Count + (extract.ErrorItems.Count > 0
                        ? " [" + string.Join("; ", extract.ErrorItems.Take(3)) + "]" : ""));

                if (extract.Cancelled && hooks.BudgetExhausted)
                {
                    // 磁盘分批中止：记录大文件已写字节，暂停等待用户清理磁盘后继续
                    CapturePartialState();
                    if (DiskPauseAndWait(token,
                            "本批已写满磁盘可用空间，已暂停并保留已写部分；清理磁盘后点「继续」即可从断点续写"))
                    {
                        result.Cancelled = true;
                        result.Message = "操作已取消。";
                        return result;
                    }
                    continue;
                }

                if (extract.Cancelled)
                {
                    SaveProgress(force: true);
                    result.Cancelled = true;
                    result.Message = "操作已取消。";
                    return result;
                }

                if (extract.WrongPassword)
                {
                    SaveProgress(force: true);
                    result.WrongPassword = true;
                    result.Message = "密码错误，无法解密文件。";
                    return result;
                }

                // 本批失败的条目重试一次（不用断点、不带预算），仍失败则硬标记
                RetryFailedEntries(session, progress, token, pauseGate);

                SaveProgress(force: true);
            }
        }

        // ==================== 批次规划 ====================

        private string Target => _record?.Target ?? "";

        private long TotalBytes => _record?.TotalBytes ?? 0;

        private long DoneBytes => _record?.DoneBytes ?? 0;

        private long PartialBytes => _record?.PartialBytes ?? 0;

        private long ComputeBudget()
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(Target)) ?? "";
                if (root.Length == 0) return long.MaxValue;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return long.MaxValue;
                return (long)(drive.AvailableFreeSpace * 0.8);
            }
            catch
            {
                return long.MaxValue;
            }
        }

        /// <summary>待解压条目（仅文件）：已完成且校验在位的跳过，硬失败条目排除。</summary>
        private List<uint> ComputeRemaining()
        {
            var list = new List<uint>();
            var record = _record;
            if (record == null) return list;

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.IsDir || _hardFailed.Contains(i)) continue;

                if (record.DoneIndices.Contains(i))
                {
                    if (_verifiedDone.Contains(i)) continue;

                    // 校验已完成文件仍然在位且大小一致，否则视为未完成重新解压
                    var path = SafeCombine(record.Target, entry.Path);
                    if (path != null && File.Exists(path) && new FileInfo(path).Length == (long)entry.Size)
                    {
                        _verifiedDone.Add(i);
                        continue;
                    }

                    record.DoneIndices.Remove(i);
                    record.DoneBytes -= (long)entry.Size;
                    if (record.DoneBytes < 0) record.DoneBytes = 0;
                    _recordDirty = true;
                }

                list.Add((uint)i);
            }

            return list;
        }

        private List<uint> BuildBatch(List<uint> remaining, long budget, out bool giantResume)
        {
            giantResume = false;
            _giantIndex = -1;
            var batch = new List<uint>();
            var left = budget;

            foreach (var index in remaining)
            {
                if (index >= _entries.Count) continue;
                var entry = _entries[(int)index];

                if ((long)entry.Size <= left)
                {
                    batch.Add(index);
                    left -= (long)entry.Size;
                    continue;
                }

                // 装不下的第一个文件：若整批预算都不够它一个人用，则单独成批，
                // 依赖字节级断点分多次写完；否则留给下一批
                if (batch.Count == 0 && (long)entry.Size > budget)
                {
                    batch.Add(index);
                    _giantIndex = (int)index;
                    giantResume = true;
                }
                else
                {
                    _giantIndex = -1;
                }

                break;
            }

            // 只剩大文件的收尾批次（文件级进度已全部完成，仅续写未完的大文件）
            if (batch.Count == 0 && _record is { PartialIndex: >= 0 } record &&
                record.PartialIndex < _entries.Count && !_hardFailed.Contains(record.PartialIndex))
            {
                var entry = _entries[record.PartialIndex];
                if ((long)entry.Size > budget)
                {
                    batch.Add((uint)record.PartialIndex);
                    _giantIndex = record.PartialIndex;
                    giantResume = true;
                }
            }

            return batch;
        }

        /// <summary>大文件续写计划：分批大文件始终写 .part，成功后由引擎改名成最终文件。</summary>
        private (string PartPath, long Offset)? GetPartialPlan(int index)
        {
            var record = _record;
            if (record == null || index >= _entries.Count || _entries[index].IsDir) return null;
            if (index != _giantIndex && index != record.PartialIndex) return null;

            var partPath = PartPathFor(Target, _entries[index].Path);
            var offset = 0L;

            if (record.PartialIndex == index)
            {
                if (!string.IsNullOrEmpty(record.PartialPartPath) && File.Exists(record.PartialPartPath))
                {
                    partPath = record.PartialPartPath;
                    offset = record.PartialBytes;
                }

                if (offset > 0)
                {
                    var length = new FileInfo(partPath).Length;
                    if (length < offset)
                    {
                        // .part 比记录短（被外力破坏）：放弃续写，从头重解该文件
                        DeletePartFile(partPath);
                        ClearPartial();
                        offset = 0;
                        partPath = PartPathFor(Target, _entries[index].Path);
                    }
                }
            }

            return (partPath, offset);
        }

        private void CapturePartialState()
        {
            var record = _record;
            if (record == null || _giantIndex < 0 || _giantIndex >= _entries.Count) return;

            var partPath = PartPathFor(Target, _entries[_giantIndex].Path);
            if (!File.Exists(partPath)) return;

            record.PartialIndex = _giantIndex;
            record.PartialPartPath = partPath;
            record.PartialBytes = new FileInfo(partPath).Length;
            _recordDirty = true;
        }

        private void OnEntryFinished(int index, string path, bool ok)
        {
            var record = _record;
            if (record == null || index < 0 || index >= _entries.Count) return;
            if (_entries[index].IsDir) return;

            lock (_sync)
            {
                if (ok)
                {
                    record.DoneBytes += (long)_entries[index].Size;
                    if (!record.DoneIndices.Contains(index)) record.DoneIndices.Add(index);
                    if (record.PartialIndex == index) ClearPartial();
                }
                else
                {
                    // 条目失败：续写文件清掉 .part 下轮从头重解；稍后统一重试一次
                    if (record.PartialIndex == index)
                    {
                        DeletePartFile(record.PartialPartPath);
                        ClearPartial();
                    }
                    if (!_hardFailed.Contains(index)) _failedThisBatch.Add(index);
                }

                _recordDirty = true;
                SaveLocked();
            }
        }

        private void RetryFailedEntries(ArchiveSession session, ProgressState progress,
            CancellationToken token, ManualResetEventSlim? pauseGate)
        {
            var failed = _failedThisBatch.ToList();
            _failedThisBatch.Clear();

            foreach (var index in failed)
            {
                if (token.IsCancellationRequested) return;
                if (_retried.Contains(index))
                {
                    _hardFailed.Add(index);
                    continue;
                }
                _retried.Add(index);

                var retry = SevenZipEngine.Extract(session, new[] { (uint)index }, Target, progress, token,
                    pauseGate, null);
                SevenZipEngine.Trace?.Invoke("[resume] retry #" + index + " ok=" + retry.Success +
                    " cancelled=" + retry.Cancelled + " errors=" + retry.ErrorItems.Count);
                if (retry.Success)
                {
                    // 重试成功也要记账，否则该条目永远留在待解列表里
                    OnEntryFinished(index, _entries[index].Path, true);
                    continue;
                }
                if (retry.Cancelled || retry.WrongPassword) return;
                _hardFailed.Add(index);
            }
        }

        // ==================== 记录读写 ====================

        private void CompleteSuccessfully(ProgressState progress)
        {
            var record = _record;
            if (record != null)
            {
                DeletePartFile(record.PartialPartPath);
                _record = null;
            }

            TryDelete(RecordFileFor(_archivePath));

            progress.BaseCompleted = 0;
            progress.FixedTotal = 0;
            progress.Completed = progress.Total;
        }

        private void SaveProgress(bool force)
        {
            lock (_sync) SaveLocked(force);
        }

        private void SaveLocked(bool force = false)
        {
            if (_record == null) return;
            if (!force && !_recordDirty) return;

            var now = Environment.TickCount64;
            if (!force && now - _lastSaveTime < 800) return;
            _lastSaveTime = now;
            _recordDirty = false;

            try
            {
                Directory.CreateDirectory(ProgressDirActual);
                _record.UpdatedUtc = DateTime.UtcNow;
                var file = RecordFileFor(_archivePath);
                var json = JsonSerializer.Serialize(_record);
                WriteAtomically(file, json);
                SevenZipEngine.Trace?.Invoke("[record] saved done=" + _record.DoneIndices.Count +
                    " partial=" + _record.PartialIndex + "@" + _record.PartialBytes);
            }
            catch (Exception ex)
            {
                SevenZipEngine.Trace?.Invoke("[record] save FAILED: " + ex.GetType().Name + " " + ex.Message);
                // 持久化失败不阻断解压，只影响断点精度
            }
        }

        /// <summary>
        /// 先写临时文件再替换，带短暂重试。杀毒软件常短暂锁定 JSON，
        /// 直接原地重建会遇到 UnauthorizedAccessException。
        /// </summary>
        private static void WriteAtomically(string file, string json)
        {
            var tmp = file + ".tmp";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, json);
                    if (File.Exists(file)) File.Replace(tmp, file, null);
                    else File.Move(tmp, file);
                    return;
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(60);
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(60);
                }
            }
        }

        private void ClearPartial()
        {
            if (_record == null) return;
            _record.PartialIndex = -1;
            _record.PartialBytes = 0;
            _record.PartialPartPath = "";
            _recordDirty = true;
        }

        // ==================== 队列快照 ====================

        /// <summary>保存批量队列的未完成任务列表，供下次启动提示恢复。</summary>
        public static void SaveQueueSnapshot(IEnumerable<string> archivePaths)
        {
            try
            {
                Directory.CreateDirectory(ProgressDirActual);
                var paths = archivePaths.ToList();
                if (paths.Count == 0)
                {
                    TryDelete(QueueSnapshotFileActual);
                    return;
                }

                WriteAtomically(QueueSnapshotFileActual,
                    JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch
            {
                // 忽略快照写入失败
            }
        }

        /// <summary>读取上次未完成的队列任务；无快照时返回空列表。</summary>
        public static List<string> LoadQueueSnapshot()
        {
            try
            {
                if (!File.Exists(QueueSnapshotFileActual)) return new List<string>();
                var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueueSnapshotFileActual));
                return paths?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>手动清理：删除全部断点记录与 .part 临时文件（任务运行时不应调用）。</summary>
        public static void CleanupAllProgressData()
        {
            foreach (var file in SafeList(ProgressDirActual, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<ExtractRecord>(File.ReadAllText(file));
                    if (!string.IsNullOrEmpty(record?.PartialPartPath)) DeletePartFile(record.PartialPartPath);
                }
                catch { /* ignore */ }

                TryDelete(file);
            }

            foreach (var tmp in SafeList(ProgressDirActual, "*.tmp")) TryDelete(tmp);
            TryDelete(QueueSnapshotFileActual);
        }

        // ==================== 工具 ====================

        private static string PartPathFor(string target, string entryPath)
        {
            var relative = entryPath.Replace('/', '\\').TrimStart('\\');
            return Path.Combine(target, relative + ".novapart");
        }

        private static void DeletePartFile(string? partPath)
        {
            if (string.IsNullOrEmpty(partPath)) return;
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* ignore */ }
        }

        private static string? SafeCombine(string target, string entryPath)
        {
            try
            {
                var relative = entryPath.Replace('/', '\\').TrimStart('\\');
                if (relative.Length == 0) return null;
                return Path.Combine(target, relative);
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> SafeList(string dir, string pattern)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* ignore */ }
        }
    }
}
