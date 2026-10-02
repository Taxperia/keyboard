using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KeyBridge.Models;
using KeyBridge.Services.WindowsInput;

namespace KeyBridge.Services;

public sealed class KeyboardBridgeService : IDisposable
{
    private const int VkK = 0x4B;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;

    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private readonly LowLevelMouseHook _mouseHook = new();
    private readonly KeyboardInjector _keyboardInjector = new();
    private readonly KeyboardTextTranslator _textTranslator = new();
    private readonly HashSet<int> _textPacketKeys = [];
    private readonly object _settingsLock = new();
    private readonly object _outgoingInputConnectionLock = new();
    private readonly Channel<OutgoingInputPacket> _reliableInputQueue =
        Channel.CreateBounded<OutgoingInputPacket>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Channel<bool> _inputWakeup =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private UdpClient? _listener;
    private UdpClient? _sender;
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;
    private long _sentPackets;
    private long _receivedPackets;
    private volatile bool _isRemoteControlActive;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _kDown;
    private bool _releaseLocalHotkeyChord;
    private bool _hasLastMousePoint;
    private volatile bool _incomingSessionApproved;
    private Func<int, int, bool>? _localMousePassthrough;
    private int _lastMouseX;
    private int _lastMouseY;
    private OutgoingInputPacket? _pendingMouseMove;
    private int _inputQueueOverflowReported;
    private TcpClient? _outgoingInputClient;
    private NetworkStream? _outgoingInputStream;
    private string _outgoingInputConnectionKey = string.Empty;

    public KeyboardBridgeService()
    {
        _keyboardHook.KeyboardEvent += OnKeyboardEvent;
        _mouseHook.MouseEvent += OnMouseEvent;
    }

    public event EventHandler<KeyboardBridgeStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PeerDisconnectedEventArgs>? PeerDisconnected;

    public event EventHandler<PeerDisconnectedEventArgs>? PeerSessionEnded;

    public bool IsRemoteControlActive => _isRemoteControlActive;

    public void SetIncomingSessionApproved(bool approved)
    {
        _incomingSessionApproved = approved;
        if (!approved)
        {
            _keyboardInjector.ReleaseSafetyKeys();
        }
    }

    public void SetLocalMousePassthrough(Func<int, int, bool>? predicate) =>
        _localMousePassthrough = predicate;

    public void Start(AppSettings settings)
    {
        StopNetworkOnly();
        _incomingSessionApproved = false;

        UpdateSettings(settings);
        _keyboardInjector.ReleaseSafetyKeys();
        _cts = new CancellationTokenSource();

        _listener = new UdpClient(AddressFamily.InterNetwork);
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkPorts.Keyboard));
        _sender = new UdpClient(AddressFamily.InterNetwork);
        _tcpListener = new TcpListener(IPAddress.Any, NetworkPorts.Keyboard);
        _tcpListener.Start();

        _keyboardHook.Start();
        _mouseHook.Start();
        _ = Task.Run(() => ListenForKeyboardPacketsAsync(_cts.Token));
        _ = Task.Run(() => ListenForKeyboardTcpPacketsAsync(_cts.Token));
        _ = Task.Run(() => ProcessOutgoingInputAsync(_cts.Token));

        RaiseStatus("Klavye servisi hazır.");
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (_settingsLock)
        {
            _settings = settings;
        }
    }

    public async Task<bool> ToggleRemoteControlAsync()
    {
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            _isRemoteControlActive = false;
            RaiseStatus("Önce bir cihaz eşleştirin.");
            return false;
        }

        if (_isRemoteControlActive)
        {
            SetRemoteControl(false);
            return true;
        }

        RaiseStatus($"{settings.PairedDevice.DeviceName} kontrol izni doğrulanıyor...");
        if (!await ProbeRemoteControlAsync(settings, settings.PairedDevice))
        {
            _isRemoteControlActive = false;
            RaiseStatus("Uzak cihaz giriş izni vermedi veya bağlantıya ulaşılamıyor.");
            return false;
        }

        _isRemoteControlActive = true;
        Interlocked.Exchange(ref _inputQueueOverflowReported, 0);
        _releaseLocalHotkeyChord = _ctrlDown || _altDown || _kDown;
        ResetMouseState();
        RaiseStatus($"{settings.PairedDevice.DeviceName} kontrol ediliyor.");
        return true;
    }

    public void SetRemoteControl(bool isActive)
    {
        if (_isRemoteControlActive && !isActive)
        {
            var settings = GetSettingsSnapshot();
            if (settings?.PairedDevice is not null)
            {
                _ = ReleaseRemoteSafetyKeysAsync(settings);
            }
        }

        _isRemoteControlActive = isActive;
        if (!isActive)
        {
            CloseOutgoingInputConnection();
            _keyboardInjector.ReleaseSafetyKeys();
            _ctrlDown = false;
            _altDown = false;
            _shiftDown = false;
            _kDown = false;
            _releaseLocalHotkeyChord = false;
            _textPacketKeys.Clear();
            ResetMouseState();
        }

        RaiseStatus(isActive ? "Uzak bilgisayar kontrol modu açık." : "Klavye bu bilgisayarda.");
    }

    public Task<bool> EndSessionAsync() => SendConnectionActionAsync("end-session");

    public Task<bool> ForgetPeerAsync() => SendConnectionActionAsync("disconnect");

    private async Task<bool> SendConnectionActionAsync(string action)
    {
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            return true;
        }

        SetRemoteControl(false);

        var packet = new ConnectionPacket
        {
            SourceDeviceId = settings.DeviceId,
            PairingToken = settings.PairedDevice.PairingToken,
            Action = action
        };

        try
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            return await SendPacketAsync(settings.PairedDevice, payload);
        }
        catch
        {
            return false;
        }
    }

    private void OnKeyboardEvent(object? sender, KeyboardHookEventArgs args)
    {
        UpdateModifierState(args);

        if (!_isRemoteControlActive || args.IsInjected)
        {
            return;
        }

        if (!args.IsKeyDown && _textPacketKeys.Remove(args.VirtualKey))
        {
            args.Handled = true;
            return;
        }

        if (_releaseLocalHotkeyChord)
        {
            if (!args.IsKeyDown && IsHotkeyChordKey(args.VirtualKey))
            {
                if (!_ctrlDown && !_altDown && !_kDown)
                {
                    _releaseLocalHotkeyChord = false;
                }

                return;
            }

            if (!_ctrlDown && !_altDown && !_kDown)
            {
                _releaseLocalHotkeyChord = false;
            }
        }

        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            _isRemoteControlActive = false;
            RaiseStatus("Eşleşmiş cihaz bulunamadı.");
            return;
        }

        if (args.IsKeyDown && args.VirtualKey == VkK && _ctrlDown && _altDown)
        {
            args.Handled = true;
            _ = ReleaseRemoteSafetyKeysAsync(settings);
            _keyboardInjector.ReleaseSafetyKeys();
            _isRemoteControlActive = false;
            _ctrlDown = false;
            _altDown = false;
            _shiftDown = false;
            _kDown = false;
            _releaseLocalHotkeyChord = false;
            _textPacketKeys.Clear();
            RaiseStatus("Klavye bu bilgisayarda.");
            return;
        }

        args.Handled = true;

        if (_textTranslator.TryTranslate(args, _ctrlDown, _altDown, _shiftDown, out var text))
        {
            _textPacketKeys.Add(args.VirtualKey);
            var textPacket = new KeyboardPacket
            {
                SourceDeviceId = settings.DeviceId,
                PairingToken = settings.PairedDevice.PairingToken,
                VirtualKey = args.VirtualKey,
                ScanCode = args.ScanCode,
                IsKeyDown = true,
                IsExtendedKey = args.IsExtendedKey,
                Text = text
            };

            EnqueueReliableInput(settings.PairedDevice, textPacket);
            return;
        }

        var packet = new KeyboardPacket
        {
            SourceDeviceId = settings.DeviceId,
            PairingToken = settings.PairedDevice.PairingToken,
            VirtualKey = args.VirtualKey,
            ScanCode = args.ScanCode,
            IsKeyDown = args.IsKeyDown,
            IsExtendedKey = args.IsExtendedKey
        };

        EnqueueReliableInput(settings.PairedDevice, packet);
    }

    private void OnMouseEvent(object? sender, MouseHookEventArgs args)
    {
        if (!_isRemoteControlActive || args.IsInjected)
        {
            return;
        }

        var localMousePassthrough = _localMousePassthrough;
        if (localMousePassthrough?.Invoke(args.X, args.Y) == true)
        {
            return;
        }

        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            return;
        }

        var packet = new MousePacket
        {
            SourceDeviceId = settings.DeviceId,
            PairingToken = settings.PairedDevice.PairingToken,
            Action = args.Action,
            WheelDelta = args.WheelDelta
        };

        if (args.Action == "move")
        {
            if (!_hasLastMousePoint)
            {
                _lastMouseX = args.X;
                _lastMouseY = args.Y;
                _hasLastMousePoint = true;
                return;
            }

            packet.DeltaX = args.X - _lastMouseX;
            packet.DeltaY = args.Y - _lastMouseY;
            _lastMouseX = args.X;
            _lastMouseY = args.Y;

            var absolutePoint = ScreenCoordinateMapper.ToAbsoluteVirtualDesktop(args.X, args.Y);
            packet.IsAbsolute = true;
            packet.AbsoluteX = absolutePoint.X;
            packet.AbsoluteY = absolutePoint.Y;

            if (packet.DeltaX == 0 && packet.DeltaY == 0)
            {
                return;
            }
        }
        else
        {
            args.Handled = true;
        }

        EnqueueMouseInput(settings.PairedDevice, packet);
    }

    private void EnqueueMouseInput(PairedDevice targetDevice, MousePacket packet)
    {
        var outgoing = new OutgoingInputPacket(targetDevice, packet);
        if (packet.Action == "move")
        {
            // Mouse hooks can fire hundreds of times per second. Only the newest
            // unsent position matters, so replacing it avoids network/task storms.
            Interlocked.Exchange(ref _pendingMouseMove, outgoing);
            _inputWakeup.Writer.TryWrite(true);
            return;
        }

        // A click must be sent after the latest cursor position so it lands on the
        // same target the user sees.
        var pendingMove = Interlocked.Exchange(ref _pendingMouseMove, null);
        if (pendingMove is not null && !TryEnqueueReliableInput(pendingMove))
        {
            return;
        }

        TryEnqueueReliableInput(outgoing);
    }

    private void EnqueueReliableInput(PairedDevice targetDevice, KeyboardPacket packet) =>
        TryEnqueueReliableInput(new OutgoingInputPacket(targetDevice, packet));

    private bool TryEnqueueReliableInput(OutgoingInputPacket packet)
    {
        if (_reliableInputQueue.Writer.TryWrite(packet))
        {
            _inputWakeup.Writer.TryWrite(true);
            return true;
        }

        if (Interlocked.Exchange(ref _inputQueueOverflowReported, 1) == 0)
        {
            _isRemoteControlActive = false;
            _ = Task.Run(() => RaiseStatus("Giriş kuyruğu doldu; yerel kontrol güvenli biçimde geri alındı."));
        }

        return false;
    }

    private async Task ProcessOutgoingInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _inputWakeup.Reader.ReadAsync(cancellationToken);
                while (_inputWakeup.Reader.TryRead(out _))
                {
                }

                // Reliable key and button events keep their original order.
                for (var processed = 0;
                     processed < 32 && _reliableInputQueue.Reader.TryRead(out var reliablePacket);
                     processed++)
                {
                    await SendOutgoingInputAsync(reliablePacket);
                }

                var mouseMove = Interlocked.Exchange(ref _pendingMouseMove, null);
                if (mouseMove is not null)
                {
                    await SendOutgoingInputAsync(mouseMove);
                }

                if (_reliableInputQueue.Reader.TryPeek(out _) ||
                    Volatile.Read(ref _pendingMouseMove) is not null)
                {
                    _inputWakeup.Writer.TryWrite(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SendOutgoingInputAsync(OutgoingInputPacket outgoing)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outgoing.TargetDevice.IpAddress))
            {
                return;
            }

            var json = JsonSerializer.Serialize(outgoing.Packet, outgoing.Packet.GetType());
            var protectedPayload = Encoding.UTF8.GetBytes(
                SecureMessage.ProtectText(json, outgoing.TargetDevice.PairingToken) + "\n");

            if (!await SendQueuedPacketOverTcpAsync(outgoing.TargetDevice, protectedPayload))
            {
                RaiseStatus("Giriş paketi gönderilemedi; bağlantı yeniden kuruluyor.");
                return;
            }

            Interlocked.Increment(ref _sentPackets);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or FormatException)
        {
            CloseOutgoingInputConnection();
            RaiseStatus("Giriş paketi gönderilemedi; ağ bağlantısını kontrol edin.");
        }
    }

    private async Task<bool> SendQueuedPacketOverTcpAsync(PairedDevice targetDevice, byte[] protectedPayload)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var stream = await GetOrConnectOutgoingInputStreamAsync(targetDevice);
                using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await stream.WriteAsync(protectedPayload, writeTimeout.Token);
                await stream.FlushAsync(writeTimeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                CloseOutgoingInputConnection();
            }
        }

        return false;
    }

    private async Task<NetworkStream> GetOrConnectOutgoingInputStreamAsync(PairedDevice targetDevice)
    {
        var connectionKey = $"{targetDevice.IpAddress}:{targetDevice.KeyboardPort}:{targetDevice.PairingToken}";
        lock (_outgoingInputConnectionLock)
        {
            if (_outgoingInputStream is not null &&
                _outgoingInputClient?.Connected == true &&
                _outgoingInputConnectionKey == connectionKey)
            {
                return _outgoingInputStream;
            }
        }

        CloseOutgoingInputConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(
                IPAddress.Parse(targetDevice.IpAddress),
                targetDevice.KeyboardPort,
                timeout.Token);
            var stream = client.GetStream();

            lock (_outgoingInputConnectionLock)
            {
                _outgoingInputClient = client;
                _outgoingInputStream = stream;
                _outgoingInputConnectionKey = connectionKey;
            }

            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void CloseOutgoingInputConnection()
    {
        lock (_outgoingInputConnectionLock)
        {
            _outgoingInputStream?.Dispose();
            _outgoingInputClient?.Dispose();
            _outgoingInputStream = null;
            _outgoingInputClient = null;
            _outgoingInputConnectionKey = string.Empty;
        }
    }

    private void UpdateModifierState(KeyboardHookEventArgs args)
    {
        if (args.VirtualKey is VkControl or VkLeftControl or VkRightControl)
        {
            _ctrlDown = args.IsKeyDown;
        }
        else if (args.VirtualKey is VkMenu or VkLeftMenu or VkRightMenu)
        {
            _altDown = args.IsKeyDown;
        }
        else if (args.VirtualKey is VkShift or VkLeftShift or VkRightShift)
        {
            _shiftDown = args.IsKeyDown;
        }
        else if (args.VirtualKey == VkK)
        {
            _kDown = args.IsKeyDown;
        }
    }

    private static bool IsHotkeyChordKey(int virtualKey)
    {
        return virtualKey is VkK or VkControl or VkLeftControl or VkRightControl or VkMenu or VkLeftMenu or VkRightMenu;
    }

    private async Task ReleaseRemoteSafetyKeysAsync(AppSettings settings)
    {
        if (settings.PairedDevice is null)
        {
            return;
        }

        var keys = new[]
        {
            0x10, VkControl, VkMenu, 0x5B, 0x5C,
            0xA0, 0xA1, VkLeftControl, VkRightControl, VkLeftMenu, VkRightMenu
        };

        foreach (var key in keys)
        {
            await SendKeyboardPacketAsync(settings.PairedDevice, CreateKeyUpPacket(settings, key));
        }
    }

    private static KeyboardPacket CreateKeyUpPacket(AppSettings settings, int virtualKey)
    {
        return new KeyboardPacket
        {
            SourceDeviceId = settings.DeviceId,
            PairingToken = settings.PairedDevice?.PairingToken ?? "",
            VirtualKey = virtualKey,
            ScanCode = 0,
            IsKeyDown = false,
            IsExtendedKey = false
        };
    }

    private async Task SendKeyboardPacketAsync(PairedDevice targetDevice, KeyboardPacket packet)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetDevice.IpAddress))
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            await SendPacketAsync(targetDevice, payload);

            Interlocked.Increment(ref _sentPackets);
            if (_sentPackets % 8 == 0)
            {
                RaiseStatus($"{_sentPackets} klavye paketi gönderildi.");
            }
        }
        catch
        {
            RaiseStatus("Klavye paketi gönderilemedi. Ağ bağlantısını kontrol edin.");
        }
    }

    private async Task SendMousePacketAsync(PairedDevice targetDevice, MousePacket packet)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetDevice.IpAddress))
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            await SendPacketAsync(targetDevice, payload, preferUdp: packet.Action == "move");

            Interlocked.Increment(ref _sentPackets);
        }
        catch
        {
            RaiseStatus("Fare paketi gönderilemedi. Ağ bağlantısını kontrol edin.");
        }
    }

    private async Task<bool> SendPacketAsync(PairedDevice targetDevice, byte[] payload, bool preferUdp = false)
    {
        var protectedPayload = Encoding.UTF8.GetBytes(
            SecureMessage.ProtectText(Encoding.UTF8.GetString(payload), targetDevice.PairingToken));

        if (preferUdp && _sender is not null)
        {
            try
            {
                await _sender.SendAsync(protectedPayload, protectedPayload.Length, targetDevice.IpAddress, targetDevice.KeyboardPort);
                return true;
            }
            catch (SocketException)
            {
                // Fall back to TCP when UDP is unavailable on this adapter.
            }
        }

        if (await SendKeyboardPacketOverTcpAsync(targetDevice, protectedPayload))
        {
            return true;
        }

        if (_sender is null)
        {
            return false;
        }

        await _sender.SendAsync(protectedPayload, protectedPayload.Length, targetDevice.IpAddress, targetDevice.KeyboardPort);
        return true;
    }

    private static async Task<bool> ProbeRemoteControlAsync(AppSettings settings, PairedDevice targetDevice)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(targetDevice.IpAddress), targetDevice.KeyboardPort, timeout.Token);
            await using var stream = client.GetStream();

            var packet = new ConnectionPacket
            {
                SourceDeviceId = settings.DeviceId,
                PairingToken = targetDevice.PairingToken,
                Action = "probe"
            };
            var plaintext = JsonSerializer.Serialize(packet);
            var request = SecureMessage.ProtectText(plaintext, targetDevice.PairingToken) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = await reader.ReadLineAsync(timeout.Token);
            return !string.IsNullOrWhiteSpace(response) &&
                   SecureMessage.TryUnprotectText(response, targetDevice.PairingToken, out var acknowledgement) &&
                   acknowledgement == "control-ready";
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or FormatException)
        {
            return false;
        }
    }

    private static async Task<bool> SendKeyboardPacketOverTcpAsync(PairedDevice targetDevice, byte[] payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(targetDevice.IpAddress), targetDevice.KeyboardPort, timeout.Token);

            await using var stream = client.GetStream();
            await stream.WriteAsync(payload, timeout.Token);
            await stream.WriteAsync("\n"u8.ToArray(), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or FormatException)
        {
            return false;
        }
    }

    private async Task ListenForKeyboardPacketsAsync(CancellationToken cancellationToken)
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
                HandleIncomingInputPacket(json);
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
                RaiseStatus("Gelen klavye paketi işlenemedi.");
            }
        }
    }

    private async Task ListenForKeyboardTcpPacketsAsync(CancellationToken cancellationToken)
    {
        if (_tcpListener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleKeyboardTcpClientAsync(client, cancellationToken), cancellationToken);
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
                RaiseStatus("TCP klavye paketi işlenemedi.");
            }
        }
    }

    private async Task HandleKeyboardTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var json = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var response = HandleIncomingInputPacket(json);
                if (!string.IsNullOrEmpty(response))
                {
                    var settings = GetSettingsSnapshot();
                    var token = settings?.PairedDevice?.PairingToken;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        var protectedResponse = SecureMessage.ProtectText(response, token) + "\n";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(protectedResponse), cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                }
            }
        }
    }

    private string? HandleIncomingInputPacket(string json)
    {
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            return null;
        }

        if (!SecureMessage.TryUnprotectText(json, settings.PairedDevice.PairingToken, out var plaintext))
        {
            RaiseStatus("Yetkisiz giriş paketi reddedildi.");
            return null;
        }

        using var document = JsonDocument.Parse(plaintext);
        if (!document.RootElement.TryGetProperty("Type", out var typeProperty))
        {
            return null;
        }

        var type = typeProperty.GetString();

        if (type == "keyboard")
        {
            if (!_incomingSessionApproved)
            {
                return null;
            }

            if (!settings.EnableKeyboardControl)
            {
                return null;
            }

            var packet = JsonSerializer.Deserialize<KeyboardPacket>(plaintext);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken)
            {
                return null;
            }

            CountReceivedOrReportFailure(packet);
            return null;
        }

        if (type == "mouse")
        {
            if (!_incomingSessionApproved)
            {
                return null;
            }

            if (!settings.EnableMouseControl)
            {
                return null;
            }

            var packet = JsonSerializer.Deserialize<MousePacket>(plaintext);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken)
            {
                return null;
            }

            CountMouseReceivedOrReportFailure(packet);
            return null;
        }

        if (type == "connection")
        {
            var packet = JsonSerializer.Deserialize<ConnectionPacket>(plaintext);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken)
            {
                return null;
            }

            if (packet.Action == "probe")
            {
                return _incomingSessionApproved &&
                       (settings.EnableKeyboardControl || settings.EnableMouseControl)
                    ? "control-ready"
                    : null;
            }

            if (packet.Action == "end-session")
            {
                var sessionDeviceName = settings.PairedDevice.DeviceName;
                SetRemoteControl(false);
                SetIncomingSessionApproved(false);
                PeerSessionEnded?.Invoke(this, new PeerDisconnectedEventArgs(sessionDeviceName));
                return null;
            }

            if (packet.Action != "disconnect")
            {
                return null;
            }

            var deviceName = settings.PairedDevice.DeviceName;
            SetRemoteControl(false);
            SetIncomingSessionApproved(false);
            PeerDisconnected?.Invoke(this, new PeerDisconnectedEventArgs(deviceName));
        }

        return null;
    }

    private void CountReceivedOrReportFailure(KeyboardPacket? packet = null)
    {
        if (packet is not null && !_keyboardInjector.Inject(packet))
        {
            RaiseStatus($"Windows girdiyi reddetti. {_keyboardInjector.LastError}");
            return;
        }

        Interlocked.Increment(ref _receivedPackets);
        if (_receivedPackets % 8 == 0)
        {
            RaiseStatus($"{_receivedPackets} klavye paketi alındı.");
        }
    }

    private void CountMouseReceivedOrReportFailure(MousePacket packet)
    {
        if (!_keyboardInjector.InjectMouse(packet))
        {
            RaiseStatus($"Windows mouse girdisini reddetti. {_keyboardInjector.LastError}");
            return;
        }

        Interlocked.Increment(ref _receivedPackets);
    }

    private AppSettings? GetSettingsSnapshot()
    {
        lock (_settingsLock)
        {
            return _settings;
        }
    }

    private void RaiseStatus(string message)
    {
        StatusChanged?.Invoke(
            this,
            new KeyboardBridgeStatusChangedEventArgs(
                _isRemoteControlActive,
                message,
                Interlocked.Read(ref _sentPackets),
                Interlocked.Read(ref _receivedPackets)));
    }

    private void StopNetworkOnly()
    {
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is not null)
        {
            try
            {
                ReleaseRemoteSafetyKeysAsync(settings).GetAwaiter().GetResult();
            }
            catch
            {
                // The network socket may already be closing.
            }
        }

        _keyboardInjector.ReleaseSafetyKeys();
        _isRemoteControlActive = false;
        _ctrlDown = false;
        _altDown = false;
        _shiftDown = false;
        _kDown = false;
        _releaseLocalHotkeyChord = false;
        _textPacketKeys.Clear();
        ResetMouseState();
        Interlocked.Exchange(ref _pendingMouseMove, null);
        while (_reliableInputQueue.Reader.TryRead(out _))
        {
        }
        while (_inputWakeup.Reader.TryRead(out _))
        {
        }

        _cts?.Cancel();
        CloseOutgoingInputConnection();
        _tcpListener?.Stop();
        _listener?.Dispose();
        _sender?.Dispose();
        _cts?.Dispose();

        _cts = null;
        _tcpListener = null;
        _listener = null;
        _sender = null;
    }

    private void ResetMouseState()
    {
        _hasLastMousePoint = false;
        _lastMouseX = 0;
        _lastMouseY = 0;
    }

    public void Dispose()
    {
        StopNetworkOnly();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
    }

    private sealed record OutgoingInputPacket(PairedDevice TargetDevice, object Packet);
}
