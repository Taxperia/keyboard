using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class FileTransferService : IDisposable
{
    private const int MaximumFileSize = 100 * 1024 * 1024;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;
    private readonly string? _receivingDirectory;
    private volatile bool _incomingSessionApproved;

    public FileTransferService(string? receivingDirectory = null)
    {
        _receivingDirectory = receivingDirectory;
    }

    public event EventHandler<FileReceivedEventArgs>? FileReceived;
    public event EventHandler<string>? StatusChanged;

    public void Start(AppSettings settings)
    {
        DisposeListener();
        _incomingSessionApproved = false;
        _settings = settings;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, NetworkPorts.FileTransfer);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void SetIncomingSessionApproved(bool approved) => _incomingSessionApproved = approved;

    public async Task SendFileAsync(PairedDevice device, string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Gönderilecek dosya bulunamadı.", path);
        }

        if (info.Length > MaximumFileSize)
        {
            throw new InvalidOperationException("Dosya aktarımı en fazla 100 MB destekliyor.");
        }

        StatusChanged?.Invoke(this, $"{info.Name} şifreleniyor...");
        var fileBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var encryptedFile = SecureMessage.ProtectBytes(fileBytes, device.PairingToken);
        var header = new TransferHeader { FileName = info.Name, PlainLength = info.Length };
        var encryptedHeader = SecureMessage.ProtectText(JsonSerializer.Serialize(header), device.PairingToken);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(device.IpAddress), NetworkPorts.FileTransfer, cancellationToken);
        await using var stream = client.GetStream();
        await NetworkProtocol.WriteFrameAsync(stream, System.Text.Encoding.UTF8.GetBytes(encryptedHeader), cancellationToken);
        await NetworkProtocol.WriteFrameAsync(stream, encryptedFile, cancellationToken);
        StatusChanged?.Invoke(this, $"{info.Name} gönderildi.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => ReceiveFileAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Dosya alımı hatası: {ex.Message}"); }
        }
    }

    private async Task ReceiveFileAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            if (!_incomingSessionApproved)
            {
                StatusChanged?.Invoke(this, "Onaylanmamış dosya aktarımı reddedildi.");
                return;
            }

            var token = _settings?.PairedDevice?.PairingToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var headerBytes = await NetworkProtocol.ReadFrameAsync(stream, 16 * 1024, cancellationToken);
            var encryptedFile = await NetworkProtocol.ReadFrameAsync(stream, MaximumFileSize + 64, cancellationToken);
            if (headerBytes is null || encryptedFile is null ||
                !SecureMessage.TryUnprotectText(System.Text.Encoding.UTF8.GetString(headerBytes), token, out var headerJson) ||
                !SecureMessage.TryUnprotectBytes(encryptedFile, token, out var fileBytes))
            {
                StatusChanged?.Invoke(this, "Geçersiz veya yetkisiz dosya aktarımı reddedildi.");
                return;
            }

            var header = JsonSerializer.Deserialize<TransferHeader>(headerJson);
            if (header is null || header.PlainLength != fileBytes.LongLength)
            {
                return;
            }

            var safeName = Path.GetFileName(header.FileName);
            var downloads = _receivingDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "KeyBridge Transfers");
            Directory.CreateDirectory(downloads);
            var destination = UniquePath(downloads, safeName);
            await File.WriteAllBytesAsync(destination, fileBytes, cancellationToken);
            FileReceived?.Invoke(this, new FileReceivedEventArgs(safeName, destination, fileBytes.LongLength));
        }
    }

    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
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

    private sealed class TransferHeader
    {
        public string FileName { get; set; } = string.Empty;
        public long PlainLength { get; set; }
    }
}
