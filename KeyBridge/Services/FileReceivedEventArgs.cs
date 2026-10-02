using System;

namespace KeyBridge.Services;

public sealed class FileReceivedEventArgs(string fileName, string savedPath, long size) : EventArgs
{
    public string FileName { get; } = fileName;
    public string SavedPath { get; } = savedPath;
    public long Size { get; } = size;
}
