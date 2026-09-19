using System;

namespace KeyBridge.Services;

public sealed class PeerDisconnectedEventArgs : EventArgs
{
    public PeerDisconnectedEventArgs(string deviceName)
    {
        DeviceName = deviceName;
    }

    public string DeviceName { get; }
}
