namespace KeyBridge.Models;

public sealed class MousePacket
{
    public string Type { get; set; } = "mouse";

    public string SourceDeviceId { get; set; } = "";

    public string PairingToken { get; set; } = "";

    public string Action { get; set; } = "move";

    public int DeltaX { get; set; }

    public int DeltaY { get; set; }

    // Absolute coordinates use the Win32 0..65535 virtual-desktop range. Keeping
    // the relative fields above makes packets compatible with older clients.
    public bool IsAbsolute { get; set; }

    public int AbsoluteX { get; set; }

    public int AbsoluteY { get; set; }

    public int WheelDelta { get; set; }
}
