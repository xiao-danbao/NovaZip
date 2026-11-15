using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace NovaZip
{
    /// <summary>在运行时切换深色 / 浅色 / 跟随系统主题，并记住用户的选择。</summary>
    internal static class ThemeManager
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_THEMECHANGED = 0x031A;

        private static bool _isDark = true;
        private static bool _applied;

        public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;

        public static bool IsDark => _isDark;

        /// <summary>界面配色真正发生变化时触发，供窗口同步图标与背景模糊。</summary>
        public static event Action? ThemeChanged;

        /// <summary>仅主题模式（跟随系统 / 深色 / 浅色）变化时触发，配色可能不变。</summary>
        public static event Action? ModeChanged;

        public static void Initialize()
        {
            AppSettings.Load();
            Mode = AppSettings.Theme;
            Apply(Resolve(Mode));

            try
            {
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (Mode != ThemeMode.Auto) return;
                    if (e.Category != UserPreferenceCategory.General &&
                        e.Category != UserPreferenceCategory.VisualStyle &&
                        e.Category != UserPreferenceCategory.Color) return;

                    var app = Application.Current;
                    if (app == null) return;
                    app.Dispatcher.BeginInvoke(new Action(RefreshIfAuto));
                };
            }
            catch
            {
                // 监听系统主题失败时仅保留手动切换
            }
        }

        /// <summary>
        /// 挂到窗口消息循环：Windows 切换亮暗模式时只广播 WM_SETTINGCHANGE，
        /// 部分系统版本不会触发 UserPreferenceChanged，这里补齐实时跟随。
        /// </summary>
        public static void AttachWindow(Window window)
        {
            if (window == null) return;

            HwndSource? source = null;
            HwndSourceHook? hook = null;

            void Release()
            {
                if (source != null && hook != null) source.RemoveHook(hook);
                source = null;
                hook = null;
            }

            window.SourceInitialized += (_, _) =>
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                if (source == null) return;

                hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_SETTINGCHANGE || msg == WM_THEMECHANGED)
                        window.Dispatcher.BeginInvoke(new Action(RefreshIfAuto));
                    return IntPtr.Zero;
                };

                source.AddHook(hook);
            };

            window.Closed += (_, _) => Release();
        }

        /// <summary>跟随系统模式下重新读取系统配色，供窗口激活或系统广播时调用。</summary>
        public static void RefreshIfAuto()
        {
            if (Mode != ThemeMode.Auto) return;
            Apply(Resolve(ThemeMode.Auto));
        }

        public static bool Toggle() => SetMode(_isDark ? ThemeMode.Light : ThemeMode.Dark);

        public static bool SetMode(ThemeMode mode)
        {
            var changed = Mode != mode;
            Mode = mode;
            AppSettings.Theme = mode;
            AppSettings.Save();
            Apply(Resolve(mode));
            if (changed) ModeChanged?.Invoke();
            return true;
        }

        private static bool Resolve(ThemeMode mode) => mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => AppSettings.SystemUsesDarkTheme()
        };

        private static void Apply(bool dark)
        {
            if (_applied && dark == _isDark) return;

            _isDark = dark;
            _applied = true;

            var app = Application.Current;
            if (app != null)
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative)
                };

                var merged = app.Resources.MergedDictionaries;
                var existing = merged.FirstOrDefault(d =>
                    d.Source != null &&
                    d.Source.OriginalString.IndexOf("Colors.", StringComparison.OrdinalIgnoreCase) >= 0);

                if (existing != null)
                    merged[merged.IndexOf(existing)] = dictionary;
                else
                    merged.Insert(0, dictionary);
            }

            ThemeChanged?.Invoke();
        }
    }
}
