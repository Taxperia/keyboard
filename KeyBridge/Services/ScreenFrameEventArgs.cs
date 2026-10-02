using System;

namespace KeyBridge.Services;

public sealed class ScreenFrameEventArgs(byte[] imageBytes, long latencyMilliseconds) : EventArgs
{
    public byte[] ImageBytes { get; } = imageBytes;
    public long LatencyMilliseconds { get; } = latencyMilliseconds;
}
