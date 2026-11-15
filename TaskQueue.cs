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
        Cancelled
    }

    /// <summary>任务队列中的单个解压任务，属性变更直接驱动右侧面板绑定。</summary>
    internal sealed class QueueItem : INotifyPropertyChanged
    {
        private QueueState _state = QueueState.Pending;
        private double _percent;
        private string _detail = "等待开始…";

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
            _ => "等待中"
        };

        public bool IsRunning => _state == QueueState.Running;

        public bool IsFinished => _state == QueueState.Done ||
                                  _state == QueueState.Failed ||
                                  _state == QueueState.Cancelled;

        public bool CanOpen => _state == QueueState.Done && !string.IsNullOrEmpty(Target) && Directory.Exists(Target);

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

        public void Finish(QueueState state, string detail, double percent)
        {
            State = state;
            Detail = detail;
            Percent = percent;
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>顺序执行解压任务的后台队列，支持取消当前任务与清理已完成记录。</summary>
    internal static class TaskQueue
    {
        public static ObservableCollection<QueueItem> Items { get; } = new();

        public static event Action? Changed;

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
            Changed?.Invoke();
        }

        public static void Resume()
        {
            if (!IsPaused) return;
            IsPaused = false;
            PauseGate.Set();
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
            if (!_running) _ = PumpAsync();
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
        }

        public static void ClearFinished()
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].IsFinished) Items.RemoveAt(i);

            Changed?.Invoke();
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
                item.Detail = "正在读取压缩包…";
                Changed?.Invoke();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var progress = new ProgressState();
                var target = ArchivePaths.BuildExtractFolder(item.FilePath);
                item.SetTarget(target);

                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(140)
                };
                timer.Tick += (_, _) => item.Report(progress);
                timer.Start();

                OperationResult result;
                int repaired = 0;

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
                        var extract = SevenZipEngine.Extract(session, null, target, progress, token, PauseGate);
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

                timer.Stop();
                _cts.Dispose();
                _cts = null;

                if (result.Cancelled)
                {
                    item.Finish(QueueState.Cancelled, "已取消", item.Percent);
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
