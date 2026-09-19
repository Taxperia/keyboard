using System;

namespace KeyBridge.Services.WindowsInput;

public sealed class MouseHookEventArgs : EventArgs
{
    public MouseHookEventArgs(
        string action,
        int x,
        int y,
        int wheelDelta,
        bool isInjected)
    {
        Action = action;
        X = x;
        Y = y;
        WheelDelta = wheelDelta;
        IsInjected = isInjected;
    }

    public string Action { get; }

    public int X { get; }

    public int Y { get; }

    public int WheelDelta { get; }

    public bool IsInjected { get; }

    public bool Handled { get; set; }
}
