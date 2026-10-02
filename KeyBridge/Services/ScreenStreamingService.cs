using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class ScreenStreamingService : IDisposable
{
    private const int Srccopy = 0x00CC0020;
    private const int SmCxscreen = 0;
    private const int SmCyscreen = 1;
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _viewerCts;
    private AppSettings? _settings;
    private volatile bool _incomingSessionApproved;

    public event EventHandler<ScreenFrameEventArgs>? FrameReceived;
    public event EventHandler<string>? StatusChanged;
    public event Action<bool>? ConnectionChanged;

    public void Start(AppSettings settings)
    {
        StopServer();
        _incomingSessionApproved = false;
        _settings = settings;
        _serverCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, NetworkPorts.Screen);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void SetIncomingSessionApproved(bool approved) => _incomingSessionApproved = approved;

    public void StartViewing(PairedDevice device)
    {
        StopViewing();
        _viewerCts = new CancellationTokenSource();
        _ = Task.Run(() => ViewLoopAsync(device, _viewerCts.Token));
    }

    public void StopViewing()
    {
        _viewerCts?.Cancel();
        _viewerCts?.Dispose();
        _viewerCts = null;
        ConnectionChanged?.Invoke(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => ServeViewerAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"Ekran paylaşımı hatası: {ex.Message}"); }
        }
    }

    private async Task ServeViewerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                if (!_incomingSessionApproved)
                {
                    return;
                }

                var authPayload = await NetworkProtocol.ReadFrameAsync(stream, 4096, cancellationToken);
                var token = _settings?.PairedDevice?.PairingToken;
                if (authPayload is null || string.IsNullOrWhiteSpace(token) ||
                    !SecureMessage.TryUnprotectBytes(authPayload, token, out var authBytes) ||
                    !"screen-view"u8.SequenceEqual(authBytes))
                {
                    return;
                }

                var request = new byte[1];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(request, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    var frame = CaptureDesktopJpeg();
                    var protectedFrame = SecureMessage.ProtectBytes(frame, token);
                    await NetworkProtocol.WriteFrameAsync(stream, protectedFrame, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Ekran gönderimi hatası: {ex.Message}");
        }
    }

    private async Task ViewLoopAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Parse(device.IpAddress), NetworkPorts.Screen, cancellationToken);
                await using var stream = client.GetStream();
                await NetworkProtocol.WriteFrameAsync(stream, SecureMessage.ProtectBytes("screen-view"u8.ToArray(), device.PairingToken), cancellationToken);
                var firstFrameReceived = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    var protectedFrame = await NetworkProtocol.ReadFrameAsync(stream, 12 * 1024 * 1024, cancellationToken);
                    stopwatch.Stop();
                    if (protectedFrame is null)
                    {
                        break;
                    }

                    if (!SecureMessage.TryUnprotectBytes(protectedFrame, device.PairingToken, out var frame))
                    {
                        throw new InvalidDataException("Canlı ekran karesi doğrulanamadı.");
                    }

                    if (!firstFrameReceived)
                    {
                        firstFrameReceived = true;
                        ConnectionChanged?.Invoke(true);
                        StatusChanged?.Invoke(this, "Canlı ekran bağlantısı kuruldu.");
                    }

                    FrameReceived?.Invoke(this, new ScreenFrameEventArgs(frame, stopwatch.ElapsedMilliseconds));
                    await Task.Delay(140, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    ConnectionChanged?.Invoke(false);
                    StatusChanged?.Invoke(this, "Canlı ekran yeniden bağlanıyor...");
                    await Task.Delay(1500, cancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                ConnectionChanged?.Invoke(false);
                StatusChanged?.Invoke(this, "Canlı ekran yeniden bağlanıyor...");
                try { await Task.Delay(1500, cancellationToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static byte[] CaptureDesktopJpeg()
    {
        var width = GetSystemMetrics(SmCxscreen);
        var height = GetSystemMetrics(SmCyscreen);
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var oldObject = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, Srccopy))
            {
                throw new InvalidOperationException("Ekran görüntüsü alınamadı.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        finally
        {
            SelectObject(memoryDc, oldObject);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void StopServer()
    {
        _serverCts?.Cancel();
        _listener?.Stop();
        _serverCts?.Dispose();
        _serverCts = null;
        _listener = null;
    }

    public void Dispose()
    {
        StopViewing();
        StopServer();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
}
