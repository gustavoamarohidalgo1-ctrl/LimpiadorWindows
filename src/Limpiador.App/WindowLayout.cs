using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Limpiador.App;

/// <summary>Keeps every control reachable when Windows scaling leaves a small work area.</summary>
internal static class WindowLayout
{
    public static void Attach(Window window, double minimumContentHeight, double minimumContentWidth = 440)
    {
        window.MinWidth = 320;
        window.MinHeight = 240;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window.Owner ?? window).Handle;
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = Math.Max(96u, GetDpiForWindow(handle));
                var width = (info.Work.Right - info.Work.Left) * 96.0 / dpi;
                var height = (info.Work.Bottom - info.Work.Top) * 96.0 / dpi;
                window.Width = Math.Min(window.Width, Math.Max(240, width - 16));
                window.Height = Math.Min(window.Height, Math.Max(180, height - 16));
                window.MinWidth = Math.Min(window.MinWidth, window.Width);
                window.MinHeight = Math.Min(window.MinHeight, window.Height);
            }
            if (window.Content is not FrameworkElement content) return;
            // A finite content height preserves virtualization in the inner lists.
            // Scrolling the whole dialog remains available below this useful size.
            window.Content = null;
            var viewport = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false,
                Content = content,
                Focusable = false
            };
            void ResizeContent()
            {
                content.Width = Math.Max(minimumContentWidth, viewport.ViewportWidth - content.Margin.Left - content.Margin.Right);
                content.Height = Math.Max(minimumContentHeight, viewport.ViewportHeight - content.Margin.Top - content.Margin.Bottom);
            }
            viewport.ScrollChanged += (_, args) =>
            {
                if (args.ViewportWidthChange != 0 || args.ViewportHeightChange != 0) ResizeContent();
            };
            viewport.Loaded += (_, _) => ResizeContent();
            content.Width = minimumContentWidth;
            content.Height = minimumContentHeight;
            window.Content = viewport;
        };
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
