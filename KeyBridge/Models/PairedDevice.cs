namespace KeyBridge.Models;

public sealed class PairedDevice
{
    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "";

    public string IpAddress { get; set; } = "";

    public int KeyboardPort { get; set; }

    public string PairingToken { get; set; } = "";
}
