namespace KeyBridge.Models;

public sealed class ConnectionPacket
{
    public string Type { get; set; } = "connection";

    public string SourceDeviceId { get; set; } = "";

    public string PairingToken { get; set; } = "";

    public string Action { get; set; } = "end-session";
}
