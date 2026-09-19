using System;
using System.Runtime.InteropServices;

namespace KeyBridge.Services.WindowsInput;

public static class ScreenCoordinateMapper
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int AbsoluteCoordinateMaximum = 65535;

    public static (int X, int Y) ToAbsoluteVirtualDesktop(int screenX, int screenY)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));

        return (
            Normalize(screenX, left, width),
            Normalize(screenY, top, height));
    }

    internal static int Normalize(int coordinate, int origin, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        var offset = Math.Clamp((long)coordinate - origin, 0, length - 1L);
        return (int)Math.Round(offset * AbsoluteCoordinateMaximum / (double)(length - 1));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
