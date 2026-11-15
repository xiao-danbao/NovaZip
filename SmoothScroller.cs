using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NovaZip
{
    /// <summary>
    /// 滚轮平滑滚动：接管按像素滚动的容器，在 CompositionTarget.Rendering 中
    /// 跟随显示器刷新率逐帧缓动，替代系统默认的整格跳变。
    /// </summary>
    internal static class SmoothScroller
    {
        private sealed class ScrollState
        {
            public ScrollViewer Viewer = null!;
            public double Target;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> States = new();
        private static bool _renderingHooked;

        public static void Attach()
        {
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnPreviewMouseWheel), true);
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

            var viewer = FindScrollable(e.OriginalSource as DependencyObject);
            if (viewer == null) return;

            var step = Math.Min(220, Math.Max(56, viewer.ViewportHeight * 0.10));
            var notches = e.Delta / 120.0;

            if (!States.TryGetValue(viewer, out var state))
            {
                state = new ScrollState { Viewer = viewer, Target = viewer.VerticalOffset };
                States[viewer] = state;
            }

            state.Target = Math.Clamp(state.Target - notches * step, 0, viewer.ScrollableHeight);

            e.Handled = true;
            HookRendering();
        }

        private static void HookRendering()
        {
            if (_renderingHooked) return;
            _renderingHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>每帧推进一段距离，屏幕刷新率越高越顺滑，动画结束后自动停止订阅。</summary>
        private static void OnRendering(object? sender, EventArgs e)
        {
            if (States.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingHooked = false;
                return;
            }

            foreach (var state in new List<ScrollState>(States.Values))
            {
                var viewer = state.Viewer;
                var target = Math.Clamp(state.Target, 0, viewer.ScrollableHeight);
                var offset = viewer.VerticalOffset;
                var diff = target - offset;

                if (Math.Abs(diff) <= 0.5)
                {
                    viewer.ScrollToVerticalOffset(target);
                    States.Remove(viewer);
                    continue;
                }

                // 指数缓动：约 0.1 秒内完成大部分位移，末尾自然收住
                viewer.ScrollToVerticalOffset(offset + diff * 0.32);
            }
        }

        /// <summary>只接管按像素滚动的容器：逻辑滚动（整行跳动）保持系统原生行为。</summary>
        private static ScrollViewer? FindScrollable(DependencyObject? source)
        {
            var current = source;

            while (current != null)
            {
                if (current is ScrollViewer viewer &&
                    viewer.ScrollableHeight > 0.5 &&
                    ScrollsByPixel(viewer))
                {
                    return viewer;
                }

                current = ParentOf(current);
            }

            return null;
        }

        private static bool ScrollsByPixel(ScrollViewer viewer)
        {
            if (!viewer.CanContentScroll) return true;

            // 虚拟化列表设置 ScrollUnit=Pixel 后同样按像素滚动
            var panel = FindChild<VirtualizingStackPanel>(viewer, 4);
            return panel != null && VirtualizingPanel.GetScrollUnit(panel) == ScrollUnit.Pixel;
        }

        private static DependencyObject? ParentOf(DependencyObject node) =>
            node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

        private static T? FindChild<T>(DependencyObject parent, int depth) where T : DependencyObject
        {
            if (depth < 0) return null;

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;

                var nested = FindChild<T>(child, depth - 1);
                if (nested != null) return nested;
            }

            return null;
        }
    }
}
