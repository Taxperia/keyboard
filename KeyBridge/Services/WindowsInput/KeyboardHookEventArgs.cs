using System;

namespace KeyBridge.Services.WindowsInput;

public sealed class KeyboardHookEventArgs : EventArgs
{
    public KeyboardHookEventArgs(
        int virtualKey,
        int scanCode,
        bool isKeyDown,
        bool isExtendedKey,
        bool isInjected)
    {
        VirtualKey = virtualKey;
        ScanCode = scanCode;
        IsKeyDown = isKeyDown;
        IsExtendedKey = isExtendedKey;
        IsInjected = isInjected;
    }

    public int VirtualKey { get; }

    public int ScanCode { get; }

    public bool IsKeyDown { get; }

    public bool IsExtendedKey { get; }

    public bool IsInjected { get; }

    public bool Handled { get; set; }
}
