using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NeoIDE.Services;

/// <summary>Flashes the taskbar button (orange) until the window is activated: the "push" notification of the updates.</summary>
public static class TaskbarFlash
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    public static void Flash(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG, uCount = 3 };
        FlashWindowEx(ref info);
    }
}
