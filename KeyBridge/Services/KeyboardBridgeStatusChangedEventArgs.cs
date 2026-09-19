using System;

namespace KeyBridge.Services;

public sealed class KeyboardBridgeStatusChangedEventArgs : EventArgs
{
    public KeyboardBridgeStatusChangedEventArgs(
        bool isRemoteControlActive,
        string message,
        long sentPackets,
        long receivedPackets)
    {
        IsRemoteControlActive = isRemoteControlActive;
        Message = message;
        SentPackets = sentPackets;
        ReceivedPackets = receivedPackets;
    }

    public bool IsRemoteControlActive { get; }

    public string Message { get; }

    public long SentPackets { get; }

    public long ReceivedPackets { get; }
}
