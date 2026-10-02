using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KeyBridge.Services.WindowsInput;

public static class WindowMonitorBounds
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rect GetMonitorBounds(Window window) => GetMonitorRect(window, useWorkArea: false);

    public static Rect GetMonitorWorkArea(Window window) => GetMonitorRect(window, useWorkArea: true);

    private static Rect GetMonitorRect(Window window, bool useWorkArea)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            if (useWorkArea)
            {
                return SystemParameters.WorkArea;
            }

            return new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }

        var nativeRect = useWorkArea ? info.WorkArea : info.Monitor;
        var source = HwndSource.FromHwnd(handle);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(nativeRect.Left, nativeRect.Top));
        var bottomRight = fromDevice.Transform(new Point(nativeRect.Right, nativeRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
