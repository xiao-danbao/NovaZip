using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using NovaZip.Engine;

namespace NovaZip
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<EntryRow> _rows = new();
        private readonly List<EntryRow> _allRows = new();
        private readonly ObservableCollection<SourceRow> _sources = new();
        private readonly ObservableCollection<HistoryRow> _history = new();
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _searchTimer;
        private readonly Stopwatch _speedWatch = new();

        private ArchiveSession? _session;
        private ProgressState? _progress;
        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _mainPauseGate = new(true);
        private bool _paused;
        private bool _canPause;
        private string _progressTitle = "";
        private TaskCompletionSource<string?>? _passwordTcs;
        private TaskCompletionSource<string?>? _renameTcs;
        private string? _pendingArchivePath;
        private string? _previewTemp;
        private string? _previewFilePath;
        private string? _previewEntryPath;
        private bool _previewEditing;
        private long _lastCompleted;
        private double _lastSpeedTime;
        private double _smoothedSpeed;
        private bool _isDragging;
        private bool _suppressFormatSync;
        private bool _settingsOpen;
        private bool _queueOpen;
        private bool _updatingSettings;
        private FilterKind _filterKind = FilterKind.All;
        private SortMode _sortMode = SortMode.Name;
        private double _themeIconAngle;
        private Point _sourceDragStart;
        private SourceRow? _sourceDragItem;
        private Point _entryDragStart;
        private bool _entryDragArmed;
        private int _pageIndex;

        private enum FilterKind { All, File, Folder, Image, Media, Document, Code }

        private enum SortMode { Name, Size, Date }

        public MainWindow()
        {
            InitializeComponent();

            AppSettings.Load();

            EntryList.ItemsSource = _rows;
            SourceList.ItemsSource = _sources;
            HistoryList.ItemsSource = _history;
            QueueList.ItemsSource = TaskQueue.Items;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _progressTimer.Tick += ProgressTimer_Tick;

            // 搜索输入合并为一次筛选，避免大压缩包逐字重建列表
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                ApplyFilter();
            };

            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            KeyDown += MainWindow_KeyDown;
            DragOver += MainWindow_DragOver;
            DragLeave += MainWindow_DragLeave;
            Drop += MainWindow_Drop;

            Format7z.Checked += Format_Checked;
            FormatZip.Checked += Format_Checked;
            FormatTar.Checked += Format_Checked;

            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.ModeChanged += OnThemeModeChanged;

            // 系统亮暗模式切换时补一条窗口消息通道，跟随系统模式可实时生效
            ThemeManager.AttachWindow(this);
            TaskQueue.Changed += OnQueueChanged;
            Closed += (_, _) =>
            {
                ThemeManager.ThemeChanged -= OnThemeChanged;
                ThemeManager.ModeChanged -= OnThemeModeChanged;
                TaskQueue.Changed -= OnQueueChanged;
                TaskQueue.SaveSnapshot();
                CleanupPreview();
            };

            SourceList.AllowDrop = true;
            SourceList.PreviewMouseLeftButtonDown += SourceList_PreviewMouseLeftButtonDown;
            SourceList.PreviewMouseMove += SourceList_PreviewMouseMove;
            SourceList.Drop += SourceList_Drop;

            EntryList.PreviewMouseLeftButtonDown += EntryList_PreviewMouseLeftButtonDown;
            EntryList.PreviewMouseMove += EntryList_PreviewMouseMove;

            UpdateSourceSummary();
            UpdateHistoryEmpty();
            UpdateQueueBadge();
            SyncThemeVisuals();
            ApplyWindowOpacity();
        }

        public void QueueArchive(string path)
        {
            _pendingArchivePath = path;
        }

        // ==================== Lifecycle ====================

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PlayEntranceAnimation();
            StartAmbientAnimations();
            AnimatePage(PageExtract);
            await InitializeEngineAsync();

            if (_pendingArchivePath != null)
            {
                var path = _pendingArchivePath;
                _pendingArchivePath = null;
                await OpenArchiveAsync(path);
            }

            UpdateQueueBadge();
            await ShowRestoreBannerIfAnyAsync();
            CheckUpdatesOnStartup();
        }

        /// <summary>启动时检查上次未完成的解压任务（队列快照 + 全部断点记录）并弹出恢复提示。</summary>
        private async Task ShowRestoreBannerIfAnyAsync()
        {
            try
            {
                // 进度目录扫描要读多个 JSON，放到线程池，避免阻塞首帧与启动动画
                var pending = await Task.Run(() =>
                {
                    var list = ExtractCoordinator.LoadQueueSnapshot();
                    list.AddRange(ExtractCoordinator.LoadInterruptedRecords()
                        .Select(r => r.Archive));
                    return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                });
                if (pending.Count == 0) return;

                RestoreTitleText.Text = "检测到未完成的解压任务";
                RestoreDetailText.Text = pending.Count == 1
                    ? "「" + Path.GetFileName(pending[0]) + "」上次中断，可从断点继续（实验性功能，不一定生效）"
                    : "共 " + pending.Count + " 个任务未完成，可从断点继续（实验性功能，不一定生效）";
                RestoreBanner.Tag = pending;
                RestoreBanner.Visibility = Visibility.Visible;
            }
            catch
            {
                // 快照损坏时静默跳过
            }
        }

        private void RestoreContinue_Click(object sender, RoutedEventArgs e)
        {
            RestoreBanner.Visibility = Visibility.Collapsed;
            if (RestoreBanner.Tag is List<string> pending)
            {
                TaskQueue.Enqueue(pending);
                ShowToast("已恢复 " + pending.Count + " 个任务 · 将从断点继续", ToastKind.Success);
                OpenQueuePanel();
            }
        }

        private void RestoreDismiss_Click(object sender, RoutedEventArgs e)
        {
            RestoreBanner.Visibility = Visibility.Collapsed;
        }

        private void PlayEntranceAnimation()
        {
            RootGrid.Opacity = 0;
            var transform = new TranslateTransform(0, 12);
            RootGrid.RenderTransform = transform;

            RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            AcrylicHelper.Apply(this);
        }

        // ==================== Theme ====================

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var wasAuto = ThemeManager.Mode == ThemeMode.Auto;
            ThemeManager.Toggle();

            // 跟随系统时点按钮会转为手动模式，明确提示一次，避免用户以为跟随失效
            if (wasAuto)
                ShowToast("已切换为手动" + (ThemeManager.IsDark ? "深色" : "浅色") + " · 不再跟随系统", ToastKind.Info);
        }

        private void OnThemeChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncThemeVisuals();
                SyncSettingsChecks();
                AnimateThemeChange();
            }));
        }

        /// <summary>仅模式变化（例如从深色切到跟随系统且配色恰好相同）时同步文案，不做整窗动画。</summary>
        private void OnThemeModeChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncThemeVisuals();
                SyncSettingsChecks();
            }));
        }

        private void SyncThemeVisuals()
        {
            ThemeSunIcon.Visibility = ThemeManager.IsDark ? Visibility.Visible : Visibility.Collapsed;
            ThemeMoonIcon.Visibility = ThemeManager.IsDark ? Visibility.Collapsed : Visibility.Visible;

            var current = ThemeManager.IsDark ? "深色" : "浅色";
            var next = ThemeManager.IsDark ? "浅色" : "深色";
            ThemeButton.ToolTip = ThemeManager.Mode == ThemeMode.Auto
                ? "跟随系统 · 当前" + current + "，点击切换为手动" + next
                : "当前" + current + "主题，点击切换" + next;

            if (ThemeHintText != null)
                ThemeHintText.Text = ThemeManager.Mode == ThemeMode.Auto
                    ? "已跟随 Windows 亮暗模式 · 当前" + current
                    : "手动选择 · 当前" + current;

            // 拖拽高亮会写入本地画刷，切换主题时重新挂回动态资源
            DropZone.SetResourceReference(Border.BorderBrushProperty, "DropZoneBorderBrush");
            CreateDropZone.SetResourceReference(Border.BorderBrushProperty, "DropZoneBorderBrush");

            ApplyDwmTheme();
        }

        private void ApplyDwmTheme()
        {
            AcrylicHelper.Apply(this);
        }

        private void AnimateThemeChange()
        {
            // 1) 先轻微压暗再回弹，掩盖整窗配色瞬切
            var dip = new DoubleAnimationUsingKeyFrames();
            dip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            dip.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            dip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            RootGrid.BeginAnimation(OpacityProperty, dip);

            // 2) 从右上角主题按钮扩散的柔光涟漪
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ThemeRipple.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(760)) { EasingFunction = ease });
            ThemeRippleScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.08, 1.25, TimeSpan.FromMilliseconds(780)) { EasingFunction = ease });
            ThemeRippleScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.08, 1.25, TimeSpan.FromMilliseconds(780)) { EasingFunction = ease });

            // 3) 主题图标旋转半圈，衔接深浅切换
            var from = _themeIconAngle;
            _themeIconAngle += 180;
            var rotate = new DoubleAnimation(from, _themeIconAngle, TimeSpan.FromMilliseconds(480))
            { EasingFunction = ease };
            rotate.Completed += (_, _) =>
            {
                ThemeIconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                ThemeIconRotate.Angle = _themeIconAngle;
            };
            ThemeIconRotate.BeginAnimation(RotateTransform.AngleProperty, rotate);
        }

        private async Task InitializeEngineAsync()
        {
            try
            {
                var info = await Task.Run(() =>
                {
                    var formats = SevenZipEngine.Formats;
                    return (Count: formats.Count, Dll: SevenZipEngine.DllPath);
                });

                EngineNameText.Text = "7z.dll · 26.06";
                EngineFormatsText.Text = info.Count + " 种格式处理器已就绪";
                EngineFormatsText.ToolTip = info.Dll;
            }
            catch (Exception ex)
            {
                EngineFormatsText.Text = "内核加载失败";
                EngineFormatsText.Foreground = (Brush)FindResource("DangerBrush");
                ShowToast("7-Zip 内核加载失败：" + ex.Message, ToastKind.Error);
            }
        }

        private void StartAmbientAnimations()
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            BlobATransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 60, TimeSpan.FromSeconds(16))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            BlobATransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 34, TimeSpan.FromSeconds(19))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            BlobBTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -70, TimeSpan.FromSeconds(21))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            BlobBTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -40, TimeSpan.FromSeconds(17))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });

            DashRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(38))
            { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* ignore */ }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter &&
                EntryList.IsKeyboardFocusWithin &&
                EntryList.SelectedItem is EntryRow selected &&
                !selected.Entry.IsDir)
            {
                e.Handled = true;
                await PreviewEntryAsync(selected);
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (RenameOverlay.Visibility == Visibility.Visible)
                {
                    CloseRename(null);
                }
                else if (PreviewOverlay.Visibility == Visibility.Visible)
                {
                    ClosePreview();
                }
                else if (SettingsOverlay.Visibility == Visibility.Visible)
                {
                    CloseSettings();
                }
                else if (_queueOpen)
                {
                    CloseQueuePanel();
                }
                else if (PasswordOverlay.Visibility == Visibility.Visible)
                {
                    _passwordTcs?.TrySetResult(null);
                    _passwordTcs = null;
                    PasswordOverlay.Visibility = Visibility.Collapsed;
                }
                else if (ProgressOverlay.Visibility == Visibility.Visible)
                {
                    _cts?.Cancel();
                }
            }
        }

        // ==================== Navigation ====================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageExtract == null) return;

            var tag = (sender as RadioButton)?.Tag as string;
            var page = tag switch
            {
                "create" => PageCreate,
                "history" => PageHistory,
                _ => PageExtract
            };

            var index = tag switch
            {
                "create" => 1,
                "history" => 2,
                _ => 0
            };

            var forward = index >= _pageIndex;
            _pageIndex = index;

            foreach (var candidate in new[] { PageExtract, PageCreate, PageHistory })
            {
                if (!ReferenceEquals(candidate, page))
                {
                    candidate.Visibility = Visibility.Collapsed;
                    candidate.Opacity = 1;
                }
            }

            if (page.Visibility != Visibility.Visible || page.Opacity < 1)
                AnimatePage(page, forward);
        }

        /// <summary>页面切换：按导航方向从侧向滑入，并伴随轻微上浮与淡入。</summary>
        private void AnimatePage(UIElement page, bool forward = true)
        {
            page.Visibility = Visibility.Visible;
            page.Opacity = 0;

            var offset = forward ? 26.0 : -26.0;
            var transform = new TranslateTransform(offset, 8);
            page.RenderTransform = transform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)));
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }

        private void NavigateCreate_Click(object sender, RoutedEventArgs e) => NavCreate.IsChecked = true;

        // ==================== Open archive ====================

        private async void OpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择压缩包",
                Filter = "压缩包|*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.zst;*.iso;*.cab;*.wim;*.arj;*.lzh;*.jar;*.apk;*.msi;*.exe;*.rpm;*.deb|所有文件|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                await OpenArchiveAsync(dialog.FileName);
            }
        }

        private async Task OpenArchiveAsync(string path)
        {
            if (!File.Exists(path))
            {
                ShowToast("文件不存在：" + path, ToastKind.Error);
                return;
            }

            // 分卷压缩包无法在内核中按卷集合浏览，直接进入解压流程
            if (ArchiveEditor.IsSplitVolume(path))
            {
                new QuickExtractWindow(path, QuickExtractMode.Folder).Show();
                return;
            }

            Mouse.OverrideCursor = Cursors.Wait;
            ArchiveSession? session = null;
            string password = "";

            try
            {
                var errorHint = "";
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        var pwd = password;
                        session = await Task.Run(() => SevenZipEngine.Open(path, pwd));
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            ShowToast("打开失败：" + FirstLine(ex.Message), ToastKind.Error);
                            return;
                        }

                        var entered = await PromptPasswordAsync(Path.GetFileName(path), errorHint);
                        if (entered == null)
                        {
                            ShowToast("已取消打开压缩包", ToastKind.Info);
                            return;
                        }

                        password = entered;
                        errorHint = "密码不正确，请重新输入。";
                    }
                }

                if (session == null)
                {
                    ShowToast("无法打开该压缩包", ToastKind.Error);
                    return;
                }

                _session?.Dispose();
                _session = session;
                await BindArchiveAsync(session);
                await SwitchToArchiveStateAsync();
                ShowToast("已载入 " + Path.GetFileName(session.FilePath) + " · " + session.Entries.Count + " 个项目", ToastKind.Success);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async Task BindArchiveAsync(ArchiveSession session)
        {
            ArchiveNameText.Text = Path.GetFileName(session.FilePath);
            ArchiveNameText.ToolTip = session.FilePath;
            FormatPillText.Text = session.Format.Name.ToUpperInvariant();
            EncryptedPill.Visibility = session.HasEncrypted ? Visibility.Visible : Visibility.Collapsed;

            var files = session.Entries.Count(x => !x.IsDir);
            var dirs = session.Entries.Count - files;
            var ratio = session.UnpackedSize > 0
                ? session.PackedSize * 100.0 / session.UnpackedSize
                : 0;

            ArchiveMetaText.Text = files + " 个文件 · " + dirs + " 个文件夹 · 原始 " +
                                   FormatSize(session.UnpackedSize) + " · 压缩后 " + FormatSize(session.PackedSize) +
                                   " · 压缩率 " + ratio.ToString("0.#") + "%";

            StatusSummaryText.Text = "共 " + session.Entries.Count + " 项";
            StatusPathText.Text = session.FilePath;

            _allRows.Clear();

            // 大压缩包分片填充：每 800 条让出一次 UI 线程，10 万级条目时窗口仍可重绘与响应
            var total = session.Entries.Count;
            for (var i = 0; i < total; i++)
            {
                _allRows.Add(EntryRow.Create(session.Entries[i], (uint)i));
                if (total > 2000 && (i + 1) % 800 == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            ApplyFilter();
            SearchBox.Text = "";
            SearchHint.Visibility = Visibility.Visible;
        }

        private async Task SwitchToArchiveStateAsync()
        {
            if (ArchiveState.Visibility == Visibility.Visible) return;

            EmptyState.Visibility = Visibility.Collapsed;
            ArchiveState.Visibility = Visibility.Visible;
            ArchiveState.Opacity = 0;

            var transform = new TranslateTransform(0, 10);
            ArchiveState.RenderTransform = transform;
            ArchiveState.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            await Task.Delay(30);
            AnimateRows();
        }

        private void AnimateRows()
        {
            var index = 0;
            foreach (var item in EntryList.Items)
            {
                // 只做可见范围附近的行动画，大列表不逐项查找容器
                if (index >= 24) break;

                if (EntryList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;

                var delay = TimeSpan.FromMilliseconds(Math.Min(index, 16) * 16);
                container.Opacity = 0;
                var transform = new TranslateTransform(0, 8);
                container.RenderTransform = transform;

                container.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay });
                transform.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(280))
                    {
                        BeginTime = delay,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });

                index++;
            }
        }

        private void CloseArchive_Click(object sender, RoutedEventArgs e)
        {
            _session?.Dispose();
            _session = null;
            _allRows.Clear();
            _rows.Clear();
            ArchiveState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Opacity = 0;
            EmptyState.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }

        // ==================== Filter ====================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;

            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private void ApplyFilter()
        {
            if (SearchBox == null) return;

            var query = SearchBox.Text.Trim();
            _rows.Clear();

            IEnumerable<EntryRow> source = _allRows;

            source = _filterKind switch
            {
                FilterKind.File => source.Where(r => !r.Entry.IsDir),
                FilterKind.Folder => source.Where(r => r.Entry.IsDir),
                FilterKind.Image => source.Where(r => !r.Entry.IsDir && ImageExtensions.Contains(r.Entry.Extension)),
                FilterKind.Media => source.Where(r => !r.Entry.IsDir &&
                                                      (VideoExtensions.Contains(r.Entry.Extension) ||
                                                       AudioExtensions.Contains(r.Entry.Extension))),
                FilterKind.Document => source.Where(r => !r.Entry.IsDir && DocExtensions.Contains(r.Entry.Extension)),
                FilterKind.Code => source.Where(r => !r.Entry.IsDir && CodeExtensions.Contains(r.Entry.Extension)),
                _ => source
            };

            if (query.Length > 0)
            {
                var lower = query.ToLowerInvariant();
                source = source.Where(r => r.Name.ToLowerInvariant().Contains(lower) ||
                                           r.Entry.Path.ToLowerInvariant().Contains(lower));
            }

            IEnumerable<EntryRow> ordered = _sortMode switch
            {
                SortMode.Size => source.OrderByDescending(r => r.Entry.Size),
                SortMode.Date => source.OrderByDescending(r => r.Entry.Modified),
                _ => source.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            };

            var orderedRows = new List<EntryRow>(_allRows.Count);
            foreach (var row in ordered) orderedRows.Add(row);

            // 先解绑再批量填充，避免大压缩包逐项触发列表刷新
            EntryList.ItemsSource = null;
            _rows.Clear();
            foreach (var row in orderedRows) _rows.Add(row);
            EntryList.ItemsSource = _rows;

            var filtered = _filterKind != FilterKind.All || query.Length > 0;
            FilterCountText.Text = filtered ? "筛选出 " + _rows.Count + " / " + _allRows.Count + " 项" : "";

            var archiveEmpty = _allRows.Count == 0;
            EntryEmptyTitle.Text = archiveEmpty ? "压缩包里没有条目" : "没有匹配的条目";
            EntryEmptyDesc.Text = archiveEmpty ? "这个压缩包是空的" : "换个筛选条件或搜索关键词试试";

            var showEmpty = _rows.Count == 0;
            if (showEmpty && EntryEmptyHint.Visibility != Visibility.Visible)
            {
                EntryEmptyHint.Visibility = Visibility.Visible;
                EntryEmptyHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else if (!showEmpty)
            {
                EntryEmptyHint.Visibility = Visibility.Collapsed;
            }

            Dispatcher.BeginInvoke(new Action(AnimateRows), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FilterChip_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag }) return;

            _filterKind = tag switch
            {
                "file" => FilterKind.File,
                "folder" => FilterKind.Folder,
                "image" => FilterKind.Image,
                "media" => FilterKind.Media,
                "doc" => FilterKind.Document,
                "code" => FilterKind.Code,
                _ => FilterKind.All
            };

            if (IsLoaded) ApplyFilter();
        }

        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = _sortMode switch
            {
                SortMode.Name => SortMode.Size,
                SortMode.Size => SortMode.Date,
                _ => SortMode.Name
            };

            SortText.Text = _sortMode switch
            {
                SortMode.Size => "大小",
                SortMode.Date => "时间",
                _ => "名称"
            };

            ApplyFilter();
        }

        // ==================== Extract / test ====================

        private async void ExtractAll_Click(object sender, RoutedEventArgs e) => await ExtractAsync(null, true);

        private async void ExtractTo_Click(object sender, RoutedEventArgs e)
        {
            var selected = EntryList.SelectedItems.Cast<EntryRow>().Select(r => r.Index).ToList();
            await ExtractAsync(selected.Count > 0 ? selected : null);
        }

        private static string BuildExtractFolder(string archivePath) => ArchivePaths.BuildExtractFolder(archivePath);

        private static void FlattenSingleRoot(ArchiveSession session, string target) =>
            ArchivePaths.FlattenSingleRoot(session, target);

        private static void OpenInExplorer(string path) => ArchivePaths.OpenInExplorer(path);

        private async Task ExtractAsync(IReadOnlyList<uint>? indices, bool autoFolder = false)
        {
            if (_session == null) return;
            if (PasswordOverlay.Visibility == Visibility.Visible) return;

            // 自动解压到同名文件夹时接入断点续传：中断后再次解压自动从上次进度继续
            ExtractCoordinator? coordinator = autoFolder
                ? ExtractCoordinator.Create(_session.FilePath)
                : null;

            string target;
            if (autoFolder)
            {
                target = coordinator?.RecordTarget ?? BuildExtractFolder(_session.FilePath);
            }
            else
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择解压目标文件夹",
                    InitialDirectory = Path.GetDirectoryName(_session.FilePath) ?? ""
                };

                if (dialog.ShowDialog(this) != true) return;
                target = dialog.FolderName;
            }

            var session = _session;
            var total = indices == null
                ? session.Entries.Where(x => !x.IsDir).Count()
                : indices.Count(i => i < session.Entries.Count && !session.Entries[(int)i].IsDir);

            OperationResult? result;
            if (coordinator != null)
            {
                coordinator.Initialize(target, session.Entries);
                // 主界面没有磁盘分批入口，关闭磁盘分批；中断后可再次解压从断点续传
                result = await RunOperationAsync("正在解压 " + total + " 个文件…",
                    (progress, token) => coordinator.Run(session, progress, token, _mainPauseGate, diskBatching: false),
                    canPause: true);
            }
            else
            {
                result = await RunOperationAsync("正在解压 " + total + " 个文件…",
                    (progress, token) => SevenZipEngine.Extract(session, indices, target, progress, token, _mainPauseGate),
                    canPause: true);
            }

            if (result == null) return;

            if (result.WrongPassword)
            {
                var pwd = await PromptPasswordAsync(Path.GetFileName(session.FilePath));
                if (pwd != null)
                {
                    session.Password = pwd;
                    if (coordinator != null)
                    {
                        result = await RunOperationAsync("正在解压 " + total + " 个文件…",
                            (progress, token) => coordinator.Run(session, progress, token, _mainPauseGate, diskBatching: false),
                            canPause: true);
                    }
                    else
                    {
                        result = await RunOperationAsync("正在解压 " + total + " 个文件…",
                            (progress, token) => SevenZipEngine.Extract(session, indices, target, progress, token, _mainPauseGate),
                            canPause: true);
                    }
                }
            }

            if (result == null) return;

            AddHistory(HistoryKind.Extract, Path.GetFileName(session.FilePath), result.Message, result.Success);

            if (result.Success)
            {
                if (autoFolder && AppSettings.SmartExtract)
                    FlattenSingleRoot(session, target);

                if (AppSettings.FixLegacyNames &&
                    ArchivePaths.MayNeedRepair(session.Entries.Select(e => e.Path)))
                    ArchivePaths.RepairLegacyNames(target);

                ShowToast("解压完成 · " + result.Items + " 个项目已保存", ToastKind.Success);
            }
            else if (result.Cancelled)
            {
                ShowToast(coordinator?.HasRecord == true
                    ? "解压已中断 · 再次解压可从断点继续"
                    : "解压已取消", ToastKind.Info);
            }
            else
            {
                ShowToast("解压失败：" + FirstLine(result.Message), ToastKind.Error);
            }
        }

        private async void TestArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var session = _session;

            var result = await RunOperationAsync("正在校验压缩包…",
                (progress, token) => SevenZipEngine.Test(session, progress, token));

            if (result == null) return;
            AddHistory(HistoryKind.Test, Path.GetFileName(session.FilePath), result.Message, result.Success);
            ShowToast(FirstLine(result.Message), result.Success ? ToastKind.Success : ToastKind.Error);
        }

        // ==================== Batch extract ====================

        private void BatchExtract_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "批量解压压缩包",
                Multiselect = true,
                Filter = "压缩包|*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.zst;*.iso;*.cab;*.wim;*.arj;*.lzh;*.jar;*.apk;*.msi;*.rpm;*.deb|所有文件|*.*"
            };

            if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;

            TaskQueue.Enqueue(dialog.FileNames);
            OpenQueuePanel();
            ShowToast("已加入任务队列 · " + dialog.FileNames.Length + " 个压缩包", ToastKind.Info);
        }

        // ==================== Entry preview ====================

        private async void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (EntryList.SelectedItem is not EntryRow row || row.Entry.IsDir) return;
            await PreviewEntryAsync(row);
        }

        private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
        {
            if (EntryList.SelectedItems.Count != 1 ||
                EntryList.SelectedItem is not EntryRow row ||
                row.Entry.IsDir)
            {
                ShowToast("请选择一个文件后再预览", ToastKind.Info);
                return;
            }

            await PreviewEntryAsync(row);
        }

        private async Task PreviewEntryAsync(EntryRow row)
        {
            if (_session == null || _cts != null) return;
            var session = _session;

            var result = await RunOperationAsync("正在准备预览 " + row.Name + "…", (progress, token) =>
            {
                var temp = Path.Combine(Path.GetTempPath(), "NovaZip", "preview", Guid.NewGuid().ToString("N"));
                var extraction = SevenZipEngine.Extract(session, new[] { row.Index }, temp, progress, token);
                extraction.Message = temp;
                return extraction;
            });

            if (result == null) return;

            if (!result.Success)
            {
                ShowToast("无法预览：" + FirstLine(result.Message), ToastKind.Error);
                return;
            }

            var path = Path.Combine(result.Message, row.Entry.Path.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                ShowToast("预览文件未生成", ToastKind.Error);
                return;
            }

            CleanupPreview();
            _previewTemp = result.Message;
            _previewFilePath = path;
            ShowPreview(row.Entry, path);
        }

        private void ShowPreview(ArchiveEntry entry, string path)
        {
            PreviewTitleText.Text = entry.Name;
            PreviewPathText.Text = entry.Path;
            PreviewInfoText.Text = FormatSize(entry.Size) + " · " + entry.Modified.ToString("yyyy/MM/dd HH:mm");

            PreviewTextBox.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;

            var ext = entry.Extension;
            var canEdit = false;

            if (ImageExtensions.Contains(ext) && ext is not "svg" and not "psd" and not "heic" and not "avif")
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.DecodePixelWidth = 1800;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    PreviewImage.Source = bitmap;
                    PreviewImage.Visibility = Visibility.Visible;
                }
                catch
                {
                    PreviewFallback.Visibility = Visibility.Visible;
                }
            }
            else if (TextPreviewExtensions.Contains(ext))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Length > 300_000)
                        text = text.Substring(0, 300_000) + "\n\n…（内容过长，仅显示前 300 KB）";

                    PreviewTextBox.Text = text;
                    PreviewTextBox.Visibility = Visibility.Visible;
                    canEdit = true;
                }
                catch
                {
                    PreviewFallback.Visibility = Visibility.Visible;
                }
            }
            else
            {
                PreviewFallback.Visibility = Visibility.Visible;
            }

            // 文本条目且当前压缩包支持修改时，允许就地编辑并写回
            _previewEntryPath = entry.Path;
            _previewEditing = false;
            PreviewTextBox.IsReadOnly = true;
            PreviewTextBox.CaretBrush = Brushes.Transparent;
            PreviewEditButton.Content = "编辑内容";
            PreviewEditButton.Visibility = canEdit && _session != null && _session.Format.CanUpdate
                ? Visibility.Visible
                : Visibility.Collapsed;

            PreviewOverlay.Visibility = Visibility.Visible;
            PreviewOverlay.Opacity = 0;
            PreviewScale.ScaleX = 0.96;
            PreviewScale.ScaleY = 0.96;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PreviewOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
            PreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            PreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();

        /// <summary>文本预览的就地编辑：第一次点击进入编辑，第二次点击保存回压缩包。</summary>
        private async void PreviewEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_previewEntryPath == null || _previewFilePath == null) return;

            if (!_previewEditing)
            {
                _previewEditing = true;
                PreviewTextBox.IsReadOnly = false;
                PreviewTextBox.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
                PreviewEditButton.Content = "保存到压缩包";
                PreviewTextBox.Focus();
                PreviewTextBox.CaretIndex = PreviewTextBox.Text.Length;
                return;
            }

            if (_session == null) return;

            var archive = _session.FilePath;
            var password = _session.Password;
            var entryPath = _previewEntryPath;
            var tempFile = _previewFilePath;
            var text = PreviewTextBox.Text;

            _previewEditing = false;
            PreviewTextBox.IsReadOnly = true;
            PreviewTextBox.CaretBrush = Brushes.Transparent;
            PreviewEditButton.Content = "编辑内容";

            Mouse.OverrideCursor = Cursors.Wait;
            var (ok, message) = await Task.Run(() =>
            {
                try
                {
                    File.WriteAllText(tempFile, text);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }

                return ArchiveEditor.UpdateEntry(archive, entryPath, tempFile, password);
            });
            Mouse.OverrideCursor = null;

            if (!ok)
            {
                ShowToast("保存失败：" + FirstLine(message), ToastKind.Error);
                return;
            }

            await OpenArchiveAsync(archive);
            ShowToast("已保存到压缩包 · " + Path.GetFileName(entryPath), ToastKind.Success);
        }

        private void PreviewScrim_Click(object sender, MouseButtonEventArgs e) => ClosePreview();

        private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void ClosePreview()
        {
            _previewEditing = false;
            PreviewTextBox.IsReadOnly = true;
            PreviewTextBox.CaretBrush = Brushes.Transparent;
            PreviewEditButton.Content = "编辑内容";

            var fade = new DoubleAnimation(PreviewOverlay.Opacity, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                PreviewOverlay.Visibility = Visibility.Collapsed;
                PreviewTextBox.Text = "";
                PreviewImage.Source = null;
            };

            PreviewOverlay.BeginAnimation(OpacityProperty, fade);
            CleanupPreview();
        }

        private void CleanupPreview()
        {
            try
            {
                if (_previewTemp != null && Directory.Exists(_previewTemp))
                    Directory.Delete(_previewTemp, true);
            }
            catch
            {
                // 临时目录被占用时留待系统清理
            }

            _previewTemp = null;
            _previewFilePath = null;
        }

        private void OpenPreviewExternal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_previewFilePath) || !File.Exists(_previewFilePath))
            {
                ShowToast("预览文件已失效，请重新打开预览", ToastKind.Info);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(_previewFilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowToast("无法打开文件：" + FirstLine(ex.Message), ToastKind.Error);
            }
        }

        // ==================== Archive editing ====================

        private async void AddEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _cts != null) return;
            if (!_session.Format.CanUpdate)
            {
                ShowToast("该格式不支持直接修改，请先解压后重新压缩", ToastKind.Info);
                return;
            }

            var dialog = new OpenFileDialog { Title = "添加到压缩包", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;

            await RunArchiveEditAsync((file, password) => ArchiveEditor.Add(file, dialog.FileNames, password),
                "已添加 " + dialog.FileNames.Length + " 个文件");
        }

        private async void DeleteEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _cts != null) return;
            if (!_session.Format.CanUpdate)
            {
                ShowToast("该格式不支持直接修改，请先解压后重新压缩", ToastKind.Info);
                return;
            }

            var selected = EntryList.SelectedItems.Cast<EntryRow>().ToList();
            if (selected.Count == 0)
            {
                ShowToast("请先选择要删除的条目", ToastKind.Info);
                return;
            }

            var confirm = MessageBox.Show(this,
                "确定要从压缩包中删除选中的 " + selected.Count + " 个条目吗？此操作不可撤销。",
                "删除条目", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            var entries = selected.Select(r => r.Entry.Path).ToList();
            await RunArchiveEditAsync((file, password) => ArchiveEditor.Delete(file, entries, password),
                "已删除 " + entries.Count + " 个条目");
        }

        private async void RenameEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _cts != null) return;
            if (!_session.Format.CanUpdate)
            {
                ShowToast("该格式不支持直接修改，请先解压后重新压缩", ToastKind.Info);
                return;
            }

            var selected = EntryList.SelectedItems.Cast<EntryRow>().ToList();
            if (selected.Count != 1)
            {
                ShowToast("请选择单个条目进行重命名", ToastKind.Info);
                return;
            }

            var row = selected[0];
            var name = await PromptRenameAsync(row.Entry.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            if (string.Equals(name, row.Entry.Name, StringComparison.Ordinal))
                return;

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowToast("名称包含非法字符", ToastKind.Error);
                return;
            }

            var directory = row.Entry.Directory;
            var newPath = string.IsNullOrEmpty(directory) ? name : directory + "\\" + name;

            await RunArchiveEditAsync((file, password) => ArchiveEditor.Rename(file, row.Entry.Path, newPath, password),
                "已重命名为 " + name);
        }

        private async Task RunArchiveEditAsync(Func<string, string, (bool Ok, string Message)> action, string successMessage)
        {
            if (_session == null) return;

            var file = _session.FilePath;
            var password = _session.Password;

            _session.Dispose();
            _session = null;

            var (ok, message) = await Task.Run(() => action(file, password));

            await ReloadArchiveAsync(file, password);

            if (ok) ShowToast(successMessage, ToastKind.Success);
            else ShowToast(FirstLine(message), ToastKind.Error);
        }

        private async Task ReloadArchiveAsync(string path, string password)
        {
            try
            {
                var session = await Task.Run(() => SevenZipEngine.Open(path, password));
                _session?.Dispose();
                _session = session;
                await BindArchiveAsync(session);

                if (ArchiveState.Visibility != Visibility.Visible)
                    await SwitchToArchiveStateAsync();
                else
                    AnimateRows();
            }
            catch (Exception ex)
            {
                _session = null;
                ArchiveState.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                ShowToast("重新载入失败：" + FirstLine(ex.Message), ToastKind.Error);
            }
        }

        private Task<string?> PromptRenameAsync(string current)
        {
            RenameInput.Text = current;
            RenameErrorText.Visibility = Visibility.Collapsed;
            RenameOverlay.Visibility = Visibility.Visible;
            RenameOverlay.Opacity = 0;
            RenameScale.ScaleX = 0.94;
            RenameScale.ScaleY = 0.94;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RenameOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            RenameScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            RenameScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

            _renameTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RenameInput.Focus();
                RenameInput.SelectAll();
            }), DispatcherPriority.Input);

            return _renameTcs.Task;
        }

        private void RenameOk_Click(object sender, RoutedEventArgs e)
        {
            var value = RenameInput.Text.Trim();
            if (value.Length == 0)
            {
                RenameErrorText.Text = "名称不能为空";
                RenameErrorText.Visibility = Visibility.Visible;
                return;
            }

            CloseRename(value);
        }

        private void RenameCancel_Click(object sender, RoutedEventArgs e) => CloseRename(null);

        private void CloseRename(string? value)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renameTcs?.TrySetResult(value);
            _renameTcs = null;
        }

        // ==================== Settings ====================

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsOpen) CloseSettings();
            else OpenSettings();
        }

        private void OpenSettings()
        {
            CloseQueuePanel();
            SyncSettingsChecks();
            _settingsOpen = true;

            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.Opacity = 0;
            SettingsSlide.X = 26;

            SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(280))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void CloseSettings()
        {
            if (!_settingsOpen) return;
            _settingsOpen = false;

            var fade = new DoubleAnimation(SettingsOverlay.Opacity, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.BeginAnimation(OpacityProperty, fade);

            SettingsSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        private void SettingsScrim_Click(object sender, MouseButtonEventArgs e) => CloseSettings();

        private void SyncSettingsChecks()
        {
            _updatingSettings = true;
            ThemeAuto.IsChecked = ThemeManager.Mode == ThemeMode.Auto;
            ThemeDark.IsChecked = ThemeManager.Mode == ThemeMode.Dark;
            ThemeLight.IsChecked = ThemeManager.Mode == ThemeMode.Light;
            ContextMenuToggle.IsChecked = ContextMenuIntegration.IsEnabled();
            ClassicMenuToggle.IsChecked = ContextMenuIntegration.IsClassicMenuEnabled();
            FileAssocToggle.IsChecked = ContextMenuIntegration.IsFileAssociationEnabled();
            SmartExtractToggle.IsChecked = AppSettings.SmartExtract;
            ExcludeJunkToggle.IsChecked = AppSettings.ExcludeJunk;
            FixLegacyNamesToggle.IsChecked = AppSettings.FixLegacyNames;

            var percent = Math.Round(AppSettings.WindowOpacity * 100);
            OpacitySlider.Value = percent;
            OpacityValueText.Text = ((int)percent) + "%";
            AutoUpdateToggle.IsChecked = AppSettings.AutoCheckUpdates;
            if (!_updateInProgress)
                UpdateStatusText.Text = "当前版本 " + UpdateService.CurrentVersionText;
            _updatingSettings = false;
        }

        /// <summary>窗口背景透明度：仅作用于桌面透视背景层，不影响文字与控件的可读性。</summary>
        private void ApplyWindowOpacity()
        {
            if (BackdropLayer != null)
                BackdropLayer.Opacity = Math.Clamp(AppSettings.WindowOpacity, 0.0, 1.0);
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityValueText != null)
                OpacityValueText.Text = ((int)Math.Round(e.NewValue)) + "%";

            if (_updatingSettings || !IsLoaded) return;

            AppSettings.WindowOpacity = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
            ApplyWindowOpacity();
            AppSettings.Save();
        }

        private void ThemeMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingSettings || !IsLoaded) return;
            if (sender is not RadioButton { Tag: string tag }) return;

            ThemeManager.SetMode(tag switch
            {
                "dark" => ThemeMode.Dark,
                "light" => ThemeMode.Light,
                _ => ThemeMode.Auto
            });

            if (ThemeManager.Mode == ThemeMode.Auto)
                ShowToast("已跟随系统 · 当前" + (ThemeManager.IsDark ? "深色" : "浅色"), ToastKind.Info);
        }

        private void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingSettings || !IsLoaded) return;

            if (ReferenceEquals(sender, ContextMenuToggle))
            {
                var enable = ContextMenuToggle.IsChecked == true;
                string message;
                bool ok = enable
                    ? ContextMenuIntegration.Enable(out message)
                    : ContextMenuIntegration.Disable(out message);

                if (!ok)
                {
                    _updatingSettings = true;
                    ContextMenuToggle.IsChecked = !enable;
                    _updatingSettings = false;
                }

                ShowToast(message, ok ? ToastKind.Success : ToastKind.Error);

                // Windows 11 新式菜单不显示第三方项目，主动询问是否切换到经典菜单
                if (ok && enable && ContextMenuIntegration.IsWindows11 && !ContextMenuIntegration.IsClassicMenuEnabled())
                {
                    var answer = MessageBox.Show(this,
                        "NovaZip 已注册到右键菜单。\n\n" +
                        "Windows 11 的新版右键菜单只显示系统自带项目，第三方程序会被折叠到「显示更多选项」中。\n\n" +
                        "是否切换为经典右键菜单，让 NovaZip 直接出现在一级菜单？\n" +
                        "（可随时在设置中关闭，切换后会自动重启资源管理器）",
                        "右键菜单设置", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Yes)
                    {
                        if (ContextMenuIntegration.EnableClassicMenu(out var classicMessage))
                        {
                            _updatingSettings = true;
                            ClassicMenuToggle.IsChecked = true;
                            _updatingSettings = false;
                            ShowToast(classicMessage, ToastKind.Success);
                            ContextMenuIntegration.RestartExplorer();
                        }
                        else
                        {
                            ShowToast(classicMessage, ToastKind.Error);
                        }
                    }
                }

                return;
            }

            if (ReferenceEquals(sender, ClassicMenuToggle))
            {
                var enable = ClassicMenuToggle.IsChecked == true;
                string message;
                bool ok = enable
                    ? ContextMenuIntegration.EnableClassicMenu(out message)
                    : ContextMenuIntegration.DisableClassicMenu(out message);

                if (!ok)
                {
                    _updatingSettings = true;
                    ClassicMenuToggle.IsChecked = !enable;
                    _updatingSettings = false;
                }

                ShowToast(message, ok ? ToastKind.Success : ToastKind.Error);
                if (ok) ContextMenuIntegration.RestartExplorer();
                return;
            }

            if (ReferenceEquals(sender, FileAssocToggle))
            {
                var enable = FileAssocToggle.IsChecked == true;
                string message;
                bool ok = enable
                    ? ContextMenuIntegration.EnableFileAssociation(out message)
                    : ContextMenuIntegration.DisableFileAssociation(out message);

                if (!ok)
                {
                    _updatingSettings = true;
                    FileAssocToggle.IsChecked = !enable;
                    _updatingSettings = false;
                }

                ShowToast(message, ok ? ToastKind.Success : ToastKind.Error);
                return;
            }

            if (ReferenceEquals(sender, AutoUpdateToggle))
            {
                AppSettings.AutoCheckUpdates = AutoUpdateToggle.IsChecked == true;
                AppSettings.Save();
                return;
            }

            AppSettings.SmartExtract = SmartExtractToggle.IsChecked == true;
            AppSettings.ExcludeJunk = ExcludeJunkToggle.IsChecked == true;
            AppSettings.FixLegacyNames = FixLegacyNamesToggle.IsChecked == true;
            AppSettings.Save();
        }

        // ==================== Update ====================

        private bool _updateInProgress;

        /// <summary>启动后静默检查：开启自动检查且距上次检查超过 24 小时时才请求。</summary>
        private async void CheckUpdatesOnStartup()
        {
            if (!AppSettings.AutoCheckUpdates || !UpdateService.IsConfigured) return;
            if (DateTime.UtcNow - AppSettings.LastUpdateCheckUtc < TimeSpan.FromHours(24)) return;

            try
            {
                var info = await UpdateService.CheckAsync();
                AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;
                AppSettings.Save();

                if (info == null) return;

                UpdateStatusText.Text = "发现新版本 " + info.Version;
                if (!ConfirmUpdate(info)) return;

                OpenSettings();
                await RunUpdateAsync(info);
            }
            catch
            {
                // 启动检查失败时保持静默，不打扰正常使用
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updateInProgress) return;

            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查更新…";

            try
            {
                var info = await UpdateService.CheckAsync();
                AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;
                AppSettings.Save();

                if (info == null)
                {
                    UpdateStatusText.Text = "已是最新版本 " + UpdateService.CurrentVersionText;
                    ShowToast("已是最新版本", ToastKind.Success);
                    return;
                }

                UpdateStatusText.Text = "发现新版本 " + info.Version;
                if (ConfirmUpdate(info))
                    await RunUpdateAsync(info);
            }
            catch (Exception ex)
            {
                var message = !UpdateService.IsConfigured
                    ? "更新源尚未配置，暂时无法检查更新"
                    : ex.Message;
                UpdateStatusText.Text = "检查失败：" + message;
                ShowToast("检查更新失败：" + message, ToastKind.Error);
            }
            finally
            {
                if (!_updateInProgress) CheckUpdateButton.IsEnabled = true;
            }
        }

        private bool ConfirmUpdate(UpdateInfo info)
        {
            var notes = (info.Notes ?? "").Trim();
            if (notes.Length > 300) notes = notes.Substring(0, 300).TrimEnd() + "…";

            var size = info.Size > 0 ? $"大小：{info.Size / 1024.0 / 1024.0:0.0} MB\n" : "";
            var text = $"发现新版本 {info.Version}（当前 {UpdateService.CurrentVersionText}）\n{size}\n" +
                       (notes.Length > 0 ? notes + "\n\n" : "") +
                       "下载完成后会校验 SHA-256，随后自动重启完成更新。";

            return MessageBox.Show(this, text, "NovaZip 更新",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        }

        private async Task RunUpdateAsync(UpdateInfo info)
        {
            _updateInProgress = true;
            CheckUpdateButton.IsEnabled = false;
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.Value = 0;

            try
            {
                var progress = new Progress<double>(value =>
                {
                    var percent = Math.Clamp(value * 100, 0, 100);
                    UpdateProgress.Value = percent;
                    UpdateStatusText.Text = $"正在下载 {info.Version}… {(int)percent}%";
                });

                var file = await UpdateService.DownloadAsync(info, progress);
                UpdateProgress.Value = 100;
                UpdateStatusText.Text = "校验通过，正在重启安装…";
                UpdateService.ApplyAndRestart(file);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "更新失败：" + ex.Message;
                ShowToast("更新失败：" + ex.Message, ToastKind.Error);
                UpdateProgress.Visibility = Visibility.Collapsed;
                _updateInProgress = false;
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private void TargetSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TargetSizeHint != null)
                TargetSizeHint.Visibility = string.IsNullOrEmpty(TargetSizeBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void VolumeSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (VolumeSizeHint != null)
                VolumeSizeHint.Visibility = string.IsNullOrEmpty(VolumeSizeBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        // ==================== Source list reorder ====================

        private void SourceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sourceDragStart = e.GetPosition(SourceList);
            _sourceDragItem = RowAt(_sourceDragStart);
        }

        private void SourceList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _sourceDragItem == null) return;

            var position = e.GetPosition(SourceList);
            if (Math.Abs(position.Y - _sourceDragStart.Y) < 8) return;

            var row = RowAt(position);
            if (row == null) return;

            try
            {
                DragDrop.DoDragDrop(SourceList, new DataObject(typeof(SourceRow), row), DragDropEffects.Move);
            }
            catch
            {
                // 忽略拖拽异常
            }

            _sourceDragItem = null;
        }

        private void SourceList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(SourceRow))) return;
            if (e.Data.GetData(typeof(SourceRow)) is not SourceRow from) return;

            var target = RowAt(e.GetPosition(SourceList));
            if (target == null || ReferenceEquals(target, from)) return;

            var oldIndex = _sources.IndexOf(from);
            var newIndex = _sources.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0) return;

            _sources.Move(oldIndex, newIndex);
            e.Handled = true;
        }

        private SourceRow? RowAt(Point point)
        {
            var element = SourceList.InputHitTest(point) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            return (element as ListBoxItem)?.DataContext as SourceRow;
        }

        // ==================== Create ====================

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "选择要压缩的文件", Multiselect = true };
            if (dialog.ShowDialog(this) == true) AddSources(dialog.FileNames);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "选择要压缩的文件夹" };
            if (dialog.ShowDialog(this) == true) AddSources(new[] { dialog.FolderName });
        }

        private void AddSources(IEnumerable<string> paths)
        {
            var added = 0;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (_sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                var row = SourceRow.Create(path);
                if (row == null) continue;

                _sources.Add(row);
                added++;
            }

            if (added == 0) return;

            UpdateSourceSummary();
            SuggestOutputPath();
            ShowToast("已添加 " + added + " 个项目", ToastKind.Info);
        }

        private void RemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: SourceRow row })
            {
                _sources.Remove(row);
                UpdateSourceSummary();
            }
        }

        private void ClearSources_Click(object sender, RoutedEventArgs e)
        {
            _sources.Clear();
            UpdateSourceSummary();
        }

        private void UpdateSourceSummary()
        {
            var count = _sources.Count;
            var totalBytes = _sources.Where(s => !s.IsDirectory).Sum(s => s.Size);
            SourceSummaryText.Text = count + " 个项目";
            SourceEmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SourceFootText.Text = count == 0
                ? "支持任意文件与文件夹，文件夹会递归打包。"
                : "共 " + count + " 个项目 · 已知大小 " + FormatSize((ulong)Math.Max(0, totalBytes));

            EstimateText.Text = count == 0
                ? "添加文件后可查看压缩预估"
                : "将使用 " + CurrentFormat() + " 格式，预计生成约 " +
                  FormatSize((ulong)Math.Max(0, totalBytes * 0.72)) + " 的压缩包（实际大小取决于内容）";
        }

        private string CurrentFormat() => FormatZip.IsChecked == true ? "ZIP" : FormatTar.IsChecked == true ? "TAR" : "7Z";

        private string CurrentExtension() => FormatZip.IsChecked == true ? ".zip" : FormatTar.IsChecked == true ? ".tar" : ".7z";

        private void Format_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressFormatSync || OutputPathBox == null) return;

            var current = OutputPathBox.Text;
            if (!string.IsNullOrWhiteSpace(current))
            {
                var ext = Path.GetExtension(current);
                if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tar", StringComparison.OrdinalIgnoreCase))
                {
                    OutputPathBox.Text = Path.ChangeExtension(current, CurrentExtension());
                }
            }

            UpdateSourceSummary();
        }

        private void SuggestOutputPath()
        {
            if (!string.IsNullOrWhiteSpace(OutputPathBox.Text)) return;
            if (_sources.Count == 0) return;

            var first = _sources[0];
            var dir = first.IsDirectory ? Path.GetDirectoryName(first.Path) : Path.GetDirectoryName(first.Path);
            var baseName = _sources.Count == 1
                ? (first.IsDirectory ? new DirectoryInfo(first.Path).Name : Path.GetFileNameWithoutExtension(first.Path))
                : "archive";

            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            OutputPathBox.Text = Path.Combine(dir, baseName + CurrentExtension());
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var ext = CurrentExtension();
            var dialog = new SaveFileDialog
            {
                Title = "保存压缩包",
                Filter = "压缩包|*" + ext + "|所有文件|*.*",
                DefaultExt = ext,
                FileName = string.IsNullOrWhiteSpace(OutputPathBox.Text)
                    ? "archive" + ext
                    : Path.GetFileName(OutputPathBox.Text),
                InitialDirectory = string.IsNullOrWhiteSpace(OutputPathBox.Text)
                    ? ""
                    : Path.GetDirectoryName(OutputPathBox.Text) ?? ""
            };

            if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FileName;
        }

        private async void CreateArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_sources.Count == 0)
            {
                ShowToast("请先添加要压缩的文件或文件夹", ToastKind.Info);
                return;
            }

            var output = OutputPathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                BrowseOutput_Click(sender, e);
                output = OutputPathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(output)) return;
            }

            var password = CreatePasswordBox.Password;
            var level = Level1.IsChecked == true ? 1 : Level9.IsChecked == true ? 9 : 5;
            var paths = _sources.Select(s => s.Path).ToList();

            long targetBytes = 0;
            var targetText = TargetSizeBox.Text.Trim();
            if (targetText.Length > 0)
            {
                if (!double.TryParse(targetText, out var targetMb) || targetMb <= 0)
                {
                    ShowToast("目标大小需要填写大于 0 的数字（单位 MB）", ToastKind.Info);
                    TargetSizeBox.Focus();
                    return;
                }

                targetBytes = (long)(targetMb * 1024 * 1024);
            }

            var volumeMb = 0;
            var volumeText = VolumeSizeBox.Text.Trim();
            if (volumeText.Length > 0)
            {
                if (!double.TryParse(volumeText, out var volumeValue) || volumeValue <= 0)
                {
                    ShowToast("分卷大小需要填写大于 0 的数字（单位 MB）", ToastKind.Info);
                    VolumeSizeBox.Focus();
                    return;
                }

                if (FormatTar.IsChecked == true)
                {
                    ShowToast("TAR 格式不支持分卷，请改用 7Z 或 ZIP", ToastKind.Info);
                    return;
                }

                volumeMb = (int)Math.Max(1, Math.Round(volumeValue));
            }

            CreateButton.IsEnabled = false;
            try
            {
                var sources = await Task.Run(() => SevenZipEngine.CollectSources(paths, AppSettings.ExcludeJunk));
                if (sources.Count == 0)
                {
                    ShowToast("没有可压缩的内容（可能都被排除规则跳过）", ToastKind.Info);
                    return;
                }

                var title = targetBytes > 0
                    ? "正在压缩到目标大小 " + FormatSize((ulong)targetBytes) + "…"
                    : "正在压缩 " + sources.Count + " 个项目…";

                OperationResult? result;

                if (volumeMb > 0)
                {
                    var volumeFormat = FormatZip.IsChecked == true ? "zip" : "7z";
                    result = await RunOperationAsync(
                        "正在分卷压缩 " + sources.Count + " 个项目（每卷 " + volumeMb + " MB）…",
                        (progress, token) =>
                        {
                            progress.Total = 100;
                            progress.Completed = 5;
                            var (ok, message) = ArchiveEditor.CreateVolumes(
                                output, paths, password, volumeFormat, level, volumeMb);
                            progress.Completed = 100;
                            return new OperationResult { Success = ok, Message = message };
                        });
                }
                else
                {
                    result = targetBytes > 0
                        ? await RunOperationAsync(title, (progress, token) =>
                            CompressToTarget(sources, output, password, targetBytes, progress, token))
                        : await RunOperationAsync(title, (progress, token) =>
                            Compress(sources, output, password, level, progress, token));
                }

                if (result == null) return;

                AddHistory(HistoryKind.Create, Path.GetFileName(output), result.Message, result.Success);
                CreateStatusText.Text = result.Success ? "已生成 " + output : FirstLine(result.Message);

                if (result.Success)
                {
                    if (volumeMb > 0)
                    {
                        var dir = Path.GetDirectoryName(output) ?? ".";
                        var parts = Directory.Exists(dir)
                            ? Directory.GetFiles(dir, Path.GetFileName(output) + ".*").Length
                            : 0;
                        ShowToast("分卷压缩完成 · 已生成 " + parts + " 个分卷", ToastKind.Success);
                    }
                    else
                    {
                        var size = (ulong)(File.Exists(output) ? new FileInfo(output).Length : 0);
                        ShowToast("压缩完成 · " + Path.GetFileName(output) + " (" + FormatSize(size) + ")",
                            ToastKind.Success);
                    }
                }
                else if (result.Cancelled)
                {
                    ShowToast("压缩已取消", ToastKind.Info);
                }
                else
                {
                    ShowToast("压缩失败：" + FirstLine(result.Message), ToastKind.Error);
                }
            }
            finally
            {
                CreateButton.IsEnabled = true;
            }
        }

        /// <summary>压缩入口：优先走 7z.exe 子进程（海量小文件快数倍），路径无法安全映射时回退内核。</summary>
        private static OperationResult Compress(IReadOnlyList<SevenZipEngine.SourceItem> sources, string output,
            string password, int level, ProgressState progress, CancellationToken token)
        {
            return CompressProcess.TryCompress(sources, output, password, level, AppSettings.ExcludeJunk, progress, token)
                   ?? SevenZipEngine.Create(sources, output, password, level, progress, token);
        }

        /// <summary>目标大小压缩：从标准等级逐级降低，直到压缩包不超过目标体积。</summary>
        private static OperationResult CompressToTarget(IReadOnlyList<SevenZipEngine.SourceItem> sources, string output,
            string password, long targetBytes, ProgressState progress, CancellationToken token)
        {
            // 从标准等级起步而不是最高等级：最高等级那一遍最耗时且常常白压，
            // 目标大小模式下只要满足体积即可，没必要先付出极限压缩的时间
            var levels = new[] { 5, 3, 1 };
            OperationResult? last = null;

            foreach (var level in levels)
            {
                if (token.IsCancellationRequested)
                    return new OperationResult { Cancelled = true, Message = "压缩已取消。" };

                try
                {
                    if (File.Exists(output)) File.Delete(output);
                }
                catch
                {
                    // 删除旧输出失败时继续尝试覆盖
                }

                last = Compress(sources, output, password, level, progress, token);
                if (!last.Success) return last;

                var size = File.Exists(output) ? new FileInfo(output).Length : 0;
                if (size <= targetBytes)
                {
                    last.Message = "已压缩到 " + FormatSize((ulong)size) + "（压缩等级 " + level + "）。";
                    return last;
                }
            }

            var finalSize = File.Exists(output) ? new FileInfo(output).Length : 0;
            if (last != null)
            {
                last.Success = true;
                last.Message = "已使用最高压缩等级，实际大小 " + FormatSize((ulong)finalSize) + "，未能达到目标。";
            }

            return last ?? new OperationResult { Success = false, Message = "压缩失败。" };
        }

        // ==================== Drag & drop ====================

        private void DropZone_DragEnter(object sender, DragEventArgs e) => SetDragVisual(true, DropScale, DropZone, true);
        private void DropZone_DragLeave(object sender, DragEventArgs e) => SetDragVisual(false, DropScale, DropZone, true);
        private void CreateDropZone_DragEnter(object sender, DragEventArgs e) => SetDragVisual(true, CreateDropScale, CreateDropZone, false);
        private void CreateDropZone_DragLeave(object sender, DragEventArgs e) => SetDragVisual(false, CreateDropScale, CreateDropZone, false);

        private void SetDragVisual(bool active, ScaleTransform scale, Border border, bool allowArchiveHint)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(active ? 1.015 : 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(active ? 1.015 : 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });

            if (border.Effect is DropShadowEffect glow)
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(active ? 0.85 : 0, TimeSpan.FromMilliseconds(200)));
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                    new DoubleAnimation(active ? 52 : 40, TimeSpan.FromMilliseconds(200)));
            }

            if (border.BorderBrush is SolidColorBrush brush)
            {
                var normal = ThemeManager.IsDark ? Color.FromRgb(0x22, 0x31, 0x4C) : Color.FromRgb(0xC9, 0xD6, 0xE8);
                var activeColor = ThemeManager.IsDark ? Color.FromRgb(0x38, 0xBD, 0xF8) : Color.FromRgb(0x0E, 0xA5, 0xE9);

                border.BorderBrush = brush.Clone();
                ((SolidColorBrush)border.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(active ? activeColor : normal, TimeSpan.FromMilliseconds(180)));
            }
        }

        private void MainWindow_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _isDragging = true;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void MainWindow_DragLeave(object sender, DragEventArgs e)
        {
            _isDragging = false;
        }

        private void MainWindow_Drop(object sender, DragEventArgs e)
        {
            _isDragging = false;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            if (e.Handled) return;

            e.Handled = true;
            HandleDroppedPaths(paths);
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _isDragging = false;
            SetDragVisual(false, DropScale, DropZone, true);

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            _ = OpenDroppedArchiveAsync(paths);
        }

        private void CreateDropZone_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _isDragging = false;
            SetDragVisual(false, CreateDropScale, CreateDropZone, false);

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            AddSources(paths);
            NavCreate.IsChecked = true;
        }

        private async Task OpenDroppedArchiveAsync(string[] paths)
        {
            var file = paths.FirstOrDefault(File.Exists);
            if (file == null)
            {
                ShowToast("请拖入一个压缩包文件", ToastKind.Info);
                return;
            }
            await OpenArchiveAsync(file);
        }

        private void HandleDroppedPaths(string[] paths)
        {
            if (PageCreate.Visibility == Visibility.Visible)
            {
                AddSources(paths);
                return;
            }

            var archives = paths.Where(IsArchivePath).ToArray();
            if (archives.Length > 1)
            {
                TaskQueue.Enqueue(archives);
                OpenQueuePanel();
                ShowToast("已加入任务队列 · " + archives.Length + " 个压缩包", ToastKind.Info);
                return;
            }

            if (File.Exists(paths[0]))
            {
                _ = OpenDroppedArchiveAsync(paths);
                return;
            }

            AddSources(paths);
            NavCreate.IsChecked = true;
        }

        private static bool IsArchivePath(string path)
        {
            var ext = Path.GetExtension(path);
            return ContextMenuIntegration.ArchiveExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        // ==================== 拖拽追加到压缩包 ====================

        private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _entryDragStart = e.GetPosition(EntryList);
            _entryDragArmed = true;
        }

        /// <summary>把选中的条目拖到资源管理器：先解压到临时目录，再以文件拖放方式交付。</summary>
        private async void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_entryDragArmed || e.LeftButton != MouseButtonState.Pressed) return;

            var position = e.GetPosition(EntryList);
            if (Math.Abs(position.X - _entryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _entryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _entryDragArmed = false;

            if (_session == null || _cts != null) return;

            var rows = EntryList.SelectedItems.OfType<EntryRow>().Where(r => !r.Entry.IsDir).ToList();
            if (rows.Count == 0) return;

            var session = _session;
            var indices = rows.Select(r => r.Index).ToList();
            var temp = Path.Combine(Path.GetTempPath(), "NovaZip", "drag-" + Guid.NewGuid().ToString("N"));

            try
            {
                var result = await Task.Run(() =>
                {
                    Directory.CreateDirectory(temp);
                    return SevenZipEngine.Extract(session, indices, temp, new ProgressState(), CancellationToken.None);
                });

                if (!result.Success)
                {
                    ShowToast("无法准备拖拽文件：" + FirstLine(result.Message), ToastKind.Error);
                    return;
                }

                var files = Directory.GetFiles(temp, "*", SearchOption.AllDirectories);
                if (files.Length == 0) return;

                var data = new DataObject(DataFormats.FileDrop, files);
                DragDrop.DoDragDrop(EntryList, data, DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                ShowToast("拖拽解压失败：" + ex.Message, ToastKind.Error);
            }
        }

        private void EntryList_DragOver(object sender, DragEventArgs e)
        {
            if (_session == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void EntryList_Drop(object sender, DragEventArgs e)
        {
            if (_session == null) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            e.Handled = true;
            _isDragging = false;

            // 拖入同名文件时先确认，避免误覆盖压缩包内已有内容
            var existing = new HashSet<string>(
                _session.Entries.Where(en => !en.IsDir).Select(en => en.Name),
                StringComparer.OrdinalIgnoreCase);
            var conflicts = paths
                .Where(p => File.Exists(p) && existing.Contains(Path.GetFileName(p)))
                .ToList();

            if (conflicts.Count > 0)
            {
                var list = string.Join("\n", conflicts.Take(5).Select(p => Path.GetFileName(p)));
                var more = conflicts.Count > 5 ? "\n…等 " + conflicts.Count + " 个文件" : "";
                var answer = MessageBox.Show(this,
                    "压缩包中已存在以下同名文件，继续将覆盖其内容：\n\n" + list + more,
                    "替换确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.OK) return;
            }

            var archive = _session.FilePath;
            var password = _session.Password;

            var (ok, message) = await Task.Run(() => ArchiveEditor.Add(archive, paths, password));

            if (ok)
            {
                ShowToast("已向压缩包追加 " + paths.Length + " 项", ToastKind.Success);
                await OpenArchiveAsync(archive);
            }
            else
            {
                ShowToast("追加失败：" + FirstLine(message), ToastKind.Error);
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            OpenArchive_Click(sender, e);
        }

        // ==================== 任务队列 ====================

        private void OnQueueChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateQueueBadge));
        }

        private void UpdateQueueBadge()
        {
            var total = TaskQueue.TotalCount;
            var active = TaskQueue.ActiveCount;

            QueueBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueCountText.Text = active > 0 ? active.ToString() : total.ToString();
            QueueEmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

            QueueSummaryText.Text = total == 0
                ? "暂无任务"
                : TaskQueue.IsPaused && active > 0
                    ? "已暂停 · " + active + " 个待继续 · 共 " + total + " 个"
                    : active > 0
                        ? active + " 个进行中 · 共 " + total + " 个"
                        : "共 " + total + " 个任务";

            QueuePauseButton.Content = TaskQueue.IsPaused ? "继续" : "暂停";
            QueuePauseButton.IsEnabled = active > 0;
        }

        private void Queue_Click(object sender, RoutedEventArgs e)
        {
            if (_queueOpen) CloseQueuePanel();
            else OpenQueuePanel();
        }

        private void OpenQueuePanel()
        {
            CloseSettings();
            UpdateQueueBadge();

            if (_queueOpen) return;

            _queueOpen = true;
            QueuePanel.Visibility = Visibility.Visible;
            QueuePanel.Opacity = 0;
            QueueSlide.X = 400;

            QueuePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            QueueSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(400, 0, TimeSpan.FromMilliseconds(340))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            QueueList.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                { BeginTime = TimeSpan.FromMilliseconds(90) });
        }

        private void CloseQueuePanel()
        {
            if (!_queueOpen) return;
            _queueOpen = false;

            var fade = new DoubleAnimation(QueuePanel.Opacity, 0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) => QueuePanel.Visibility = Visibility.Collapsed;
            QueuePanel.BeginAnimation(OpacityProperty, fade);

            QueueSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 40, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        private void QueueClose_Click(object sender, RoutedEventArgs e) => CloseQueuePanel();

        private void QueueOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: QueueItem item } && item.CanOpen)
                ArchivePaths.OpenInExplorer(item.Target);
        }

        private void QueueItemCancel_Click(object sender, RoutedEventArgs e)
        {
            TaskQueue.CancelCurrent();
        }

        private void QueueItemResume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: QueueItem item } && item.CanResume)
                TaskQueue.ResumeItem(item);
        }

        private void QueueCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (TaskQueue.ActiveCount > 0)
            {
                ShowToast("有任务正在运行，完成后再清理断点", ToastKind.Info);
                return;
            }

            TaskQueue.CleanupProgressData();
            ShowToast("已清理全部断点数据", ToastKind.Success);
        }

        private void QueuePause_Click(object sender, RoutedEventArgs e)
        {
            if (TaskQueue.IsPaused) TaskQueue.Resume();
            else TaskQueue.Pause();
        }

        private void QueueCancelAll_Click(object sender, RoutedEventArgs e)
        {
            TaskQueue.CancelAll();
            ShowToast("已取消队列中的任务", ToastKind.Info);
        }

        private void QueueClear_Click(object sender, RoutedEventArgs e)
        {
            TaskQueue.ClearFinished();
        }

        // ==================== Progress overlay ====================

        private void ShowProgress(string title)
        {
            ProgressTitle.Text = title;
            ProgressFileText.Text = "正在准备…";
            ProgressBytesText.Text = "";
            ProgressSpeedText.Text = "";
            ProgressRing.Value = 0;

            _progressTitle = title;
            _paused = false;
            _mainPauseGate.Set();
            PauseResumeText.Text = "暂停";
            PauseResumeButton.Visibility = _canPause ? Visibility.Visible : Visibility.Collapsed;

            _lastCompleted = 0;
            _smoothedSpeed = 0;
            _speedWatch.Restart();
            _lastSpeedTime = 0;

            ProgressOverlay.Visibility = Visibility.Visible;
            ProgressOverlay.Opacity = 0;
            ProgressSlide.Y = 46;

            ProgressOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            ProgressSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(46, 0, TimeSpan.FromMilliseconds(320))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            _progressTimer.Start();
        }

        private void HideProgress()
        {
            _progressTimer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => ProgressOverlay.Visibility = Visibility.Collapsed;
            ProgressOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_progress == null) return;

            var total = _progress.Total;
            var completed = _progress.Completed;
            var percent = total > 0 ? Math.Min(100, completed * 100.0 / total) : 0;

            ProgressRing.Value = percent;
            ProgressFileText.Text = string.IsNullOrEmpty(_progress.CurrentFile)
                ? "正在准备…"
                : _progress.CurrentFile;

            if (total > 0)
                ProgressBytesText.Text = FormatSize((ulong)Math.Max(0, completed)) + " / " + FormatSize((ulong)Math.Max(0, total));
            else
                ProgressBytesText.Text = FormatSize((ulong)Math.Max(0, completed));

            var elapsed = _speedWatch.Elapsed.TotalSeconds;
            var delta = elapsed - _lastSpeedTime;
            if (delta > 0.15)
            {
                var instant = (completed - _lastCompleted) / delta;
                _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.7 + instant * 0.3;
                _lastCompleted = completed;
                _lastSpeedTime = elapsed;
            }

            ProgressSpeedText.Text = _smoothedSpeed > 0 ? FormatSize((ulong)_smoothedSpeed) + "/s" : "";
        }

        private async Task<OperationResult?> RunOperationAsync(string title,
            Func<ProgressState, CancellationToken, OperationResult> action, bool canPause = false)
        {
            if (_cts != null) return null;

            _cts = new CancellationTokenSource();
            _progress = new ProgressState();
            _canPause = canPause;
            ShowProgress(title);

            try
            {
                var result = await Task.Run(() => action(_progress, _cts.Token));

                // 成功时补满进度环并短暂停留，避免停在 9x% 就直接淡出
                if (result.Success)
                {
                    _progressTimer.Stop();
                    ProgressRing.Value = 100;
                    await Task.Delay(220);
                }

                return result;
            }
            catch (Exception ex)
            {
                return new OperationResult { Success = false, Message = ex.Message };
            }
            finally
            {
                HideProgress();
                _mainPauseGate.Set();
                _paused = false;
                _canPause = false;
                _cts.Dispose();
                _cts = null;
                _progress = null;
            }
        }

        private void CancelOperation_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void PauseOperation_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null || !_canPause) return;

            if (_paused)
            {
                _paused = false;
                _mainPauseGate.Set();
                PauseResumeText.Text = "暂停";
                ProgressTitle.Text = _progressTitle;
            }
            else
            {
                _paused = true;
                _mainPauseGate.Reset();
                PauseResumeText.Text = "继续";
                ProgressTitle.Text = "已暂停";
                ProgressSpeedText.Text = "";
            }
        }

        // ==================== Password ====================

        private Task<string?> PromptPasswordAsync(string? archiveName, string error = "")
        {
            PasswordInput.Password = "";
            PasswordErrorText.Text = error;
            PasswordErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            PasswordOverlay.Visibility = Visibility.Visible;
            PasswordOverlay.Opacity = 0;
            PasswordScale.ScaleX = 0.92;
            PasswordScale.ScaleY = 0.92;

            PasswordOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PasswordScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            PasswordScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

            _passwordTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.BeginInvoke(new Action(() => PasswordInput.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            return _passwordTcs.Task;
        }

        private void PasswordOk_Click(object sender, RoutedEventArgs e)
        {
            var value = PasswordInput.Password;
            if (string.IsNullOrEmpty(value))
            {
                PasswordErrorText.Text = "请输入密码";
                PasswordErrorText.Visibility = Visibility.Visible;
                return;
            }

            PasswordOverlay.Visibility = Visibility.Collapsed;
            _passwordTcs?.TrySetResult(value);
            _passwordTcs = null;
        }

        private void PasswordCancel_Click(object sender, RoutedEventArgs e)
        {
            PasswordOverlay.Visibility = Visibility.Collapsed;
            _passwordTcs?.TrySetResult(null);
            _passwordTcs = null;
        }

        // ==================== History ====================

        public enum HistoryKind { Extract, Create, Test }

        private void AddHistory(HistoryKind kind, string name, string detail, bool success)
        {
            _history.Insert(0, HistoryRow.Create(kind, name, detail, success));
            while (_history.Count > 60) _history.RemoveAt(_history.Count - 1);
            UpdateHistoryEmpty();
            HistorySummaryText.Text = "共 " + _history.Count + " 条记录";
        }

        private void UpdateHistoryEmpty()
        {
            HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_history.Count == 0) HistorySummaryText.Text = "";
        }

        // ==================== Toasts ====================

        private enum ToastKind { Info, Success, Error }

        private void ShowToast(string message, ToastKind kind)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var accent = kind switch
            {
                ToastKind.Success => Color.FromRgb(0x34, 0xD3, 0x99),
                ToastKind.Error => Color.FromRgb(0xF8, 0x71, 0x71),
                _ => Color.FromRgb(0x38, 0xBD, 0xF8)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(9, 0, 0, 0),
                FontSize = 12.5,
                Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            });

            var border = new Border
            {
                CornerRadius = new CornerRadius(13),
                Background = TryFindResource("ToastBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0xF5, 0x0D, 0x14, 0x24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 11, 18, 11),
                Margin = new Thickness(0, 0, 0, 9),
                Child = panel,
                Opacity = 0,
                Effect = TryFindResource("CardShadow") as Effect
            };

            var transform = new TranslateTransform(36, 0);
            border.RenderTransform = transform;
            ToastHost.Items.Add(border);

            while (ToastHost.Items.Count > 4) ToastHost.Items.RemoveAt(0);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

            var lifetime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5.5 : 3.4) };
            lifetime.Tick += (_, _) =>
            {
                lifetime.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => ToastHost.Items.Remove(border);
                border.BeginAnimation(OpacityProperty, fade);
            };
            lifetime.Start();
        }

        // ==================== Helpers ====================

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var idx = text.IndexOf('\n');
            return idx < 0 ? text : text.Substring(0, idx).Trim();
        }

        public static string FormatSize(ulong bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double value = bytes;
            string[] units = { "KB", "MB", "GB", "TB", "PB" };
            var unit = -1;
            do
            {
                value /= 1024;
                unit++;
            } while (value >= 1024 && unit < units.Length - 1);

            return value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00") + " " + units[unit];
        }

        private static Geometry GetIcon(string key) =>
            Application.Current.TryFindResource(key) as Geometry ?? Geometry.Empty;

        /// <summary>按当前主题挑选图标颜色：深色用浅色调，浅色用深色调保证对比度。</summary>
        private static Color ThemeColor(byte darkR, byte darkG, byte darkB, byte lightR, byte lightG, byte lightB) =>
            ThemeManager.IsDark
                ? Color.FromRgb(darkR, darkG, darkB)
                : Color.FromRgb(lightR, lightG, lightB);

        private static Color ThemeTint(byte r, byte g, byte b) =>
            ThemeManager.IsDark ? Color.FromArgb(0x26, r, g, b) : Color.FromArgb(0x1C, r, g, b);

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "7z", "zip", "rar", "tar", "gz", "tgz", "xz", "bz2", "zst", "iso", "cab", "wim", "arj", "lzh",
            "jar", "apk", "msi", "rpm", "deb", "dmg", "vhd", "vhdx", "z", "lzma", "zipx", "xapk", "epub"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg", "ico", "tif", "tiff", "heic", "avif", "psd" };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts" };

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "ape" };

        private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "cs", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "hpp", "go", "rs", "rb", "php",
            "html", "css", "scss", "json", "xml", "yml", "yaml", "sql", "sh", "ps1", "bat", "md", "toml", "ini"
        };

        private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "rtf", "csv", "log", "epub", "oft" };

        private static readonly HashSet<string> TextPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "md", "json", "xml", "csv", "log", "ini", "cfg", "conf", "yml", "yaml", "toml",
            "html", "htm", "css", "scss", "js", "ts", "jsx", "tsx", "py", "java", "c", "cpp", "h",
            "hpp", "cs", "go", "rs", "rb", "php", "sh", "ps1", "bat", "cmd", "sql", "properties",
            "gradle", "csproj", "sln", "vb", "lua", "kt", "swift", "dart", "r", "pl", "gitignore", "editorconfig"
        };

        public sealed class EntryRow
        {
            public ArchiveEntry Entry = null!;
            public uint Index;
            public string Name { get; set; } = "";
            public string Directory { get; set; } = "";
            public bool HasDirectory { get; set; }
            public string SizeText { get; set; } = "";
            public string PackedText { get; set; } = "";
            public string DateText { get; set; } = "";
            public bool IsEncrypted { get; set; }
            public Geometry Icon { get; set; } = Geometry.Empty;
            public Brush IconBrush { get; set; } = Brushes.White;
            public Brush IconBackground { get; set; } = Brushes.Transparent;
            public bool ShowRatioBar { get; set; }
            public double RatioWidth { get; set; }

            public static EntryRow Create(ArchiveEntry entry, uint index)
            {
                var (iconKey, color, background) = Classify(entry);
                var ratio = entry.Size > 0 ? Math.Min(1.0, entry.PackSize / (double)entry.Size) : 0;
                // 固实压缩包会把整块体积记在最后一个条目上，超过原始大小时不展示，避免误导
                var hasPack = !entry.IsDir && entry.PackSize > 0 && entry.PackSize <= entry.Size;

                return new EntryRow
                {
                    Entry = entry,
                    Index = index,
                    Name = entry.IsDir ? entry.Name : entry.Name,
                    Directory = entry.Directory,
                    HasDirectory = !string.IsNullOrEmpty(entry.Directory),
                    SizeText = entry.IsDir ? "—" : FormatSize(entry.Size),
                    PackedText = hasPack ? FormatSize(entry.PackSize) : "—",
                    DateText = entry.Modified > DateTime.MinValue ? entry.Modified.ToString("yyyy/MM/dd HH:mm") : "—",
                    IsEncrypted = entry.Encrypted,
                    Icon = GetIcon(iconKey),
                    IconBrush = new SolidColorBrush(color),
                    IconBackground = new SolidColorBrush(background),
                    ShowRatioBar = hasPack && entry.PackSize < entry.Size,
                    RatioWidth = Math.Max(3, Math.Min(60, ratio * 60))
                };
            }

            private static (string Icon, Color Color, Color Background) Classify(ArchiveEntry entry)
            {
                if (entry.IsDir)
                    return ("IconFolder", ThemeColor(0x7D, 0xD3, 0xFC, 0x02, 0x84, 0xC7), ThemeTint(0x38, 0xBD, 0xF8));

                var ext = entry.Extension;
                if (ArchiveExtensions.Contains(ext))
                    return ("IconArchive", ThemeColor(0xC4, 0xB5, 0xFD, 0x7C, 0x3A, 0xED), ThemeTint(0x8B, 0x5C, 0xF6));
                if (ImageExtensions.Contains(ext))
                    return ("IconFile", ThemeColor(0x6E, 0xE7, 0xB7, 0x05, 0x96, 0x69), ThemeTint(0x34, 0xD3, 0x99));
                if (VideoExtensions.Contains(ext))
                    return ("IconFile", ThemeColor(0xFD, 0xBA, 0x74, 0xD9, 0x77, 0x06), ThemeTint(0xF5, 0x9E, 0x0B));
                if (AudioExtensions.Contains(ext))
                    return ("IconFile", ThemeColor(0xF9, 0xA8, 0xD4, 0xDB, 0x27, 0x77), ThemeTint(0xEC, 0x48, 0x99));
                if (CodeExtensions.Contains(ext))
                    return ("IconFile", ThemeColor(0x93, 0xC5, 0xFD, 0x25, 0x63, 0xEB), ThemeTint(0x3B, 0x82, 0xF6));
                if (DocExtensions.Contains(ext))
                    return ("IconFile", ThemeColor(0xFD, 0xA4, 0xAF, 0xE1, 0x1D, 0x48), ThemeTint(0xF4, 0x3F, 0x5E));

                return ("IconFile", ThemeColor(0x94, 0xA3, 0xB8, 0x64, 0x74, 0x8B), ThemeTint(0x94, 0xA3, 0xB8));
            }
        }

        public sealed class SourceRow
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";
            public string SizeText { get; set; } = "";
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
            public Geometry Icon { get; set; } = Geometry.Empty;
            public Brush IconBrush { get; set; } = Brushes.White;
            public Brush IconBackground { get; set; } = Brushes.Transparent;

            public static SourceRow? Create(string path)
            {
                if (Directory.Exists(path))
                {
                    var info = new DirectoryInfo(path);
                    return new SourceRow
                    {
                        Path = path,
                        Name = info.Name,
                        FullPath = path,
                        SizeText = "文件夹",
                        IsDirectory = true,
                        Icon = GetIcon("IconFolder"),
                        IconBrush = new SolidColorBrush(ThemeColor(0x7D, 0xD3, 0xFC, 0x02, 0x84, 0xC7)),
                        IconBackground = new SolidColorBrush(ThemeTint(0x38, 0xBD, 0xF8))
                    };
                }

                if (!File.Exists(path)) return null;

                var file = new FileInfo(path);
                return new SourceRow
                {
                    Path = path,
                    Name = file.Name,
                    FullPath = path,
                    Size = file.Length,
                    SizeText = FormatSize((ulong)file.Length),
                    Icon = GetIcon("IconFile"),
                    IconBrush = new SolidColorBrush(ThemeColor(0x93, 0xC5, 0xFD, 0x25, 0x63, 0xEB)),
                    IconBackground = new SolidColorBrush(ThemeTint(0x3B, 0x82, 0xF6))
                };
            }
        }

        public sealed class HistoryRow
        {
            public string Title { get; set; } = "";
            public string Detail { get; set; } = "";
            public string TimeText { get; set; } = "";
            public string StatusText { get; set; } = "";
            public Brush StatusBrush { get; set; } = Brushes.White;
            public Brush StatusBackground { get; set; } = Brushes.Transparent;
            public Brush StatusBorder { get; set; } = Brushes.Transparent;
            public Geometry Icon { get; set; } = Geometry.Empty;
            public Brush IconBrush { get; set; } = Brushes.White;
            public Brush IconBackground { get; set; } = Brushes.Transparent;

            public static HistoryRow Create(HistoryKind kind, string name, string detail, bool success)
            {
                var (iconKey, color, background, title) = kind switch
                {
                    HistoryKind.Extract => ("IconExtract", ThemeColor(0x7D, 0xD3, 0xFC, 0x02, 0x84, 0xC7), ThemeTint(0x38, 0xBD, 0xF8), "解压"),
                    HistoryKind.Create => ("IconCreate", ThemeColor(0xC4, 0xB5, 0xFD, 0x7C, 0x3A, 0xED), ThemeTint(0x8B, 0x5C, 0xF6), "压缩"),
                    _ => ("IconCheck", ThemeColor(0x6E, 0xE7, 0xB7, 0x05, 0x96, 0x69), ThemeTint(0x34, 0xD3, 0x99), "校验")
                };

                var statusColor = success
                    ? ThemeColor(0x34, 0xD3, 0x99, 0x05, 0x96, 0x69)
                    : ThemeColor(0xF8, 0x71, 0x71, 0xDC, 0x26, 0x26);

                return new HistoryRow
                {
                    Title = title + " · " + name,
                    Detail = detail,
                    TimeText = DateTime.Now.ToString("HH:mm:ss"),
                    StatusText = success ? "成功" : "失败",
                    StatusBrush = new SolidColorBrush(statusColor),
                    StatusBackground = new SolidColorBrush(Color.FromArgb(0x1A, statusColor.R, statusColor.G, statusColor.B)),
                    StatusBorder = new SolidColorBrush(Color.FromArgb(0x40, statusColor.R, statusColor.G, statusColor.B)),
                    Icon = GetIcon(iconKey),
                    IconBrush = new SolidColorBrush(color),
                    IconBackground = new SolidColorBrush(background)
                };
            }
        }
    }
}
