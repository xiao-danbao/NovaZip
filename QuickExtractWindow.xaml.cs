using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NovaZip.Engine;

namespace NovaZip
{
    /// <summary>右键菜单解压的目标方式。</summary>
    public enum QuickExtractMode
    {
        /// <summary>解压到压缩包同名的独立文件夹。</summary>
        Folder,
        /// <summary>直接解压到压缩包所在的当前文件夹。</summary>
        CurrentFolder,
        /// <summary>解压到同名文件夹并自动去除重复层级。</summary>
        Smart
    }

    /// <summary>资源管理器右键「解压 / 压缩」时弹出的独立进度窗口。</summary>
    public partial class QuickExtractWindow : Window
    {
        private readonly string _archivePath;
        private readonly QuickExtractMode _mode;
        private readonly string[] _sources = Array.Empty<string>();
        private readonly string _formatExtension = "";
        private readonly string _formatLabel = "";
        private readonly bool _isCompress;
        private readonly DispatcherTimer _timer;

        private ProgressState? _progress;
        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _pauseGate = new(true);
        private bool _paused;
        private Process? _compressProcess;
        private bool _compressSuspended;
        private string _target = "";
        private long _lastCompleted;
        private double _lastSpeedTime;
        private double _smoothedSpeed;
        private double _percent;

        /// <summary>挂起/恢复 7z.exe 子进程（压缩走外部进程，无法通过内核回调暂停）。</summary>
        [DllImport("ntdll.dll")]
        private static extern uint NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern uint NtResumeProcess(IntPtr processHandle);

        private bool _closing;

        public QuickExtractWindow(string archivePath, QuickExtractMode mode = QuickExtractMode.Folder)
        {
            InitializeComponent();

            _archivePath = archivePath;
            _mode = mode;
            Title = "NovaZip 快速解压";
            HeaderText.Text = "NovaZip · 快速解压";
            ApplyBackdropOpacity();
            _timer = CreateTimer();
        }

        /// <summary>右键「压缩为 …」：打包选中的文件 / 文件夹并显示进度。</summary>
        public QuickExtractWindow(IReadOnlyList<string> sources, string format)
        {
            InitializeComponent();

            _archivePath = "";
            _sources = sources.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            _formatLabel = string.Equals(format, "zip", StringComparison.OrdinalIgnoreCase) ? "ZIP"
                : string.Equals(format, "tar", StringComparison.OrdinalIgnoreCase) ? "TAR"
                : "7Z";
            _formatExtension = "." + _formatLabel.ToLowerInvariant();
            _isCompress = true;
            Title = "NovaZip 快速压缩";
            HeaderText.Text = "NovaZip · 快速压缩";
            ApplyBackdropOpacity();
            _timer = CreateTimer();
        }

        /// <summary>独立窗口同样跟随「窗口背景透明度」设置，0% 时只剩亚克力模糊。</summary>
        private void ApplyBackdropOpacity()
        {
            BackdropLayer.Opacity = Math.Clamp(AppSettings.WindowOpacity, 0.0, 1.0);
        }

        private DispatcherTimer CreateTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            timer.Tick += Timer_Tick;

            Loaded += OnLoaded;
            SourceInitialized += (_, _) => AcrylicHelper.Apply(this);
            ThemeManager.AttachWindow(this);

            // 跟随系统模式下配色实时变化，窗口背景模糊也要同步刷新
            void OnThemeChanged()
            {
                if (_closing) return;
                Dispatcher.BeginInvoke(new Action(() => AcrylicHelper.Apply(this)));
            }

            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (_, _) =>
            {
                ThemeManager.ThemeChanged -= OnThemeChanged;
                _cts?.Cancel();
                Application.Current?.Shutdown();
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) CancelClose_Click(this, new RoutedEventArgs()); };

            return timer;
        }

        private string BusyTitle => _isCompress
            ? "正在压缩为 " + _formatLabel + "…"
            : _mode == QuickExtractMode.CurrentFolder ? "正在解压到当前文件夹…" : "正在解压…";

        private string SuccessTitle => _isCompress ? "压缩完成" : "解压完成";

        private string FailureTitle => _isCompress ? "压缩失败" : "解压失败";

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            PlayEntrance();

            if (_isCompress)
            {
                ArchiveNameText.Text = _sources.Length == 1
                    ? Path.GetFileName(_sources[0].TrimEnd('\\', '/'))
                    : _sources.Length + " 个项目";
                OpenFolderText.Text = "打开所在位置";
                await RunCompressAsync();
                return;
            }

            ArchiveNameText.Text = Path.GetFileName(_archivePath);
            await RunExtractAsync("");
        }

        private void PlayEntrance()
        {
            RootGrid.Opacity = 0;
            var transform = new TranslateTransform(0, 14);
            RootGrid.RenderTransform = transform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        }

        // ==================== 解压流程 ====================

        private async Task RunExtractAsync(string password)
        {
            ShowBusy();

            _cts = new CancellationTokenSource();
            _progress = new ProgressState();
            _percent = 0;
            _lastCompleted = 0;
            _lastSpeedTime = 0;
            _smoothedSpeed = 0;
            _timer.Start();

            OperationResult result;
            try
            {
                var token = _cts.Token;
                result = await Task.Run(() =>
                {
                    _target = _mode == QuickExtractMode.CurrentFolder
                        ? Path.GetDirectoryName(_archivePath) ?? AppContext.BaseDirectory
                        : ArchivePaths.BuildExtractFolder(_archivePath);

                    if (ArchiveEditor.IsSplitVolume(_archivePath))
                    {
                        if (_progress != null)
                        {
                            _progress.Total = 100;
                            _progress.Completed = 40;
                        }

                        var (ok, message) = ArchiveEditor.ExtractVolume(_archivePath, _target, password);

                        if (_progress != null) _progress.Completed = ok ? 100 : 40;
                        if (ok && AppSettings.FixLegacyNames)
                            ArchivePaths.RepairLegacyNames(_target, token);

                        return new OperationResult { Success = ok, Message = message };
                    }

                    using var session = SevenZipEngine.Open(_archivePath, password);
                    var item = SevenZipEngine.Extract(session, null, _target, _progress!, token, _pauseGate);

                    if (item.Success)
                    {
                        var flatten = _mode == QuickExtractMode.Smart ||
                                      (_mode == QuickExtractMode.Folder && AppSettings.SmartExtract);
                        if (flatten && _mode != QuickExtractMode.CurrentFolder)
                            ArchivePaths.FlattenSingleRoot(session, _target);

                        // 只有确实存在可疑乱码名称时才扫描磁盘，避免每次解压都全量遍历
                        if (AppSettings.FixLegacyNames && ArchivePaths.MayNeedRepair(session.Entries.Select(e => e.Path)))
                            ArchivePaths.RepairLegacyNames(_target, token);
                    }

                    return item;
                });
            }
            catch (Exception ex)
            {
                result = new OperationResult { Success = false, Message = ex.Message };
            }
            finally
            {
                _timer.Stop();
                _pauseGate.Set();
                _paused = false;
                if (_compressSuspended)
                {
                    try { NtResumeProcess(_compressProcess!.Handle); } catch { /* 进程可能已退出 */ }
                    _compressSuspended = false;
                }
                _compressProcess = null;
                _cts?.Dispose();
                _cts = null;
                _progress = null;
            }

            if (_closing) return;

            if (result.WrongPassword)
            {
                ShowPassword(password.Length > 0 ? "密码不正确，请重试" : "");
                return;
            }

            if (result.Cancelled)
            {
                Close();
                return;
            }

            ShowResult(result.Success, result.Success
                ? "已解压到 " + _target
                : FirstLine(result.Message));
        }

        // ==================== 压缩流程 ====================

        private async Task RunCompressAsync()
        {
            ShowBusy();

            _cts = new CancellationTokenSource();
            _progress = new ProgressState();
            _percent = 0;
            _lastCompleted = 0;
            _lastSpeedTime = 0;
            _smoothedSpeed = 0;
            _timer.Start();

            OperationResult result;
            try
            {
                var token = _cts.Token;
                result = await Task.Run(() =>
                {
                    var items = SevenZipEngine.CollectSources(_sources, AppSettings.ExcludeJunk);
                    if (items.Count == 0)
                        return new OperationResult { Success = false, Message = "没有可压缩的内容（可能都被排除规则跳过）。" };

                    _target = BuildOutputPath(_sources[0]);
                    // 与主界面一致：优先 7z.exe 子进程（海量小文件快数倍），无法映射路径时回退内核
                    var created = CompressProcess.TryCompress(items, _target, "", 5, AppSettings.ExcludeJunk,
                                      _progress!, token, p => _compressProcess = p)
                                  ?? SevenZipEngine.Create(items, _target, "", 5, _progress!, token);
                    if (!created.Success && File.Exists(_target)) TryDelete(_target);
                    return created;
                });
            }
            catch (Exception ex)
            {
                result = new OperationResult { Success = false, Message = ex.Message };
            }
            finally
            {
                _timer.Stop();
                _pauseGate.Set();
                _paused = false;
                if (_compressSuspended)
                {
                    try { NtResumeProcess(_compressProcess!.Handle); } catch { /* 进程可能已退出 */ }
                    _compressSuspended = false;
                }
                _compressProcess = null;
                _cts?.Dispose();
                _cts = null;
                _progress = null;
            }

            if (_closing) return;

            if (result.Cancelled)
            {
                TryDelete(_target);
                Close();
                return;
            }

            ShowResult(result.Success, result.Success
                ? "已生成 " + _target
                : FirstLine(result.Message));
        }

        /// <summary>在源文件 / 文件夹所在目录生成不重名的压缩包路径。</summary>
        private string BuildOutputPath(string source)
        {
            var full = Path.GetFullPath(source).TrimEnd('\\', '/');
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;

            var baseName = Path.GetFileName(full);
            if (Directory.Exists(full)) baseName = new DirectoryInfo(full).Name;
            else baseName = Path.GetFileNameWithoutExtension(full);
            if (string.IsNullOrEmpty(baseName)) baseName = "archive";

            var target = Path.Combine(dir, baseName + _formatExtension);
            for (var i = 2; i < 1000 && File.Exists(target); i++)
                target = Path.Combine(dir, baseName + " (" + i + ")" + _formatExtension);

            return target;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (path.Length > 0 && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除残留文件失败时忽略
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_progress == null) return;

            var total = _progress.Total;
            var completed = _progress.Completed;
            var target = total > 0 ? Math.Min(100, completed * 100.0 / total) : 0;

            // 平滑推进，避免进度条跳变
            _percent += (target - _percent) * 0.35;
            if (Math.Abs(target - _percent) < 0.4) _percent = target;

            Bar.Value = _percent;
            BarPercentText.Text = _percent.ToString("0") + "%";

            FileText.Text = string.IsNullOrEmpty(_progress.CurrentFile)
                ? "正在准备…"
                : _progress.CurrentFile;

            StatsText.Text = BuildStats(total, completed);
        }

        private string BuildStats(long total, long completed)
        {
            var now = Environment.TickCount64 / 1000.0;
            if (_lastSpeedTime > 0)
            {
                var delta = now - _lastSpeedTime;
                if (delta >= 0.25)
                {
                    var instant = (completed - _lastCompleted) / delta;
                    _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.7 + instant * 0.3;
                    _lastCompleted = completed;
                    _lastSpeedTime = now;
                }
            }
            else
            {
                _lastSpeedTime = now;
                _lastCompleted = completed;
            }

            var text = total > 0
                ? MainWindow.FormatSize((ulong)Math.Max(0, completed)) + " / " + MainWindow.FormatSize((ulong)Math.Max(0, total))
                : MainWindow.FormatSize((ulong)Math.Max(0, completed));

            if (_smoothedSpeed > 0)
                text += "  ·  " + MainWindow.FormatSize((ulong)_smoothedSpeed) + "/s";

            return text;
        }

        // ==================== 状态切换 ====================

        private void ShowBusy()
        {
            BusyPanel.Visibility = Visibility.Visible;
            BusyButtons.Visibility = Visibility.Visible;
            DonePanel.Visibility = Visibility.Collapsed;
            DoneButtons.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            PasswordButtons.Visibility = Visibility.Collapsed;

            // 解压走引擎回调原位暂停；压缩挂起 7z.exe 子进程
            _paused = false;
            _pauseGate.Set();
            _compressSuspended = false;
            PauseQuickButton.Content = "暂停";
            PauseQuickButton.Visibility = Visibility.Visible;

            // 上一轮收尾可能给进度条挂了补满动画，重新开始时先清掉，确保从 0 起步
            Bar.BeginAnimation(ProgressBar.ValueProperty, null);
            Bar.Value = 0;
            BarPercentText.Text = "0%";
            StatusTitle.Text = BusyTitle;
            FileText.Text = "正在准备…";
            StatsText.Text = "";
        }

        private void ShowPassword(string error)
        {
            BusyPanel.Visibility = Visibility.Collapsed;
            BusyButtons.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Collapsed;
            DoneButtons.Visibility = Visibility.Collapsed;

            PasswordPanel.Visibility = Visibility.Visible;
            PasswordButtons.Visibility = Visibility.Visible;
            PasswordInput.Password = "";
            PasswordError.Text = error;
            PasswordError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => PasswordInput.Focus()), DispatcherPriority.Input);
        }

        private async void ShowResult(bool success, string message)
        {
            if (success)
            {
                // 收尾时进度条与百分比同时补满，并短暂停留，避免在 9x% 就被切换掉
                Bar.BeginAnimation(ProgressBar.ValueProperty, null);
                Bar.Value = 100;
                BarPercentText.Text = "100%";
                await Task.Delay(240);
                if (_closing) return;
            }

            BusyPanel.Visibility = Visibility.Collapsed;
            BusyButtons.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            PasswordButtons.Visibility = Visibility.Collapsed;

            DonePanel.Visibility = Visibility.Visible;
            DoneButtons.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility = success ? Visibility.Visible : Visibility.Collapsed;

            DoneTitle.Text = success ? SuccessTitle : FailureTitle;
            DoneDesc.Text = message;
            DoneIcon.Data = (Geometry)FindResource(success ? "IconCheck" : "IconClose");
            DoneIcon.Stroke = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
            DoneBadge.Background = (Brush)FindResource(success ? "SuccessSoftBgBrush" : "WarnSoftBgBrush");

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoneScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
            DoneScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
        }

        // ==================== 交互 ====================

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            StatusTitle.Text = "正在取消…";
        }

        private void PauseQuick_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null) return;

            if (_paused)
            {
                _paused = false;
                if (_isCompress)
                {
                    if (_compressProcess != null && _compressSuspended)
                    {
                        try { NtResumeProcess(_compressProcess.Handle); } catch { /* 进程可能已退出 */ }
                    }
                    _compressSuspended = false;
                }
                else
                {
                    _pauseGate.Set();
                }
                PauseQuickButton.Content = "暂停";
                StatusTitle.Text = BusyTitle;
            }
            else
            {
                _paused = true;
                if (_isCompress)
                {
                    if (_compressProcess != null && !_compressProcess.HasExited)
                    {
                        try { NtSuspendProcess(_compressProcess.Handle); _compressSuspended = true; }
                        catch { /* 进程可能刚好退出 */ }
                    }
                }
                else
                {
                    _pauseGate.Reset();
                }
                PauseQuickButton.Content = "继续";
                StatusTitle.Text = "已暂停";
            }
        }

        private void CancelClose_Click(object sender, RoutedEventArgs e)
        {
            _closing = true;
            _cts?.Cancel();
            Close();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(_target)) ArchivePaths.OpenInExplorer(_target);
        }

        private async void PasswordOk_Click(object sender, RoutedEventArgs e)
        {
            var password = PasswordInput.Password;
            if (password.Length == 0)
            {
                PasswordError.Text = "请输入密码";
                PasswordError.Visibility = Visibility.Visible;
                return;
            }

            await RunExtractAsync(password);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* 非左键拖动时忽略 */ }
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var idx = text.IndexOf('\n');
            return idx < 0 ? text : text.Substring(0, idx).Trim();
        }
    }
}
