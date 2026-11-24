using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NeoIDE.Services;

/// <summary>
/// Keeps borderless (WindowChrome) windows on screen:
/// - the initial size never exceeds the monitor work area (high DPI / small screens),
/// - maximizing fills exactly the work area (title bar visible, taskbar not covered),
/// - the main window position/size/state is remembered between sessions.
/// </summary>
public static class WindowPlacement
{
    public static void Attach(Window window, bool remember = false)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            if (remember) Restore(window);
            FitToWorkArea(window);
        };
        if (remember)
            window.Closing += (_, _) => Save(window);
    }

    /// <summary>Shrinks and moves the window so it is fully visible on its monitor.</summary>
    public static void FitToWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        var work = WorkAreaOf(window);
        if (window.SizeToContent == SizeToContent.Manual)
        {
            if (double.IsNaN(window.Width) || window.Width > work.Width) window.Width = Math.Max(window.MinWidth, work.Width * 0.92);
            if (double.IsNaN(window.Height) || window.Height > work.Height) window.Height = Math.Max(window.MinHeight, work.Height * 0.92);
        }
        else
        {
            window.MaxWidth = work.Width;
            window.MaxHeight = work.Height;
        }
        var w = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        var h = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        if (window.WindowStartupLocation != WindowStartupLocation.Manual || double.IsNaN(window.Left) || double.IsNaN(window.Top))
        {
            if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen || window.Owner == null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = work.Left + (work.Width - w) / 2;
                window.Top = work.Top + (work.Height - h) / 2;
            }
            return;
        }
        window.Left = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - w));
        window.Top = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
    }

    /// <summary>
    /// How far a maximized borderless window overhangs the work area (Windows keeps the resize frame
    /// off-screen). The window content must be inset by this amount or its edges — title bar included — are cut.
    /// </summary>
    public static Thickness MaximizedInset(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.WindowState != WindowState.Maximized) return new Thickness(0);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(hwnd, out var r) || !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
            return new Thickness(7);
        var dpi = GetDpiForWindow(hwnd) / 96.0;
        if (dpi <= 0) dpi = 1;
        double D(int px) => Math.Max(0, px) / dpi;
        return new Thickness(D(info.rcWork.Left - r.Left), D(info.rcWork.Top - r.Top), D(r.Right - info.rcWork.Right), D(r.Bottom - info.rcWork.Bottom));
    }

    // ------------------------------------------------------------------ persistence

    private static void Restore(Window window)
    {
        var s = AppSettings.Current;
        if (s.WindowWidth is not > 0 || s.WindowHeight is not > 0) return;
        var rect = new Rect(s.WindowLeft, s.WindowTop, s.WindowWidth.Value, s.WindowHeight.Value);
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(rect)) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = rect.Width;
        window.Height = rect.Height;
        if (s.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    private static void Save(Window window)
    {
        var s = AppSettings.Current;
        var r = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        if (r.IsEmpty || r.Width < 100) return;
        s.WindowLeft = r.Left;
        s.WindowTop = r.Top;
        s.WindowWidth = r.Width;
        s.WindowHeight = r.Height;
        s.WindowMaximized = window.WindowState == WindowState.Maximized;
        s.Save();
    }

    // ------------------------------------------------------------------ monitor work area

    private static Rect WorkAreaOf(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        // physical pixels → WPF units
        var source = PresentationSource.FromVisual(window);
        var m = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var tl = m.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var br = m.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(tl, br);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
}
