namespace KeyBridge.Models;

public sealed class KeyboardPacket
{
    public string Type { get; set; } = "keyboard";

    public string SourceDeviceId { get; set; } = "";

    public string PairingToken { get; set; } = "";

    public int VirtualKey { get; set; }

    public int ScanCode { get; set; }

    public bool IsKeyDown { get; set; }

    public bool IsExtendedKey { get; set; }

    public string Text { get; set; } = "";
}
