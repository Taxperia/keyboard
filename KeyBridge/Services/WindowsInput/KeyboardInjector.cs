using System;
using System.Runtime.InteropServices;
using KeyBridge.Models;

namespace KeyBridge.Services.WindowsInput;

public sealed class KeyboardInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private static readonly int[] SafetyReleaseKeys =
    [
        0x10, 0x11, 0x12, 0x5B, 0x5C,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5
    ];

    public string LastError { get; private set; } = "";

    public bool Inject(KeyboardPacket packet)
    {
        return string.IsNullOrEmpty(packet.Text)
            ? InjectKey(packet)
            : InjectText(packet.Text);
    }

    public bool InjectMouse(MousePacket packet)
    {
        var flags = packet.Action switch
        {
            "move" => packet.IsAbsolute
                ? MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                : MOUSEEVENTF_MOVE,
            "leftDown" => MOUSEEVENTF_LEFTDOWN,
            "leftUp" => MOUSEEVENTF_LEFTUP,
            "rightDown" => MOUSEEVENTF_RIGHTDOWN,
            "rightUp" => MOUSEEVENTF_RIGHTUP,
            "middleDown" => MOUSEEVENTF_MIDDLEDOWN,
            "middleUp" => MOUSEEVENTF_MIDDLEUP,
            "wheel" => MOUSEEVENTF_WHEEL,
            _ => 0u
        };

        if (flags == 0)
        {
            return true;
        }

        var input = new Input
        {
            Type = INPUT_MOUSE,
            U = new InputUnion
            {
                Mi = new MouseInput
                {
                    Dx = packet.IsAbsolute ? packet.AbsoluteX : packet.DeltaX,
                    Dy = packet.IsAbsolute ? packet.AbsoluteY : packet.DeltaY,
                    MouseData = packet.Action == "wheel" ? unchecked((uint)packet.WheelDelta) : 0,
                    DwFlags = flags,
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };

        return SendInputs([input]);
    }

    private bool InjectKey(KeyboardPacket packet)
    {
        var flags = packet.IsKeyDown ? 0u : KEYEVENTF_KEYUP;
        if (packet.IsExtendedKey)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        var input = new Input
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                Ki = new KeybdInput
                {
                    WVk = (ushort)packet.VirtualKey,
                    WScan = (ushort)packet.ScanCode,
                    DwFlags = flags,
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };

        return SendInputs([input]);
    }

    private bool InjectText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var inputs = new Input[text.Length * 2];
        var index = 0;

        foreach (var character in text)
        {
            inputs[index++] = CreateUnicodeInput(character, isKeyDown: true);
            inputs[index++] = CreateUnicodeInput(character, isKeyDown: false);
        }

        return SendInputs(inputs);
    }

    public void ReleaseSafetyKeys()
    {
        var inputs = new Input[SafetyReleaseKeys.Length];

        for (var i = 0; i < SafetyReleaseKeys.Length; i++)
        {
            var virtualKey = SafetyReleaseKeys[i];
            inputs[i] = new Input
            {
                Type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    Ki = new KeybdInput
                    {
                        WVk = (ushort)virtualKey,
                        WScan = 0,
                        DwFlags = KEYEVENTF_KEYUP | IsExtendedVirtualKey(virtualKey),
                        Time = 0,
                        DwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        SendInputs(inputs);
    }

    private static Input CreateUnicodeInput(char character, bool isKeyDown)
    {
        return new Input
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                Ki = new KeybdInput
                {
                    WVk = 0,
                    WScan = character,
                    DwFlags = isKeyDown ? KEYEVENTF_UNICODE : KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private bool SendInputs(Input[] inputs)
    {
        LastError = "";
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
        {
            return true;
        }

        LastError = $"SendInput hata kodu: {Marshal.GetLastWin32Error()}";
        return false;
    }

    private static uint IsExtendedVirtualKey(int virtualKey)
    {
        return virtualKey is 0x5B or 0x5C or 0xA3 or 0xA5
            ? KEYEVENTF_EXTENDEDKEY
            : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mi;

        [FieldOffset(0)]
        public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
