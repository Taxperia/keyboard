using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyBridge.Services.WindowsInput;

public sealed class KeyboardTextTranslator
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkCapital = 0x14;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;

    public bool TryTranslate(
        KeyboardHookEventArgs args,
        bool ctrlDown,
        bool altDown,
        bool shiftDown,
        out string text)
    {
        text = "";

        if (!args.IsKeyDown || ctrlDown || altDown || IsNonTextKey(args.VirtualKey))
        {
            return false;
        }

        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
        {
            return false;
        }

        keyboardState[args.VirtualKey] = 0x80;
        keyboardState[VkControl] = 0;
        keyboardState[VkMenu] = 0;
        keyboardState[VkShift] = shiftDown ? (byte)0x80 : (byte)0;
        keyboardState[VkLeftShift] = shiftDown ? (byte)0x80 : (byte)0;
        keyboardState[VkRightShift] = shiftDown ? (byte)0x80 : (byte)0;
        keyboardState[VkCapital] = (byte)(GetKeyState(VkCapital) & 0x01);

        var buffer = new StringBuilder(8);
        var keyboardLayout = GetKeyboardLayout(0);
        var result = ToUnicodeEx(
            (uint)args.VirtualKey,
            (uint)args.ScanCode,
            keyboardState,
            buffer,
            buffer.Capacity,
            0,
            keyboardLayout);

        if (result <= 0)
        {
            return false;
        }

        text = buffer.ToString(0, Math.Min(result, buffer.Length));
        return text.Length > 0 && !char.IsControl(text[0]);
    }

    private static bool IsNonTextKey(int virtualKey)
    {
        return virtualKey is
            < 0x20 or
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
            0x2D or 0x2E or
            >= 0x70 and <= 0x87 or
            >= 0x90 and <= 0x9F or
            >= 0xA0 and <= 0xA5 or
            >= 0xB0 and <= 0xB7;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);
}
