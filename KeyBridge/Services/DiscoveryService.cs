using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KeyBridge.Models;

namespace KeyBridge.Services;

public sealed class DiscoveryService : IDisposable
{
    private const string AnnouncementType = "keybridge.announce.v1";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.72.40.77");
    private readonly ConcurrentDictionary<string, PeerDevice> _peers = new();
    private UdpClient? _listener;
    private UdpClient? _broadcaster;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;

    public event EventHandler<PeerDevice>? PeerSeen;

    public IReadOnlyList<PeerDevice> KnownPeers =>
        _peers.Values
            .Where(peer => DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15))
            .OrderBy(peer => peer.DeviceName)
            .ToList();

    public void Start(AppSettings settings)
    {
        Stop();

        _settings = settings;
        _cts = new CancellationTokenSource();

        _listener = new UdpClient(AddressFamily.InterNetwork);
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkPorts.Discovery));
        JoinMulticastGroups(_listener);

        _broadcaster = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        _ = Task.Run(() => ListenAsync(_cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _broadcaster?.Dispose();
        _cts?.Dispose();

        _cts = null;
        _listener = null;
        _broadcaster = null;
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastOnceAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    private async Task BroadcastOnceAsync(CancellationToken cancellationToken)
    {
        if (_settings is null || _broadcaster is null)
        {
            return;
        }

        var announcement = new DiscoveryAnnouncement
        {
            Type = AnnouncementType,
            DeviceId = _settings.DeviceId,
            DeviceName = _settings.DeviceName,
            Role = _settings.Role,
            PairingPort = NetworkPorts.Pairing,
            KeyboardPort = NetworkPorts.Keyboard
        };

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement));
        var endpoints = NetworkBroadcast.GetIPv4BroadcastAddresses()
            .Append(MulticastAddress)
            .Distinct()
            .Select(address => new IPEndPoint(address, NetworkPorts.Discovery));

        foreach (var endpoint in endpoints)
        {
            try
            {
                await _broadcaster.SendAsync(payload, endpoint, cancellationToken);
            }
            catch
            {
                // Some adapters reject subnet broadcast or multicast. Other endpoints may still work.
            }
        }
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
                // Multicast is a best-effort fallback for networks where broadcast is filtered.
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(cancellationToken);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(json);

                if (announcement?.Type != AnnouncementType ||
                    string.IsNullOrWhiteSpace(announcement.DeviceId) ||
                    announcement.DeviceId == _settings?.DeviceId)
                {
                    continue;
                }

                var peer = new PeerDevice
                {
                    DeviceId = announcement.DeviceId,
                    DeviceName = announcement.DeviceName,
                    Role = announcement.Role,
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    PairingPort = announcement.PairingPort,
                    KeyboardPort = announcement.KeyboardPort,
                    LastSeenUtc = DateTime.UtcNow
                };

                _peers.AddOrUpdate(peer.DeviceId, peer, (_, existing) =>
                {
                    existing.DeviceName = peer.DeviceName;
                    existing.Role = peer.Role;
                    existing.IpAddress = peer.IpAddress;
                    existing.PairingPort = peer.PairingPort;
                    existing.KeyboardPort = peer.KeyboardPort;
                    existing.LastSeenUtc = peer.LastSeenUtc;
                    return existing;
                });

                PeerSeen?.Invoke(this, peer);
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
                // Ignore malformed packets from the local network.
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private sealed class DiscoveryAnnouncement
    {
        public string Type { get; set; } = AnnouncementType;

        public string DeviceId { get; set; } = "";

        public string DeviceName { get; set; } = "";

        public DeviceRole Role { get; set; }

        public int PairingPort { get; set; }

        public int KeyboardPort { get; set; }
    }
}
