using System;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class PairingCompletedEventArgs : EventArgs
{
    public PairingCompletedEventArgs(PairedDevice device, bool isIncoming = false)
    {
        Device = device;
        IsIncoming = isIncoming;
    }

    public PairedDevice Device { get; }

    public bool IsIncoming { get; }
}
