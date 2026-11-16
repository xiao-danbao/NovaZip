using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NovaZip.Engine;

namespace NovaZip
{
    internal enum QueueState
    {
        Pending,
        Running,
        Done,
        Failed,
        Cancelled,
        Interrupted
    }

    /// <summary>任务队列中的单个解压任务，属性变更直接驱动右侧面板绑定。</summary>
    internal sealed class QueueItem : INotifyPropertyChanged
    {
        private QueueState _state = QueueState.Pending;
        private double _percent;
        private string _detail = "等待开始…";
        private bool _canResume;

        public QueueItem(string path)
        {
            FilePath = path;
            Name = Path.GetFileName(path);
        }

        public string FilePath { get; }
        public string Name { get; }
        public string Target { get; private set; } = "";
        public string Folder => Path.GetDirectoryName(FilePath) ?? "";

        public QueueState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                Raise(nameof(State));
                Raise(nameof(StateLabel));
                Raise(nameof(IsRunning));
                Raise(nameof(IsFinished));
                Raise(nameof(CanOpen));
            }
        }

        public string StateLabel => _state switch
        {
            QueueState.Running => "进行中",
            QueueState.Done => "已完成",
            QueueState.Failed => "失败",
            QueueState.Cancelled => "已取消",
            QueueState.Interrupted => "已中断",
            _ => "等待中"
        };

        public bool IsRunning => _state == QueueState.Running;

        public bool IsFinished => _state == QueueState.Done ||
                                  _state == QueueState.Failed ||
                                  _state == QueueState.Cancelled;

        public bool CanOpen => _state == QueueState.Done && !string.IsNullOrEmpty(Target) && Directory.Exists(Target);

        /// <summary>存在断点记录且任务已停止时允许一键继续。</summary>
        public bool CanResume
        {
            get => _canResume;
            private set
            {
                if (_canResume == value) return;
                _canResume = value;
                Raise(nameof(CanResume));
            }
        }

        public double Percent
        {
            get => _percent;
            private set
            {
                if (Math.Abs(_percent - value) < 0.01) return;
                _percent = value;
                Raise(nameof(Percent));
                Raise(nameof(PercentText));
            }
        }

        public string PercentText => ((int)Math.Round(_percent)) + "%";

        public string Detail
        {
            get => _detail;
            set
            {
                if (_detail == value) return;
                _detail = value;
                Raise(nameof(Detail));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetTarget(string target)
        {
            Target = target;
            Raise(nameof(CanOpen));
        }

        /// <summary>根据断点记录刷新「继续」按钮的可见性。</summary>
        public void RefreshResumable()
        {
            CanResume = State != QueueState.Done &&
                        State != QueueState.Running &&
                        ExtractCoordinator.HasRecordFile(FilePath);
        }

        public void Report(ProgressState progress)
        {
            var total = progress.Total;
            var completed = progress.Completed;

            Percent = total > 0 ? Math.Min(100, completed * 100.0 / total) : 0;

            if (TaskQueue.IsPaused)
            {
                Detail = "已暂停 · 点击「继续」恢复";
                return;
            }

            Detail = string.IsNullOrEmpty(progress.CurrentFile) ? "正在准备…" : progress.CurrentFile;
        }

        /// <summary>队列暂停时立即更新运行项提示（引擎阻塞期间没有进度回调）。</summary>
        public void MarkPaused() => Detail = "已暂停 · 点击下方「继续」恢复";

        public void Finish(QueueState state, string detail, double percent)
        {
            State = state;
            Detail = detail;
            Percent = percent;
            RefreshResumable();
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>顺序执行解压任务的后台队列，支持断点续传、磁盘分批暂停与取消。</summary>
    internal static class TaskQueue
    {
        public static ObservableCollection<QueueItem> Items { get; } = new();

        public static event Action? Changed;

        /// <summary>用户点了「继续」：通知正在等待磁盘空间的批次接着解压。</summary>
        internal static event Action? DiskResumeRequested;

        private static readonly Queue<QueueItem> Waiting = new();
        private static readonly ManualResetEventSlim PauseGate = new(true);
        private static bool _running;
        private static CancellationTokenSource? _cts;

        public static bool IsPaused { get; private set; }

        public static int ActiveCount => Items.Count(i => !i.IsFinished);

        public static int TotalCount => Items.Count;

        public static void Pause()
        {
            if (IsPaused) return;
            IsPaused = true;
            PauseGate.Reset();
            foreach (var item in Items.Where(i => i.State == QueueState.Running))
                item.MarkPaused();
            Changed?.Invoke();
        }

        public static void Resume()
        {
            if (!IsPaused) return;
            IsPaused = false;
            PauseGate.Set();
            DiskResumeRequested?.Invoke();
            Changed?.Invoke();
        }

        public static void Enqueue(IEnumerable<string> paths)
        {
            var added = 0;

            foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var item = new QueueItem(path);
                Items.Add(item);
                Waiting.Enqueue(item);
                added++;
            }

            if (added == 0) return;

            Changed?.Invoke();
            SaveSnapshot();
            if (!_running) _ = PumpAsync();
        }

        /// <summary>重新入队一个已停止且带断点的任务（替换原条目）。</summary>
        public static void ResumeItem(QueueItem item)
        {
            Items.Remove(item);
            SaveSnapshot();
            Enqueue(new[] { item.FilePath });
        }

        public static void CancelCurrent() => _cts?.Cancel();

        public static void CancelAll()
        {
            // 先解除暂停，保证被暂停的任务能立即收到取消
            Resume();
            _cts?.Cancel();

            while (Waiting.Count > 0)
            {
                var item = Waiting.Dequeue();
                item.Finish(QueueState.Cancelled, "已取消", 0);
            }

            SaveSnapshot();
        }

        public static void ClearFinished()
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].IsFinished) Items.RemoveAt(i);

            Changed?.Invoke();
            SaveSnapshot();
        }

        /// <summary>手动清理全部断点数据（进度记录、.part 文件与队列快照）。</summary>
        public static void CleanupProgressData()
        {
            if (ActiveCount > 0) return;
            ExtractCoordinator.CleanupAllProgressData();

            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].CanResume || Items[i].State == QueueState.Interrupted)
                    Items[i].Finish(QueueState.Failed, "断点已清理", Items[i].Percent);

            Changed?.Invoke();
        }

        /// <summary>把未完成任务写入快照，供下次启动提示恢复。</summary>
        public static void SaveSnapshot()
        {
            var unfinished = Items
                .Where(i => i.State is QueueState.Pending or QueueState.Running or QueueState.Interrupted)
                .Select(i => i.FilePath);
            ExtractCoordinator.SaveQueueSnapshot(unfinished);
        }

        private static async Task PumpAsync()
        {
            _running = true;

            while (Waiting.Count > 0)
            {
                // 暂停状态下不启动新任务，继续后自动接着排队
                while (IsPaused) await Task.Delay(120);

                var item = Waiting.Dequeue();
                item.State = QueueState.Running;
                item.Detail = ExtractCoordinator.HasRecordFile(item.FilePath)
                    ? "发现断点 · 将从上次进度继续"
                    : "正在读取压缩包…";
                Changed?.Invoke();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var progress = new ProgressState();
                var coordinator = ExtractCoordinator.Create(item.FilePath);
                var target = coordinator.RecordTarget ?? ArchivePaths.BuildExtractFolder(item.FilePath);
                item.SetTarget(target);

                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(140)
                };
                timer.Tick += (_, _) => item.Report(progress);
                timer.Start();

                OperationResult result;
                int repaired = 0;
                var diskPaused = false;

                // 磁盘分批暂停：后台线程触发，暂停队列并提示清理磁盘
                void OnDiskPaused()
                {
                    diskPaused = true;
                    item.Detail = "磁盘空间不足 · 请清理磁盘后点「继续」";
                    Pause();
                }

                coordinator.DiskPaused += OnDiskPaused;

                try
                {
                    result = await Task.Run(() =>
                    {
                        if (ArchiveEditor.IsSplitVolume(item.FilePath))
                        {
                            progress.Total = 100;
                            progress.Completed = 40;
                            var (ok, message) = ArchiveEditor.ExtractVolume(item.FilePath, target, "");
                            progress.Completed = ok ? 100 : 40;
                            if (ok && AppSettings.FixLegacyNames)
                                repaired = ArchivePaths.RepairLegacyNames(target, token);
                            return new OperationResult { Success = ok, Message = message };
                        }

                        using var session = SevenZipEngine.Open(item.FilePath, "");
                        coordinator.Initialize(target, session.Entries);

                        var extract = coordinator.Run(session, progress, token, PauseGate);
                        if (extract.Success)
                        {
                            if (AppSettings.SmartExtract) ArchivePaths.FlattenSingleRoot(session, target);
                            if (AppSettings.FixLegacyNames &&
                                ArchivePaths.MayNeedRepair(session.Entries.Select(e => e.Path)))
                                repaired = ArchivePaths.RepairLegacyNames(target, token);
                        }

                        return extract;
                    });
                }
                catch (Exception ex)
                {
                    result = new OperationResult { Success = false, Message = ex.Message };
                }
                finally
                {
                    coordinator.DiskPaused -= OnDiskPaused;
                }

                timer.Stop();
                _cts.Dispose();
                _cts = null;

                if (result.Cancelled && diskPaused)
                {
                    // 等待磁盘空间期间被取消
                    item.Finish(QueueState.Interrupted, "已中断 · 可继续", item.Percent);
                }
                else if (result.Cancelled)
                {
                    item.Finish(QueueState.Cancelled, "已取消 · 可继续", item.Percent);
                }
                else if (result.Success)
                {
                    var suffix = repaired > 0 ? " · 修复 " + repaired + " 个乱码名称" : "";
                    item.Finish(QueueState.Done, "已解压到 " + item.Target + suffix, 100);
                }
                else
                {
                    var message = result.WrongPassword
                        ? "需要密码，请在主界面打开并输入密码"
                        : FirstLine(result.Message);
                    item.Finish(QueueState.Failed, message, item.Percent);
                }

                Changed?.Invoke();
                SaveSnapshot();
            }

            _running = false;
            Changed?.Invoke();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "解压失败";
            var idx = text.IndexOf('\n');
            return (idx < 0 ? text : text.Substring(0, idx)).Trim();
        }
    }
}
