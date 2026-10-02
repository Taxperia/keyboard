using System;
using System.Threading;
using System.Threading.Tasks;

namespace KeyBridge.Services;

public sealed class ConnectionApprovalRequestedEventArgs(
    string deviceId,
    string deviceName,
    string ipAddress) : EventArgs
{
    private readonly TaskCompletionSource<bool> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DeviceId { get; } = deviceId;

    public string DeviceName { get; } = deviceName;

    public string IpAddress { get; } = ipAddress;

    public void Respond(bool accepted) => _decision.TrySetResult(accepted);

    internal Task<bool> WaitForDecisionAsync(CancellationToken cancellationToken) =>
        _decision.Task.WaitAsync(cancellationToken);
}
