using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyBridge.Services.WindowsInput;

public sealed class LowLevelMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int LLMHF_INJECTED = 0x01;

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId;

    public LowLevelMouseHook()
    {
        _proc = HookCallback;
    }

    public event EventHandler<MouseHookEventArgs>? MouseEvent;

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule is null
            ? IntPtr.Zero
            : GetModuleHandle(currentModule.ModuleName);

        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var action = message switch
            {
                WM_MOUSEMOVE => "move",
                WM_LBUTTONDOWN => "leftDown",
                WM_LBUTTONUP => "leftUp",
                WM_RBUTTONDOWN => "rightDown",
                WM_RBUTTONUP => "rightUp",
                WM_MBUTTONDOWN => "middleDown",
                WM_MBUTTONUP => "middleUp",
                WM_MOUSEWHEEL => "wheel",
                _ => ""
            };

            if (!string.IsNullOrEmpty(action))
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                var wheelDelta = message == WM_MOUSEWHEEL
                    ? unchecked((short)((info.MouseData >> 16) & 0xFFFF))
                    : 0;

                var args = new MouseHookEventArgs(
                    action,
                    info.Point.X,
                    info.Point.Y,
                    wheelDelta,
                    (info.Flags & LLMHF_INJECTED) != 0);

                MouseEvent?.Invoke(this, args);

                if (args.Handled)
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
