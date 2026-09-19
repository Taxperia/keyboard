using System;

namespace KeyBridge.Models;

public sealed class PeerDevice
{
    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "";

    public DeviceRole Role { get; set; }

    public string IpAddress { get; set; } = "";

    public int PairingPort { get; set; }

    public int KeyboardPort { get; set; }

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(DeviceName) ? DeviceId : DeviceName;
}
