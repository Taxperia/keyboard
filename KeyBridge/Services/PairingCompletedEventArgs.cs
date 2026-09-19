using System;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class PairingCompletedEventArgs : EventArgs
{
    public PairingCompletedEventArgs(PairedDevice device)
    {
        Device = device;
    }

    public PairedDevice Device { get; }
}
