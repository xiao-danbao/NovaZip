using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NovaZip
{
    /// <summary>
    /// 为无边框窗口启用 DWM 亚克力背景（Windows 11 22H2+），
    /// 在旧系统上回退到 SetWindowCompositionAttribute 模糊。
    /// </summary>
    internal static class AcrylicHelper
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaSystemBackdropType = 38;

        private const int AccentEnableAcrylicBlurBehind = 4;
        private const int WcaAccentPolicy = 19;

        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                if (PresentationSource.FromVisual(window) is HwndSource source &&
                    source.CompositionTarget != null)
                {
                    // 关键：让 WPF 客户端区域保持透明，DWM 模糊层才能透出桌面
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }
            }
            catch
            {
                // 忽略透明背景设置失败
            }

            try
            {
                var margins = new Margins { Left = -1, Top = -1, Right = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                var backdrop = 3; // DWMSBT_TRANSIENTWINDOW → 亚克力
                if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
                    EnableLegacyBlur(hwnd, ThemeManager.IsDark);
            }
            catch
            {
                // 更旧的系统：保持半透明底色作为回退
            }

            try
            {
                var dark = ThemeManager.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));

                var round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

                var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
            }
            catch
            {
                // 旧系统不支持圆角与边框色
            }
        }

        private static void EnableLegacyBlur(IntPtr hwnd, bool dark)
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = dark ? 0xCC000000u : 0xCCF2F6FCu
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    }
}
