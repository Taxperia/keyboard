using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyBridge.Models;
using KeyBridge.Services;
using KeyBridge.Services.WindowsInput;
using Microsoft.Win32;

namespace KeyBridge;

public partial class MainWindow : Window
{
    private const string StartupRegistryName = "KeyBridge";

    private readonly SettingsStore _settingsStore = new();
    private readonly DiscoveryService _discoveryService = new();
    private readonly PairingService _pairingService;
    private readonly KeyboardBridgeService _keyboardBridgeService = new();
    private readonly ScreenStreamingService _screenStreamingService = new();
    private readonly FileTransferService _fileTransferService = new();
    private readonly ClipboardSyncService _clipboardSyncService = new();
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly Dictionary<string, PeerDevice> _knownPeers = new();
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<string> _notifications = [];

    private AppSettings _settings = new();
    private string _pairingCode = string.Empty;
    private DateTime? _sessionStartedUtc;
    private bool _syncingSettingsUi;
    private string? _viewingDeviceId;
    private string _lastClipboardText = string.Empty;
    private bool _applyingRemoteClipboard;
    private bool _accessPasswordVisible;
    private bool _screenConnected;
    private bool _outgoingSessionApproved;
    private bool _incomingSessionApproved;
    private bool _connectionRequestPending;
    private string? _selectedPairingDeviceId;
    private bool _isWorkAreaMaximized;
    private DateTime _lastDeviceRefreshUtc;
    private Rect _restoreBounds;
    private Window? _screenPreviewWindow;
    private Image? _screenPreviewImage;

    public MainWindow()
    {
        _pairingService = new PairingService(_settingsStore);
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        _sessionTimer.Tick += SessionTimer_Tick;
        _clipboardTimer.Tick += ClipboardTimer_Tick;

        _discoveryService.PeerSeen += DiscoveryService_PeerSeen;
        _pairingService.PairingCompleted += PairingService_PairingCompleted;
        _pairingService.ConnectionApprovalRequested += PairingService_ConnectionApprovalRequested;
        _keyboardBridgeService.StatusChanged += KeyboardBridgeService_StatusChanged;
        _keyboardBridgeService.PeerDisconnected += KeyboardBridgeService_PeerDisconnected;
        _keyboardBridgeService.PeerSessionEnded += KeyboardBridgeService_PeerSessionEnded;
        _screenStreamingService.FrameReceived += ScreenStreamingService_FrameReceived;
        _screenStreamingService.ConnectionChanged += ScreenStreamingService_ConnectionChanged;
        _screenStreamingService.StatusChanged += FeatureService_StatusChanged;
        _fileTransferService.FileReceived += FileTransferService_FileReceived;
        _fileTransferService.StatusChanged += FeatureService_StatusChanged;
        _clipboardSyncService.TextReceived += ClipboardSyncService_TextReceived;
        _clipboardSyncService.StatusChanged += FeatureService_StatusChanged;
        _hotkeyService.ToggleRequested += HotkeyService_ToggleRequested;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1480, workArea.Width - 40);
        Height = Math.Min(800, workArea.Height - 40);
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + ((workArea.Height - Height) / 2);

        _settings = await _settingsStore.LoadAsync();

        HeaderDeviceNameText.Text = _settings.DeviceName;
        LocalDeviceNameText.Text = _settings.DeviceName;
        OnboardingDeviceNameTextBox.Text = _settings.DeviceName;
        SyncSettingsUi();
        GeneratePairingCode();
        RefreshRecentDevices();
        RefreshSession();
        StartBackgroundServices();
        _sessionTimer.Start();
        _clipboardTimer.Start();
        FirstRunModal.Visibility = _settings.OnboardingCompleted ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_hotkeyService.Register(this))
        {
            SetStatus("Ctrl + Alt + K başka bir uygulama tarafından kullanılıyor.");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sessionTimer.Stop();
        _clipboardTimer.Stop();
        _screenPreviewWindow?.Close();
        _hotkeyService.Dispose();
        _clipboardSyncService.Dispose();
        _fileTransferService.Dispose();
        _screenStreamingService.Dispose();
        _keyboardBridgeService.Dispose();
        _pairingService.Dispose();
        _discoveryService.Dispose();
    }

    private void StartBackgroundServices()
    {
        try
        {
            _discoveryService.Start(_settings);
            _pairingService.Start(_settings);
            _keyboardBridgeService.Start(_settings);
            _screenStreamingService.Start(_settings);
            _fileTransferService.Start(_settings);
            _clipboardSyncService.Start(_settings);
            SetStatus("Tüm sistemler çalışıyor");
        }
        catch (Exception ex)
        {
            SetStatus($"Servis başlatılamadı: {ex.Message}");
        }
    }

    private void GeneratePairingCode()
    {
        _pairingCode = _pairingService.CreatePairingCode(null);
        PairingCodeText.Text = FormatPairingCode(_pairingCode);
        _accessPasswordVisible = false;
        AccessPasswordText.Text = "••••••";
        SetStatus("Yeni bağlantı kodu hazır.");
    }

    private static string FormatPairingCode(string code)
    {
        var digits = new string(code.Where(char.IsDigit).ToArray());
        return digits.Length == 6 ? $"{digits[..3]} {digits[3..]}" : code;
    }

    private void GenerateCodeButton_Click(object sender, RoutedEventArgs e) => GeneratePairingCode();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var code = new string(PairingCodeInput.Text.Where(char.IsDigit).ToArray());
        if (code.Length != 6)
        {
            SetStatus("Bağlantı kodu 6 haneli olmalı.");
            PairingCodeInput.Focus();
            return;
        }

        var target = _selectedPairingDeviceId is null ? null :
            _knownPeers.Values.FirstOrDefault(peer =>
                peer.DeviceId == _selectedPairingDeviceId &&
                DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15));
        SetStatus(target is null
            ? "Bağlantı kodu yerel ağda aranıyor..."
            : $"{target.DeviceName} cihazına bağlantı isteği gönderiliyor...");
        var result = await _pairingService.PairWithCodeAsync(target, code);
        SetStatus(result.Message);

        if (result.Success)
        {
            _selectedPairingDeviceId = null;
            _outgoingSessionApproved = true;
            PairingCodeInput.Clear();
            _sessionStartedUtc = DateTime.UtcNow;
            RefreshRecentDevices();
            RefreshSession();
            EnsureScreenViewing();
            if (!_keyboardBridgeService.IsRemoteControlActive)
            {
                await _keyboardBridgeService.ToggleRemoteControlAsync();
            }

            RefreshSession();
        }
    }

    private async void RecentDeviceConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentDeviceItem item })
        {
            return;
        }

        if (_connectionRequestPending)
        {
            return;
        }

        if (item.RequiresRepair)
        {
            _selectedPairingDeviceId = _knownPeers.Values.FirstOrDefault(peer =>
                peer.IpAddress == item.IpAddress &&
                DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15))?.DeviceId;
            SectionPage.Visibility = Visibility.Collapsed;
            SetActiveNavigation(HomeSidebarButton);
            PairingCodeInput.Focus();
            SetStatus($"{item.DeviceName} cihazının güvenlik kimliği değişmiş. Bu cihazı doğrulamak için yalnızca bu kez güncel 6 haneli kodu girin.");
            return;
        }

        var pairedDevice = _settings.PairedDevice;
        if (pairedDevice?.DeviceId == item.DeviceId)
        {
            _connectionRequestPending = true;
            var button = (Button)sender;
            button.IsEnabled = false;
            try
            {
                // The encrypted reconnect response still authenticates the discovered endpoint.
                var discoveredPeer = _knownPeers.Values.FirstOrDefault(peer =>
                    peer.DeviceId == pairedDevice.DeviceId &&
                    DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15));
                if (discoveredPeer is not null &&
                    (pairedDevice.IpAddress != discoveredPeer.IpAddress ||
                     pairedDevice.KeyboardPort != discoveredPeer.KeyboardPort))
                {
                    pairedDevice.IpAddress = discoveredPeer.IpAddress;
                    pairedDevice.KeyboardPort = discoveredPeer.KeyboardPort;
                    await SaveSettingsAsync();
                }

                SetStatus($"{item.DeviceName} bilgisayarında onay bekleniyor...");
                var result = await _pairingService.RequestConnectionApprovalAsync(pairedDevice);
                SetStatus(result.Message);
                if (!result.Success)
                {
                    return;
                }

                _outgoingSessionApproved = true;
                _sessionStartedUtc = DateTime.UtcNow;
                EnsureScreenViewing();
                if (!_keyboardBridgeService.IsRemoteControlActive)
                {
                    await _keyboardBridgeService.ToggleRemoteControlAsync();
                }

                RefreshSession();
            }
            finally
            {
                button.IsEnabled = true;
                _connectionRequestPending = false;
            }

            return;
        }

        _selectedPairingDeviceId = item.DeviceId;
        SectionPage.Visibility = Visibility.Collapsed;
        SetActiveNavigation(HomeSidebarButton);
        PairingCodeInput.Focus();
        SetStatus($"{item.DeviceName} için bağlantı kodunu girin.");
    }

    private async void DisconnectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PairedDevice is null)
        {
            return;
        }

        var deviceName = _settings.PairedDevice.DeviceName;
        var answer = MessageBox.Show(this,
            $"{deviceName} ile etkin oturum kapatılsın mı? Cihaz kayıtlı kalacak ve daha sonra karşı tarafın onayıyla yeniden bağlanabileceksiniz.",
            "Bağlantıyı kes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        DisconnectDeviceButton.IsEnabled = false;
        await _keyboardBridgeService.EndSessionAsync();
        ResetActiveSession();
        RefreshRecentDevices();
        RefreshSession();
        DisconnectDeviceButton.IsEnabled = true;
        SetStatus($"{deviceName} ile oturum kapatıldı. Cihaz kayıtlı tutuluyor.");
    }

    private async Task ForgetPairedDeviceAsync()
    {
        if (_settings.PairedDevice is null)
        {
            return;
        }

        var deviceName = _settings.PairedDevice.DeviceName;
        var answer = MessageBox.Show(
            this,
            $"{deviceName} eşleşmesi iki bilgisayardan da kaldırılsın mı? Yeniden bağlanmak için 6 haneli kod gerekecek.",
            "Cihazı unut",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _keyboardBridgeService.ForgetPeerAsync();
        ResetActiveSession();
        _settings.PairedDevice = null;
        await SaveSettingsAsync();
        GeneratePairingCode();
        RefreshRecentDevices();
        RefreshSession();
        SetStatus($"{deviceName} eşleşmesi kaldırıldı. Yeni bağlantı kodu hazır.");
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _keyboardBridgeService.SetRemoteControl(false);
        SetModeButton(ViewModeButton);
        if (_outgoingSessionApproved)
        {
            EnsureScreenViewing();
        }
        RefreshSession();
        SetStatus("Sadece görüntüleme modu etkin.");
    }

    private async void ControlModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetModeButton(ControlModeButton);
        if (!_outgoingSessionApproved)
        {
            SetStatus("Önce Son Cihazlar bölümündeki Bağlan düğmesine basıp karşı taraftan onay alın.");
            return;
        }

        EnsureScreenViewing();
        if (_settings.PairedDevice is not null && !_keyboardBridgeService.IsRemoteControlActive)
        {
            if (await _keyboardBridgeService.ToggleRemoteControlAsync())
            {
                _sessionStartedUtc ??= DateTime.UtcNow;
            }
        }
        RefreshSession();
        if (_keyboardBridgeService.IsRemoteControlActive)
        {
            SetStatus("Tam kontrol modu etkin.");
        }
    }

    private void FileModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetModeButton(FileModeButton);
        if (_settings.PairedDevice is null)
        {
            SetStatus("Dosya göndermek için önce bir cihaz eşleştirin.");
            return;
        }

        if (!_outgoingSessionApproved)
        {
            SetStatus("Dosya aktarımı için önce Son Cihazlar bölümünden bağlantı onayı alın.");
            return;
        }

        var window = new FileTransferWindow(_settings.PairedDevice, _fileTransferService);
        window.Closed += (_, _) => SetModeButton(ControlModeButton);
        window.Show();
    }

    private void SetModeButton(Button selected)
    {
        foreach (var button in new[] { ViewModeButton, ControlModeButton, FileModeButton })
        {
            button.Background = button == selected
                ? new SolidColorBrush(Color.FromRgb(234, 246, 255))
                : Brushes.White;
            button.BorderBrush = button == selected
                ? new SolidColorBrush(Color.FromRgb(73, 175, 255))
                : new SolidColorBrush(Color.FromRgb(206, 216, 227));
        }
    }

    private async void InputControlCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _settings.EnableKeyboardControl = KeyboardControlCheckBox.IsChecked == true;
        _settings.EnableMouseControl = MouseControlCheckBox.IsChecked == true;
        await SaveSettingsAsync();
        SetStatus("Kontrol izinleri güncellendi.");
    }

    private async void ClipboardAccessCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _settings.EnableClipboardSync = ClipboardAccessCheckBox.IsChecked == true;
        await SaveSettingsAsync();
        SetStatus(_settings.EnableClipboardSync ? "Pano eşitleme açıldı." : "Pano eşitleme kapatıldı.");
    }

    private async void SettingsInputCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _settings.EnableKeyboardControl = SettingsKeyboardControlCheckBox.IsChecked == true;
        _settings.EnableMouseControl = SettingsMouseControlCheckBox.IsChecked == true;
        SyncSettingsUi();
        await SaveSettingsAsync();
        SetStatus("Klavye ve fare izinleri güncellendi.");
    }

    private async void SettingsClipboardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _settings.EnableClipboardSync = SettingsClipboardAccessCheckBox.IsChecked == true;
        SyncSettingsUi();
        await SaveSettingsAsync();
        SetStatus(_settings.EnableClipboardSync ? "Pano eşitleme açıldı." : "Pano eşitleme kapatıldı.");
    }

    private async void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _settings.StartWithWindows = AutoStartCheckBox.IsChecked == true;
        ApplyStartupRegistration(_settings.StartWithWindows);
        await SaveSettingsAsync();
        SetStatus(_settings.StartWithWindows ? "Otomatik başlatma açıldı." : "Otomatik başlatma kapatıldı.");
    }

    private void SyncSettingsUi()
    {
        _syncingSettingsUi = true;
        KeyboardControlCheckBox.IsChecked = _settings.EnableKeyboardControl;
        MouseControlCheckBox.IsChecked = _settings.EnableMouseControl;
        ClipboardAccessCheckBox.IsChecked = _settings.EnableClipboardSync;
        SettingsKeyboardControlCheckBox.IsChecked = _settings.EnableKeyboardControl;
        SettingsMouseControlCheckBox.IsChecked = _settings.EnableMouseControl;
        SettingsClipboardAccessCheckBox.IsChecked = _settings.EnableClipboardSync;
        SettingsDeviceNameTextBox.Text = _settings.DeviceName;
        AutoStartCheckBox.IsChecked = _settings.StartWithWindows;
        _syncingSettingsUi = false;
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings);
        _keyboardBridgeService.UpdateSettings(_settings);
        _screenStreamingService.UpdateSettings(_settings);
        _fileTransferService.UpdateSettings(_settings);
        _clipboardSyncService.UpdateSettings(_settings);
    }

    private void SetIncomingSessionApproved(bool approved)
    {
        _incomingSessionApproved = approved;
        _keyboardBridgeService.SetIncomingSessionApproved(approved);
        _screenStreamingService.SetIncomingSessionApproved(approved);
        _fileTransferService.SetIncomingSessionApproved(approved);
        _clipboardSyncService.SetIncomingSessionApproved(approved);
    }

    private void ResetActiveSession()
    {
        _keyboardBridgeService.SetRemoteControl(false);
        SetIncomingSessionApproved(false);
        _outgoingSessionApproved = false;
        _sessionStartedUtc = null;
        _viewingDeviceId = null;
        _screenStreamingService.StopViewing();
        RemoteScreenImage.Source = null;
        _screenPreviewWindow?.Close();
    }

    private void RefreshRecentDevices()
    {
        var peers = _knownPeers.Values
            .Where(peer => peer.DeviceId != _settings.DeviceId)
            .Where(peer => DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15))
            .OrderByDescending(peer => peer.LastSeenUtc)
            .ToList();

        var items = new List<RecentDeviceItem>();
        if (_settings.PairedDevice is not null)
        {
            var pairedPeer = peers.FirstOrDefault(peer => peer.DeviceId == _settings.PairedDevice.DeviceId);
            var identityMismatchPeer = pairedPeer is null
                ? peers.FirstOrDefault(peer => IsSameDeviceEndpoint(peer, _settings.PairedDevice))
                : null;
            var displayedPeer = pairedPeer ?? identityMismatchPeer;
            var requiresRepair = identityMismatchPeer is not null;
            items.Add(new RecentDeviceItem
            {
                DeviceId = _settings.PairedDevice.DeviceId,
                DeviceName = _settings.PairedDevice.DeviceName,
                IpAddress = displayedPeer?.IpAddress ?? _settings.PairedDevice.IpAddress,
                Platform = "Windows bilgisayar",
                Status = requiresRepair
                    ? "● Yeniden doğrulama gerekli"
                    : pairedPeer is null ? "● Çevrimdışı" : "● Çevrimiçi",
                StatusBrush = requiresRepair
                    ? new SolidColorBrush(Color.FromRgb(181, 71, 8))
                    : pairedPeer is null ? Brushes.SlateGray : new SolidColorBrush(Color.FromRgb(3, 152, 85)),
                LastSeen = requiresRepair
                    ? "Güvenlik kimliği değişti; bir kez kod girin"
                    : pairedPeer is null ? "Son bağlantı: daha önce" : "Yerel ağda bulundu",
                RequiresRepair = requiresRepair,
                ConnectButtonText = requiresRepair ? "Doğrula" : "Bağlan"
            });
        }

        items.AddRange(peers
            .Where(peer => _settings.PairedDevice is null ||
                           (peer.DeviceId != _settings.PairedDevice.DeviceId &&
                            !IsSameDeviceEndpoint(peer, _settings.PairedDevice)))
            .Select(peer => new RecentDeviceItem
            {
                DeviceId = peer.DeviceId,
                DeviceName = peer.DeviceName,
                IpAddress = peer.IpAddress,
                Platform = "Windows bilgisayar",
                Status = "● Çevrimiçi",
                StatusBrush = new SolidColorBrush(Color.FromRgb(3, 152, 85)),
                LastSeen = "İlk eşleştirme için kod gerekli",
                ConnectButtonText = "Eşleştir"
            }));

        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(item =>
                item.DeviceName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.IpAddress.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        DeviceList.ItemsSource = items;
        EmptyDevicesPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsSameDeviceEndpoint(PeerDevice peer, PairedDevice pairedDevice) =>
        (!string.IsNullOrWhiteSpace(peer.IpAddress) && peer.IpAddress == pairedDevice.IpAddress) ||
        (!string.IsNullOrWhiteSpace(peer.DeviceName) &&
         peer.DeviceName.Equals(pairedDevice.DeviceName, StringComparison.OrdinalIgnoreCase));

    private void RefreshSession()
    {
        var paired = _settings.PairedDevice;
        var remoteActive = _keyboardBridgeService.IsRemoteControlActive;

        if (paired is null)
        {
            SessionDeviceNameText.Text = "Cihaz bağlı değil";
            SessionBadgeText.Text = "Hazır";
            ConnectionQualityText.Text = "Bekleniyor";
            ControlStatusText.Text = "Bağlantı bekleniyor";
            ControlStatusSubText.Text = "Bir cihaz bağlandığında burada görünecek.";
            DisconnectDeviceButton.Visibility = Visibility.Collapsed;
            SessionDurationText.Text = "00:00:00";
            SessionLatencyText.Text = "-- ms";
            return;
        }

        SessionDeviceNameText.Text = paired.DeviceName;
        SessionBadgeText.Text = remoteActive ? "Aktif Oturum"
            : _incomingSessionApproved ? "Gelen Oturum"
            : _screenConnected ? "Görüntüleniyor"
            : "Eşleşmiş";
        var peerSeen = _knownPeers.Values.Any(peer =>
            DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(15) &&
            (peer.DeviceId == paired.DeviceId || IsSameDeviceEndpoint(peer, paired)));
        if (_incomingSessionApproved && !_outgoingSessionApproved)
        {
            ConnectionQualityText.Text = "Onaylandı";
            SessionLatencyText.Text = "-- ms";
            ControlStatusText.Text = "Gelen bağlantıya izin verildi";
            ControlStatusSubText.Text = $"{paired.DeviceName} ekranınızı izleyebilir ve izin verdiğiniz girişleri kullanabilir.";
        }
        else if (_screenConnected)
        {
            ControlStatusText.Text = remoteActive ? "Klavye ve fare kontrolü aktif" : "Canlı ekran bağlantısı kuruldu";
            ControlStatusSubText.Text = remoteActive
                ? "Bu cihazla uzak bilgisayarı kontrol ediyorsunuz."
                : "Kontrolü başlatmak için Tam Kontrol modunu seçin.";
        }
        else if (_outgoingSessionApproved)
        {
            ConnectionQualityText.Text = "Ekran bekleniyor";
            SessionLatencyText.Text = "-- ms";
            ControlStatusText.Text = remoteActive ? "Kontrol izni açık, ekran bekleniyor" : "Canlı ekran bağlantısı bekleniyor";
            ControlStatusSubText.Text = "Bağlantı onaylandı; karşı bilgisayardan görüntü henüz alınamadı. Ağ ve güvenlik duvarını kontrol edin.";
        }
        else
        {
            ConnectionQualityText.Text = peerSeen ? "Cihaz bulundu" : "Çevrimdışı";
            SessionLatencyText.Text = "-- ms";
            ControlStatusText.Text = peerSeen ? "Eşleşmiş; bağlantı bekleniyor" : "Cihaz çevrimdışı";
            ControlStatusSubText.Text = peerSeen
                ? "Yeniden bağlanmak için cihaz listesindeki Bağlan düğmesini kullanın."
                : "Diğer bilgisayarda KeyBridge'in açık ve aynı ağda olduğundan emin olun.";
        }
        DisconnectDeviceButton.Visibility = Visibility.Visible;
    }

    private void EnsureScreenViewing()
    {
        var paired = _settings.PairedDevice;
        if (paired is null || !_outgoingSessionApproved)
        {
            _screenStreamingService.StopViewing();
            _viewingDeviceId = null;
            return;
        }

        if (_viewingDeviceId == paired.DeviceId)
        {
            return;
        }

        _viewingDeviceId = paired.DeviceId;
        _screenStreamingService.StartViewing(paired);
    }

    private void SessionTimer_Tick(object? sender, EventArgs e)
    {
        if (_keyboardBridgeService.IsRemoteControlActive || _incomingSessionApproved)
        {
            _sessionStartedUtc ??= DateTime.UtcNow;
            SessionDurationText.Text = (DateTime.UtcNow - _sessionStartedUtc.Value).ToString(@"hh\:mm\:ss");
        }

        if (DateTime.UtcNow - _lastDeviceRefreshUtc > TimeSpan.FromSeconds(5))
        {
            _lastDeviceRefreshUtc = DateTime.UtcNow;
            RefreshRecentDevices();
        }
    }

    private async void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (_applyingRemoteClipboard || !_outgoingSessionApproved ||
            !_settings.EnableClipboardSync || _settings.PairedDevice is null)
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text == _lastClipboardText)
            {
                return;
            }

            _lastClipboardText = text;
            await _clipboardSyncService.SendTextAsync(_settings.PairedDevice, text);
        }
        catch
        {
            // The Windows clipboard can be temporarily locked by another process.
        }
    }

    private void ScreenStreamingService_FrameReceived(object? sender, ScreenFrameEventArgs e)
    {
        BitmapImage image;
        try
        {
            using var stream = new MemoryStream(e.ImageBytes);
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
        }
        catch
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _screenConnected = true;
            RemoteScreenImage.Source = image;
            if (_screenPreviewImage is not null)
            {
                _screenPreviewImage.Source = image;
            }
            SessionLatencyText.Text = $"{e.LatencyMilliseconds} ms";
            ConnectionQualityText.Text = e.LatencyMilliseconds switch
            {
                < 120 => "Mükemmel",
                < 300 => "İyi",
                _ => "Zayıf"
            };
            SessionBadgeText.Text = _keyboardBridgeService.IsRemoteControlActive ? "Aktif Oturum" : "Görüntüleniyor";
        });
    }

    private void ScreenStreamingService_ConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            _screenConnected = connected;
            if (!connected)
            {
                RemoteScreenImage.Source = null;
            }
            RefreshSession();
        });
    }

    private void FileTransferService_FileReceived(object? sender, FileReceivedEventArgs e)
    {
        Dispatcher.Invoke(() => SetStatus($"{e.FileName} alındı ve İndirilenler klasörüne kaydedildi."));
    }

    private void ClipboardSyncService_TextReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_settings.EnableClipboardSync || text == _lastClipboardText)
            {
                return;
            }

            try
            {
                _applyingRemoteClipboard = true;
                Clipboard.SetText(text);
                _lastClipboardText = text;
                SetStatus("Uzak panodaki metin bu cihaza aktarıldı.");
            }
            catch
            {
                SetStatus("Uzak pano metni alınamadı.");
            }
            finally
            {
                _applyingRemoteClipboard = false;
            }
        });
    }

    private void FeatureService_StatusChanged(object? sender, string message) =>
        Dispatcher.Invoke(() => SetStatus(message));

    private void DiscoveryService_PeerSeen(object? sender, PeerDevice peer)
    {
        Dispatcher.Invoke(async () =>
        {
            _knownPeers[peer.DeviceId] = peer;
            await UpdatePairedEndpointIfNeededAsync(peer);
            RefreshRecentDevices();
            RefreshSession();
            if (SectionPage.Visibility == Visibility.Visible && SectionTitleText.Text == "Cihazlar")
            {
                ShowDevicesSection();
            }
        });
    }

    private void PairingService_PairingCompleted(object? sender, PairingCompletedEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            _settings.PairedDevice = e.Device;
            _settings.OnboardingCompleted = true;
            _outgoingSessionApproved = !e.IsIncoming;
            if (e.IsIncoming)
            {
                SetIncomingSessionApproved(true);
            }

            await SaveSettingsAsync();
            _sessionStartedUtc = DateTime.UtcNow;
            RefreshRecentDevices();
            RefreshSession();
            if (!e.IsIncoming)
            {
                EnsureScreenViewing();
            }

            SetStatus(e.IsIncoming
                ? $"{e.Device.DeviceName} bilgisayarının bağlantısı onaylandı."
                : $"{e.Device.DeviceName} ile bağlantı kuruldu.");
        });
    }

    private void PairingService_ConnectionApprovalRequested(
        object? sender,
        ConnectionApprovalRequestedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                this,
                $"{e.DeviceName} ({e.IpAddress}) bu bilgisayara bağlanmak, ekranı görmek ve izin verilen girişleri kullanmak istiyor.\n\nBağlantıya izin verilsin mi?",
                "Gelen bağlantı isteği",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            e.Respond(answer == MessageBoxResult.Yes);
            SetStatus(answer == MessageBoxResult.Yes
                ? $"{e.DeviceName} bağlantısı onaylandı."
                : $"{e.DeviceName} bağlantısı reddedildi.");
        });
    }

    private void KeyboardBridgeService_StatusChanged(object? sender, KeyboardBridgeStatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus(e.Message);
            RefreshSession();
        });
    }

    private void KeyboardBridgeService_PeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            ResetActiveSession();
            _settings.PairedDevice = null;
            await SaveSettingsAsync();
            GeneratePairingCode();
            RefreshRecentDevices();
            RefreshSession();
            SetStatus($"{e.DeviceName} bu cihazı eşleşmelerinden kaldırdı.");
        });
    }

    private void KeyboardBridgeService_PeerSessionEnded(object? sender, PeerDisconnectedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ResetActiveSession();
            RefreshRecentDevices();
            RefreshSession();
            SetStatus($"{e.DeviceName} oturumu kapattı. Cihaz kayıtlı tutuluyor.");
        });
    }

    private void HotkeyService_ToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            await _keyboardBridgeService.ToggleRemoteControlAsync();
            if (_keyboardBridgeService.IsRemoteControlActive)
            {
                _sessionStartedUtc ??= DateTime.UtcNow;
            }
            RefreshSession();
        });
    }

    private async Task UpdatePairedEndpointIfNeededAsync(PeerDevice peer)
    {
        if (_settings.PairedDevice is null || _settings.PairedDevice.DeviceId != peer.DeviceId)
        {
            return;
        }

        if (_settings.PairedDevice.IpAddress != peer.IpAddress ||
            _settings.PairedDevice.KeyboardPort != peer.KeyboardPort)
        {
            _settings.PairedDevice.IpAddress = peer.IpAddress;
            _settings.PairedDevice.KeyboardPort = peer.KeyboardPort;
            await SaveSettingsAsync();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        if (IsLoaded)
        {
            RefreshRecentDevices();
        }
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

    private void UpdateSearchPlaceholder() =>
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void PairingCodeInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateCodePlaceholder();

    private void PairingCodeInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCodePlaceholder();

    private void PairingCodeInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCodePlaceholder();

    private void UpdateCodePlaceholder() =>
        CodePlaceholder.Visibility = string.IsNullOrEmpty(PairingCodeInput.Text) && !PairingCodeInput.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async void OpenFullScreenPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_outgoingSessionApproved)
        {
            SetStatus("Masaüstü modunu açmak için önce kayıtlı cihazdan bağlantı onayı alın.");
            return;
        }

        if (RemoteScreenImage.Source is null)
        {
            EnsureScreenViewing();
            SetStatus("Masaüstü modu için canlı görüntü bağlantısı bekleniyor.");
            return;
        }

        if (_screenPreviewWindow is not null)
        {
            _screenPreviewWindow.Activate();
            return;
        }

        var monitorBounds = WindowMonitorBounds.GetMonitorBounds(this);
        _screenPreviewImage = new Image
        {
            Source = RemoteScreenImage.Source,
            Stretch = Stretch.Uniform
        };
        var closeButton = new Button
        {
            Content = "×",
            Width = 44,
            Height = 44,
            FontSize = 24,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16),
            Cursor = Cursors.Hand
        };
        var content = new Grid { Background = Brushes.Black };
        content.Children.Add(_screenPreviewImage);
        content.Children.Add(closeButton);

        var helpBanner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 4, 19, 31)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16, 9, 16, 9),
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = $"{_settings.PairedDevice?.DeviceName} masaüstü modu · Yerel kontrole dön: Ctrl + Alt + K",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            }
        };
        content.Children.Add(helpBanner);

        _screenPreviewWindow = new Window
        {
            Owner = this,
            Title = "KeyBridge Uzak Masaüstü",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = monitorBounds.Left,
            Top = monitorBounds.Top,
            Width = monitorBounds.Width,
            Height = monitorBounds.Height,
            Background = Brushes.Black,
            Topmost = true,
            Content = content
        };
        closeButton.Click += (_, _) => _screenPreviewWindow?.Close();
        _screenPreviewWindow.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                _screenPreviewWindow?.Close();
            }
        };
        _screenPreviewWindow.Closed += (_, _) =>
        {
            _keyboardBridgeService.SetLocalMousePassthrough(null);
            _screenPreviewWindow = null;
            _screenPreviewImage = null;
        };
        _screenPreviewWindow.Show();
        _screenPreviewWindow.UpdateLayout();
        var closeRegionTopLeft = _screenPreviewWindow.PointToScreen(
            new Point(Math.Max(0, _screenPreviewWindow.ActualWidth - 90), 0));
        var closeRegionBottomRight = _screenPreviewWindow.PointToScreen(
            new Point(_screenPreviewWindow.ActualWidth, 90));
        _keyboardBridgeService.SetLocalMousePassthrough((x, y) =>
            x >= closeRegionTopLeft.X && x <= closeRegionBottomRight.X &&
            y >= closeRegionTopLeft.Y && y <= closeRegionBottomRight.Y);

        if (!_keyboardBridgeService.IsRemoteControlActive &&
            await _keyboardBridgeService.ToggleRemoteControlAsync())
        {
            _sessionStartedUtc ??= DateTime.UtcNow;
            RefreshSession();
            SetStatus("Uzak masaüstü kontrolü etkin. Yerel kontrole dönmek için Ctrl + Alt + K kullanın.");
        }
    }

    private void CopyDeviceNameButton_Click(object sender, RoutedEventArgs e) => CopyText(_settings.DeviceName, "Cihaz adı kopyalandı.");
    private void CopyPairingCodeButton_Click(object sender, RoutedEventArgs e) => CopyText(_pairingCode, "Bağlantı kodu kopyalandı.");

    private void ShareButton_Click(object sender, RoutedEventArgs e) =>
        CopyText($"KeyBridge cihazı: {_settings.DeviceName}\nBağlantı kodu: {FormatPairingCode(_pairingCode)}\nErişim şifresi: {_pairingCode}", "Bağlantı bilgileri kopyalandı.");

    private void AccessPassword_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _accessPasswordVisible = !_accessPasswordVisible;
        AccessPasswordText.Text = _accessPasswordVisible ? _pairingCode : "••••••";
        e.Handled = true;
    }

    private void CopyText(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(successMessage);
        }
        catch
        {
            SetStatus("Pano şu anda kullanılamıyor.");
        }
    }

    private void HomeNavButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(HomeSidebarButton);
        SectionPage.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        SetStatus("Ana sayfa hazır.");
    }

    private void DevicesNavButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(DevicesSidebarButton);
        ShowDevicesSection();
    }

    private void ConnectionsNavButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ConnectionsSidebarButton);
        ShowConnectionsSection();
    }

    private void HistoryNavButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(HistorySidebarButton);
        ShowHistorySection();
    }

    private void NotificationButton_Click(object sender, RoutedEventArgs e)
    {
        var shouldOpen = !NotificationPopup.IsOpen;
        NotificationPopup.IsOpen = false;
        if (!shouldOpen)
        {
            return;
        }

        NotificationDot.Visibility = Visibility.Collapsed;
        RefreshNotificationPopup();
        NotificationPopup.IsOpen = true;
    }

    private void RefreshNotificationPopup()
    {
        NotificationItemsPanel.Children.Clear();
        NotificationEmptyText.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in _notifications.TakeLast(10).Reverse())
        {
            NotificationItemsPanel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 235, 241)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(18, 13, 18, 13),
                Child = new TextBlock
                {
                    Text = entry,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
    }

    private void ClearNotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        _notifications.Clear();
        NotificationDot.Visibility = Visibility.Collapsed;
        RefreshNotificationPopup();
    }

    private void OpenNotificationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        NotificationPopup.IsOpen = false;
        SetActiveNavigation(HistorySidebarButton);
        ShowHistorySection();
    }

    private void DeviceOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentDeviceItem item } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        var copyName = new MenuItem { Header = "Cihaz adını kopyala" };
        copyName.Click += (_, _) => CopyText(item.DeviceName, "Cihaz adı kopyalandı.");
        menu.Items.Add(copyName);

        if (!string.IsNullOrWhiteSpace(item.IpAddress))
        {
            var copyAddress = new MenuItem { Header = "IP adresini kopyala" };
            copyAddress.Click += (_, _) => CopyText(item.IpAddress, "IP adresi kopyalandı.");
            menu.Items.Add(copyAddress);
        }

        if (_settings.PairedDevice?.DeviceId == item.DeviceId)
        {
            menu.Items.Add(new Separator());
            var forgetDevice = new MenuItem
            {
                Header = "Cihazı unut",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24))
            };
            forgetDevice.Click += async (_, _) => await ForgetPairedDeviceAsync();
            menu.Items.Add(forgetDevice);
        }

        menu.IsOpen = true;
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SettingsSidebarButton);
        SyncSettingsUi();
        SettingsModal.Visibility = Visibility.Visible;
    }
    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => SettingsModal.Visibility = Visibility.Collapsed;
    private void UpgradeButton_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        this,
        "Bu açık kaynak sürümde canlı görüntüleme, tam kontrol, şifreli dosya aktarımı ve pano eşitleme etkindir.",
        "KeyBridge özellikleri",
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private void CloseSectionButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(HomeSidebarButton);
        SectionPage.Visibility = Visibility.Collapsed;
    }

    private void SetActiveNavigation(Button activeButton)
    {
        foreach (var button in new[] { HomeSidebarButton, DevicesSidebarButton, ConnectionsSidebarButton, HistorySidebarButton, SettingsSidebarButton })
        {
            button.Style = Resources[button == activeButton ? "ActiveNavButton" : "NavButton"] as Style;
        }
    }

    private void ShowDevicesSection()
    {
        SectionTitleText.Text = "Cihazlar";
        SectionSubtitleText.Text = "Aynı yerel ağda bulunan ve daha önce eşleştirilen bilgisayarlar";
        SectionContentPanel.Children.Clear();
        RefreshRecentDevices();

        var items = (DeviceList.ItemsSource as IEnumerable<RecentDeviceItem>)?.ToList() ?? [];
        if (items.Count == 0)
        {
            SectionContentPanel.Children.Add(CreateSectionMessage("Henüz cihaz bulunamadı.", "Diğer bilgisayarda KeyBridge'i açın ve aynı yerel ağa bağlanın."));
        }
        else
        {
            foreach (var item in items)
            {
                var action = new Button { Content = item.ConnectButtonText, Style = Resources["OutlineButton"] as Style, Width = 100, Height = 38, Tag = item };
                action.Click += RecentDeviceConnectButton_Click;
                SectionContentPanel.Children.Add(CreateSectionRow(item.DeviceName, $"{item.Status.Replace("● ", string.Empty)} · {item.IpAddress}", action));
            }
        }

        SectionPage.Visibility = Visibility.Visible;
    }

    private void ShowConnectionsSection()
    {
        SectionTitleText.Text = "Bağlantılar";
        SectionSubtitleText.Text = "Etkin eşleşme ve ağ güvenliği";
        SectionContentPanel.Children.Clear();

        if (_settings.PairedDevice is null)
        {
            var openPairing = new Button { Content = "Kod Gir", Style = Resources["PrimaryButton"] as Style, Width = 120, Height = 42 };
            openPairing.Click += (_, _) => { SectionPage.Visibility = Visibility.Collapsed; PairingCodeInput.Focus(); };
            SectionContentPanel.Children.Add(CreateSectionRow("Eşleşmiş cihaz yok", "Bağlantı koduyla aynı yerel ağdaki bir bilgisayara bağlanın.", openPairing));
        }
        else
        {
            var disconnect = new Button { Content = "Bağlantıyı Kes", Style = Resources["OutlineButton"] as Style, Width = 140, Height = 38, Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24)) };
            disconnect.Click += DisconnectDeviceButton_Click;
            var sessionState = _incomingSessionApproved ? "Gelen oturum"
                : _screenConnected ? "Canlı ekran bağlı"
                : _outgoingSessionApproved ? "Ekran bekleniyor"
                : "Eşleşmiş";
            SectionContentPanel.Children.Add(CreateSectionRow(_settings.PairedDevice.DeviceName, $"{_settings.PairedDevice.IpAddress} · {sessionState}", disconnect));
            SectionContentPanel.Children.Add(CreateSectionMessage("Güvenli oturum", "Klavye, fare, ekran isteği, dosya ve pano trafiği AES-GCM ile korunur."));
        }

        SectionContentPanel.Children.Add(CreateSectionMessage("Ağ kapsamı", "Otomatik keşif aynı yerel ağla sınırlıdır. Farklı ağlardan bağlantı bu sürümde desteklenmez."));
        SectionPage.Visibility = Visibility.Visible;
    }

    private void ShowHistorySection()
    {
        SectionTitleText.Text = "Geçmiş";
        SectionSubtitleText.Text = "Bu oturumdaki son uygulama olayları";
        SectionContentPanel.Children.Clear();
        foreach (var entry in _notifications.TakeLast(30).Reverse())
        {
            SectionContentPanel.Children.Add(CreateSectionMessage(entry, string.Empty));
        }

        if (SectionContentPanel.Children.Count == 0)
        {
            SectionContentPanel.Children.Add(CreateSectionMessage("Henüz geçmiş yok.", string.Empty));
        }
        SectionPage.Visibility = Visibility.Visible;
    }

    private static Border CreateSectionRow(string title, string subtitle, Button action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 0) });
        grid.Children.Add(text);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(245, 248, 250)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 9), Child = grid };
    }

    private static Border CreateSectionMessage(string title, string subtitle)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        return new Border { Background = new SolidColorBrush(Color.FromRgb(245, 248, 250)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 9), Child = text };
    }

    private async void SaveDeviceNameButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SettingsDeviceNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Cihaz adı boş olamaz.");
            return;
        }

        _settings.DeviceName = name;
        HeaderDeviceNameText.Text = name;
        LocalDeviceNameText.Text = name;
        await SaveSettingsAsync();
        SetStatus("Cihaz adı güncellendi.");
    }

    private void OpenTransfersFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "KeyBridge Transfers");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private async void CompleteOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        var deviceName = OnboardingDeviceNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = Environment.MachineName;
        }

        _settings.DeviceName = deviceName;
        _settings.EnableKeyboardControl = OnboardingKeyboardCheckBox.IsChecked == true;
        _settings.EnableMouseControl = OnboardingMouseCheckBox.IsChecked == true;
        _settings.EnableClipboardSync = OnboardingClipboardCheckBox.IsChecked == true;
        _settings.OnboardingCompleted = true;
        HeaderDeviceNameText.Text = deviceName;
        LocalDeviceNameText.Text = deviceName;
        SyncSettingsUi();
        await SaveSettingsAsync();
        FirstRunModal.Visibility = Visibility.Collapsed;
        SetStatus("İlk kurulum tamamlandı.");
    }

    private void SettingsModal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsModal))
        {
            SettingsModal.Visibility = Visibility.Collapsed;
        }
    }

    private void SettingsCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (_isWorkAreaMaximized)
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            _isWorkAreaMaximized = false;
            MaximizeWindowButton.ToolTip = "Tam ekran";
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        _restoreBounds = new Rect(Left, Top, Width, Height);
        var workArea = WindowMonitorBounds.GetMonitorWorkArea(this);
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;
        _isWorkAreaMaximized = true;
        MaximizeWindowButton.ToolTip = "Önceki boyuta dön";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsModal.Visibility == Visibility.Visible)
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SetStatus(string message)
    {
        SidebarStatusText.Text = message;
        var entry = $"{DateTime.Now:HH:mm}  {message}";
        if (_notifications.Count == 0 || _notifications[^1] != entry)
        {
            _notifications.Add(entry);
            if (_notifications.Count > 50)
            {
                _notifications.RemoveAt(0);
            }
        }

        NotificationDot.Visibility = Visibility.Visible;
        if (NotificationPopup.IsOpen)
        {
            RefreshNotificationPopup();
        }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null)
        {
            return;
        }

        if (enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            key.SetValue(StartupRegistryName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(StartupRegistryName, false);
        }
    }

    private sealed class RecentDeviceItem
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public string IpAddress { get; init; } = string.Empty;
        public string Platform { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public Brush StatusBrush { get; init; } = Brushes.SlateGray;
        public string LastSeen { get; init; } = string.Empty;
        public bool RequiresRepair { get; init; }
        public string ConnectButtonText { get; init; } = "Bağlan";
    }
}
