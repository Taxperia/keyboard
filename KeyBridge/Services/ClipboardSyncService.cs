using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class ClipboardSyncService : IDisposable
{
    private const int MaximumClipboardBytes = 1024 * 1024;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;
    private volatile bool _incomingSessionApproved;

    public event EventHandler<string>? TextReceived;
    public event EventHandler<string>? StatusChanged;

    public void Start(AppSettings settings)
    {
        DisposeListener();
        _incomingSessionApproved = false;
        _settings = settings;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, NetworkPorts.Clipboard);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void SetIncomingSessionApproved(bool approved) => _incomingSessionApproved = approved;

    public async Task SendTextAsync(PairedDevice device, string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaximumClipboardBytes)
        {
            StatusChanged?.Invoke(this, "Pano metni 1 MB sınırını aştığı için gönderilmedi.");
            return;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(device.IpAddress), NetworkPorts.Clipboard, cancellationToken);
        await using var stream = client.GetStream();
        await NetworkProtocol.WriteFrameAsync(stream, SecureMessage.ProtectBytes(bytes, device.PairingToken), cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => ReceiveTextAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Pano bağlantısı hatası: {ex.Message}"); }
        }
    }

    private async Task ReceiveTextAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            if (!_incomingSessionApproved)
            {
                StatusChanged?.Invoke(this, "Onaylanmamış pano bağlantısı reddedildi.");
                return;
            }

            var token = _settings?.PairedDevice?.PairingToken;
            var encrypted = await NetworkProtocol.ReadFrameAsync(stream, MaximumClipboardBytes + 64, cancellationToken);
            if (encrypted is null || string.IsNullOrWhiteSpace(token) ||
                !SecureMessage.TryUnprotectBytes(encrypted, token, out var plaintext))
            {
                StatusChanged?.Invoke(this, "Yetkisiz pano paketi reddedildi.");
                return;
            }

            TextReceived?.Invoke(this, Encoding.UTF8.GetString(plaintext));
        }
    }

    private void DisposeListener()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public void Dispose() => DisposeListener();
}
