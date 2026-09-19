using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class PairingService : IDisposable
{
    private const string RequestType = "keybridge.pair.request.v1";
    private const string ResponseType = "keybridge.pair.response.v1";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.72.40.78");

    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpListener? _listener;
    private UdpClient? _udpListener;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;
    private PendingPairing? _pendingPairing;

    public PairingService(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public event EventHandler<PairingCompletedEventArgs>? PairingCompleted;

    public void Start(AppSettings settings)
    {
        Stop();

        _settings = settings;
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Any, NetworkPorts.Pairing);
        _listener.Start();

        _udpListener = new UdpClient(AddressFamily.InterNetwork);
        _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkPorts.Pairing));
        JoinMulticastGroups(_udpListener);

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => UdpListenAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _udpListener?.Dispose();
        _cts?.Dispose();

        _cts = null;
        _listener = null;
        _udpListener = null;
    }

    public string CreatePairingCode(PeerDevice? targetDevice)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pendingPairing = new PendingPairing
        {
            TargetDeviceId = targetDevice?.DeviceId ?? "",
            Code = code,
            PairingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5)
        };

        return code;
    }

    public async Task<(bool Success, string Message)> PairWithCodeAsync(PeerDevice? targetDevice, string code)
    {
        if (_settings is null)
        {
            return (false, "Uygulama ayarlari henuz yuklenmedi.");
        }

        if (targetDevice is not null)
        {
            var tcpResult = await PairWithCodeOverTcpAsync(targetDevice, code);
            if (tcpResult.Success)
            {
                return tcpResult;
            }
        }

        return await PairWithCodeOverUdpAsync(targetDevice, code);
    }

    private async Task<(bool Success, string Message)> PairWithCodeOverTcpAsync(PeerDevice targetDevice, string code)
    {
        if (_settings is null)
        {
            return (false, "Uygulama ayarlari henuz yuklenmedi.");
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(targetDevice.IpAddress), targetDevice.PairingPort);

            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            using var reader = new StreamReader(stream);

            var request = CreatePairingRequest(code, targetDevice.DeviceId);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            var responseLine = await reader.ReadLineAsync();
            var response = string.IsNullOrWhiteSpace(responseLine)
                ? null
                : JsonSerializer.Deserialize<PairingResponse>(responseLine);

            if (response?.Type != ResponseType || !response.Accepted)
            {
                return (false, response?.Message ?? "Eşleşme reddedildi.");
            }

            var pairedDevice = new PairedDevice
            {
                DeviceId = response.TargetDeviceId,
                DeviceName = response.TargetDeviceName,
                IpAddress = targetDevice.IpAddress,
                KeyboardPort = response.TargetKeyboardPort,
                PairingToken = response.PairingToken
            };

            await SavePairingAsync(pairedDevice);
            return (true, "Eşleşme tamamlandı.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or FormatException)
        {
            return (false, $"TCP eşleşme başarısız: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> PairWithCodeOverUdpAsync(PeerDevice? targetDevice, string code)
    {
        if (_settings is null)
        {
            return (false, "Uygulama ayarlari henuz yuklenmedi.");
        }

        using var client = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var request = CreatePairingRequest(code, targetDevice?.DeviceId ?? "");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        var endpoints = NetworkBroadcast.GetIPv4BroadcastAddresses()
            .Append(MulticastAddress)
            .Distinct()
            .Select(address => new IPEndPoint(address, NetworkPorts.Pairing))
            .ToList();

        string? lastReject = null;

        while (!timeout.IsCancellationRequested)
        {
            foreach (var endpoint in endpoints)
            {
                try
                {
                    await client.SendAsync(payload, payload.Length, endpoint);
                }
                catch
                {
                    // Try the remaining endpoints.
                }
            }

            try
            {
                var receiveTask = client.ReceiveAsync(timeout.Token).AsTask();
                var finishedTask = await Task.WhenAny(receiveTask, Task.Delay(1000, timeout.Token));
                if (finishedTask != receiveTask)
                {
                    continue;
                }

                var result = await receiveTask;
                var responseLine = Encoding.UTF8.GetString(result.Buffer);
                var response = JsonSerializer.Deserialize<PairingResponse>(responseLine);

                if (response?.Type != ResponseType)
                {
                    continue;
                }

                if (targetDevice is not null && response.TargetDeviceId != targetDevice.DeviceId)
                {
                    continue;
                }

                if (!response.Accepted)
                {
                    lastReject = response.Message;
                    continue;
                }

                var pairedDevice = new PairedDevice
                {
                    DeviceId = response.TargetDeviceId,
                    DeviceName = response.TargetDeviceName,
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    KeyboardPort = response.TargetKeyboardPort,
                    PairingToken = response.PairingToken
                };

                await SavePairingAsync(pairedDevice);
                return (true, "Eşleşme tamamlandı.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or JsonException)
            {
                lastReject = ex.Message;
            }
        }

        return (false, lastReject ?? "Kod bulunamadı. İki cihazda da uygulama açık ve aynı ağda olmalı.");
    }

    private PairingRequest CreatePairingRequest(string code, string targetDeviceId)
    {
        return new PairingRequest
        {
            Type = RequestType,
            Code = code.Trim(),
            SourceDeviceId = _settings?.DeviceId ?? "",
            SourceDeviceName = _settings?.DeviceName ?? "",
            SourceRole = _settings?.Role ?? DeviceRole.Primary,
            SourceKeyboardPort = NetworkPorts.Keyboard,
            TargetDeviceId = targetDeviceId
        };
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Keep accepting later pairing attempts.
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return;
        }

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        var request = string.IsNullOrWhiteSpace(requestLine)
            ? null
            : JsonSerializer.Deserialize<PairingRequest>(requestLine);

        var remoteIp = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "";
        var response = await ValidatePairingRequestAsync(request, remoteIp);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
    }

    private async Task UdpListenAsync(CancellationToken cancellationToken)
    {
        if (_udpListener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpListener.ReceiveAsync(cancellationToken);
                var requestLine = Encoding.UTF8.GetString(result.Buffer);
                var request = JsonSerializer.Deserialize<PairingRequest>(requestLine);

                if (request?.SourceDeviceId == _settings?.DeviceId)
                {
                    continue;
                }

                var response = await ValidatePairingRequestAsync(request, result.RemoteEndPoint.Address.ToString());
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                await _udpListener.SendAsync(payload, payload.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Keep listening for later pairing attempts.
            }
        }
    }

    private async Task<PairingResponse> ValidatePairingRequestAsync(PairingRequest? request, string remoteIp)
    {
        if (_settings is null)
        {
            return Reject("Uygulama ayarlari hazir degil.");
        }

        await _gate.WaitAsync();
        try
        {
            if (request?.Type != RequestType)
            {
                return Reject("Geçersiz eşleşme isteği.");
            }

            if (!string.IsNullOrWhiteSpace(request.TargetDeviceId) &&
                request.TargetDeviceId != _settings.DeviceId)
            {
                return Reject("Kod bu bilgisayar için üretilmedi.");
            }

            if (_pendingPairing is null || DateTime.UtcNow > _pendingPairing.ExpiresUtc)
            {
                return Reject("Aktif eşleşme kodu yok veya kodun süresi doldu.");
            }

            if (!string.IsNullOrWhiteSpace(_pendingPairing.TargetDeviceId) &&
                _pendingPairing.TargetDeviceId != request.SourceDeviceId)
            {
                return Reject("Eşleşme kodu bu cihaz için üretilmedi.");
            }

            if (_pendingPairing.Code != request.Code.Trim())
            {
                return Reject("Eşleşme kodu hatalı.");
            }

            var pairedDevice = new PairedDevice
            {
                DeviceId = request.SourceDeviceId,
                DeviceName = request.SourceDeviceName,
                IpAddress = remoteIp,
                KeyboardPort = request.SourceKeyboardPort,
                PairingToken = _pendingPairing.PairingToken
            };

            var response = new PairingResponse
            {
                Type = ResponseType,
                Accepted = true,
                Message = "Eşleşme tamamlandı.",
                PairingToken = _pendingPairing.PairingToken,
                TargetDeviceId = _settings.DeviceId,
                TargetDeviceName = _settings.DeviceName,
                TargetKeyboardPort = NetworkPorts.Keyboard
            };

            _pendingPairing = null;
            await SavePairingAsync(pairedDevice);
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private PairingResponse Reject(string message)
    {
        return new PairingResponse
        {
            Type = ResponseType,
            Accepted = false,
            Message = message
        };
    }

    private async Task SavePairingAsync(PairedDevice pairedDevice)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.PairedDevice = pairedDevice;
        await _settingsStore.SaveAsync(_settings);
        PairingCompleted?.Invoke(this, new PairingCompletedEventArgs(pairedDevice));
    }

    private static void JoinMulticastGroups(UdpClient listener)
    {
        foreach (var localAddress in NetworkBroadcast.GetLocalIPv4Addresses())
        {
            try
            {
                listener.JoinMulticastGroup(MulticastAddress, localAddress);
            }
            catch
            {
                // Broadcast is still available when multicast is rejected by an adapter.
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }

    private sealed class PendingPairing
    {
        public string TargetDeviceId { get; init; } = "";

        public string Code { get; init; } = "";

        public string PairingToken { get; init; } = "";

        public DateTime ExpiresUtc { get; init; }
    }

    private sealed class PairingRequest
    {
        public string Type { get; set; } = RequestType;

        public string Code { get; set; } = "";

        public string SourceDeviceId { get; set; } = "";

        public string SourceDeviceName { get; set; } = "";

        public DeviceRole SourceRole { get; set; }

        public int SourceKeyboardPort { get; set; }

        public string TargetDeviceId { get; set; } = "";
    }

    private sealed class PairingResponse
    {
        public string Type { get; set; } = ResponseType;

        public bool Accepted { get; set; }

        public string Message { get; set; } = "";

        public string PairingToken { get; set; } = "";

        public string TargetDeviceId { get; set; } = "";

        public string TargetDeviceName { get; set; } = "";

        public int TargetKeyboardPort { get; set; }
    }
}
