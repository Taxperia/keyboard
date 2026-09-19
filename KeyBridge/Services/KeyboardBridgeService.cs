using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    private UdpClient? _listener;
    private UdpClient? _sender;
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private AppSettings? _settings;
    private long _sentPackets;
    private long _receivedPackets;
    private bool _isRemoteControlActive;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _kDown;
    private bool _releaseLocalHotkeyChord;
    private bool _hasLastMousePoint;
    private int _lastMouseX;
    private int _lastMouseY;

    public KeyboardBridgeService()
    {
        _keyboardHook.KeyboardEvent += OnKeyboardEvent;
        _mouseHook.MouseEvent += OnMouseEvent;
    }

    public event EventHandler<KeyboardBridgeStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PeerDisconnectedEventArgs>? PeerDisconnected;

    public bool IsRemoteControlActive => _isRemoteControlActive;

    public void Start(AppSettings settings)
    {
        StopNetworkOnly();

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

        RaiseStatus("Klavye servisi hazır.");
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (_settingsLock)
        {
            _settings = settings;
        }
    }

    public void ToggleRemoteControl()
    {
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            _isRemoteControlActive = false;
            RaiseStatus("Önce bir cihaz eşleştirin.");
            return;
        }

        if (!_isRemoteControlActive && !settings.EnableKeyboardControl && !settings.EnableMouseControl)
        {
            RaiseStatus("Klavye veya fare kontrolünü etkinleştirin.");
            return;
        }

        _isRemoteControlActive = !_isRemoteControlActive;
        if (!_isRemoteControlActive)
        {
            _ = ReleaseRemoteSafetyKeysAsync(settings);
            _keyboardInjector.ReleaseSafetyKeys();
            _ctrlDown = false;
            _altDown = false;
            _shiftDown = false;
            _kDown = false;
            _releaseLocalHotkeyChord = false;
            _textPacketKeys.Clear();
            ResetMouseState();
        }
        else
        {
            _releaseLocalHotkeyChord = _ctrlDown || _altDown || _kDown;
        }

        RaiseStatus(_isRemoteControlActive
            ? $"{settings.PairedDevice.DeviceName} kontrol ediliyor."
            : "Klavye bu bilgisayarda.");
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

    public async Task<bool> DisconnectPeerAsync()
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
            Action = "disconnect"
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

        if (!settings.EnableKeyboardControl)
        {
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

            _ = SendKeyboardPacketAsync(settings.PairedDevice, textPacket);
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

        _ = SendKeyboardPacketAsync(settings.PairedDevice, packet);
    }

    private void OnMouseEvent(object? sender, MouseHookEventArgs args)
    {
        if (!_isRemoteControlActive || args.IsInjected)
        {
            return;
        }

        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null || !settings.EnableMouseControl)
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

        _ = SendMousePacketAsync(settings.PairedDevice, packet);
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
            await SendPacketAsync(targetDevice, payload);

            Interlocked.Increment(ref _sentPackets);
        }
        catch
        {
            RaiseStatus("Fare paketi gönderilemedi. Ağ bağlantısını kontrol edin.");
        }
    }

    private async Task<bool> SendPacketAsync(PairedDevice targetDevice, byte[] payload)
    {
        if (await SendKeyboardPacketOverTcpAsync(targetDevice, payload))
        {
            return true;
        }

        if (_sender is null)
        {
            return false;
        }

        await _sender.SendAsync(payload, payload.Length, targetDevice.IpAddress, targetDevice.KeyboardPort);
        return true;
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
                using var client = await _tcpListener.AcceptTcpClientAsync(cancellationToken);
                await HandleKeyboardTcpClientAsync(client, cancellationToken);
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
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        HandleIncomingInputPacket(json);
    }

    private void HandleIncomingInputPacket(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();
        var settings = GetSettingsSnapshot();
        if (settings?.PairedDevice is null)
        {
            return;
        }

        if (type == "keyboard")
        {
            var packet = JsonSerializer.Deserialize<KeyboardPacket>(json);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken)
            {
                return;
            }

            CountReceivedOrReportFailure(packet);
            return;
        }

        if (type == "mouse")
        {
            var packet = JsonSerializer.Deserialize<MousePacket>(json);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken)
            {
                return;
            }

            CountMouseReceivedOrReportFailure(packet);
            return;
        }

        if (type == "connection")
        {
            var packet = JsonSerializer.Deserialize<ConnectionPacket>(json);
            if (packet?.SourceDeviceId != settings.PairedDevice.DeviceId ||
                packet.PairingToken != settings.PairedDevice.PairingToken ||
                packet.Action != "disconnect")
            {
                return;
            }

            var deviceName = settings.PairedDevice.DeviceName;
            SetRemoteControl(false);
            PeerDisconnected?.Invoke(this, new PeerDisconnectedEventArgs(deviceName));
        }
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

        _cts?.Cancel();
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
}
