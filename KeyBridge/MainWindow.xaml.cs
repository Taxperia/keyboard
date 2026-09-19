using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyBridge.Models;
using KeyBridge.Services;
using Microsoft.Win32;

namespace KeyBridge;

public partial class MainWindow : Window
{
    private const string StartupRegistryName = "KeyBridge";

    private readonly SettingsStore _settingsStore = new();
    private readonly DiscoveryService _discoveryService = new();
    private readonly PairingService _pairingService;
    private readonly KeyboardBridgeService _keyboardBridgeService = new();
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly Dictionary<string, PeerDevice> _knownPeers = new();

    private AppSettings _settings = new();
    private PeerDevice? _selectedPeer;
    private AppPage _currentPage = AppPage.Welcome;
    private long _sentKeyboardPackets;
    private long _receivedKeyboardPackets;
    private bool _syncingSettingsUi;

    public MainWindow()
    {
        _pairingService = new PairingService(_settingsStore);

        InitializeComponent();

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;

        _discoveryService.PeerSeen += DiscoveryService_PeerSeen;
        _pairingService.PairingCompleted += PairingService_PairingCompleted;
        _keyboardBridgeService.StatusChanged += KeyboardBridgeService_StatusChanged;
        _keyboardBridgeService.PeerDisconnected += KeyboardBridgeService_PeerDisconnected;
        _hotkeyService.ToggleRequested += HotkeyService_ToggleRequested;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        ApplyTheme(_settings.Theme);
        SyncSettingsText();
        ShowSettingsTab("General");
        ShowPage(_settings.OnboardingCompleted ? AppPage.Dashboard : AppPage.Welcome);
        RefreshDashboard();
        ApplyResponsiveLayout();
        Opacity = 1;
        StartBackgroundServices();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_hotkeyService.Register(this))
        {
            SetStatus("Ctrl + Alt + K başka bir uygulama tarafından kullanılıyor olabilir.");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _hotkeyService.Dispose();
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
            SetStatus("Ağ keşfi ve klavye servisi hazır.");
        }
        catch (Exception ex)
        {
            SetStatus($"Servis başlatılamadı: {ex.Message}");
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Language);
    }

    private async void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string languageName ||
            !Enum.TryParse<AppLanguage>(languageName, out var language))
        {
            return;
        }

        _settings.Language = language;
        SyncSettingsText();
        await SaveSettingsAsync();
        SetStatus(language == AppLanguage.Turkish ? "Dil Türkçe olarak seçildi." : "Language set to English.");
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi || sender is not ComboBox comboBox ||
            comboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is string languageName &&
            Enum.TryParse<AppLanguage>(languageName, out var language))
        {
            _settings.Language = language;
            SyncSettingsText();
            await SaveSettingsAsync();
            SetStatus(language == AppLanguage.Turkish ? "Dil Türkçe olarak seçildi." : "Language set to English.");
        }
    }

    private void LanguageNextButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Theme);
    }

    private async void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string themeName ||
            !Enum.TryParse<AppTheme>(themeName, out var theme))
        {
            return;
        }

        _settings.Theme = theme;
        ApplyTheme(theme);
        SyncSettingsText();
        await SaveSettingsAsync();
        SetStatus($"{ThemeName(theme)} tema seçildi.");
    }

    private void ThemeNextButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.InputDevices);
    }

    private void InputDevicesNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.EnableKeyboardControl && !_settings.EnableMouseControl)
        {
            SetStatus("Devam etmek için klavye veya fare seçin.");
            return;
        }

        ShowPage(AppPage.Role);
    }

    private async void InputControlCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi || sender is not CheckBox checkBox)
        {
            return;
        }

        if (checkBox == KeyboardControlSetupCheckBox || checkBox == KeyboardControlCheckBox)
        {
            _settings.EnableKeyboardControl = checkBox.IsChecked == true;
        }
        else if (checkBox == MouseControlSetupCheckBox || checkBox == MouseControlCheckBox)
        {
            _settings.EnableMouseControl = checkBox.IsChecked == true;
        }

        SyncSettingsText();
        await SaveSettingsAsync();
        RefreshDashboard();
        SetStatus(ControlSelectionText());
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

    private async void RoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string roleName ||
            !Enum.TryParse<DeviceRole>(roleName, out var role))
        {
            return;
        }

        _settings.Role = role;
        SyncSettingsText();
        await SaveSettingsAsync();
        SetStatus($"{RoleName(role)} seçildi.");
    }

    private void RoleNextButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Devices);
        RefreshDeviceList();
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceList();
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPeer = DeviceList.SelectedItem as PeerDevice;
        if (_selectedPeer is not null)
        {
            SetStatus($"{_selectedPeer.DeviceName} seçildi.");
        }
    }

    private void GenerateCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null)
        {
            var openCode = _pairingService.CreatePairingCode(null);
            PairingCodeText.Text = $"Kod: {openCode}";
            SetStatus("Kod üretildi. Diğer bilgisayarda cihaz seçmeden bu kodu girebilirsiniz.");
            return;
        }

        var code = _pairingService.CreatePairingCode(_selectedPeer);
        PairingCodeText.Text = $"Kod: {code}";
        SetStatus("Kod üretildi. Diğer bilgisayarda aynı cihazı seçip kodu girin.");
    }

    private async void PairWithCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer is null)
        {
            SetStatus("Kod ağda aranıyor...");
        }

        var code = PairingCodeInput.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            SetStatus("Eşleştirme kodu 6 haneli olmalı.");
            return;
        }

        if (_selectedPeer is not null)
        {
            SetStatus("Eşleşme isteği gönderiliyor...");
        }

        var result = await _pairingService.PairWithCodeAsync(_selectedPeer, code);
        SetStatus(result.Message);

        if (result.Success)
        {
            _settings.OnboardingCompleted = true;
            await SaveSettingsAsync();
            RefreshDashboard();
            ShowPage(AppPage.Dashboard);
        }
    }

    private async void OpenDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PairedDevice is null)
        {
            SetStatus("Kontrol paneli için önce iki bilgisayarı kodla eşleştirin.");
            return;
        }

        _settings.OnboardingCompleted = true;
        await SaveSettingsAsync();
        RefreshDashboard();
        ShowPage(AppPage.Dashboard);
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsTab("General");
        SettingsModal.Visibility = Visibility.Visible;
    }

    private void ToggleRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PairedDevice is null)
        {
            ShowPage(AppPage.Devices);
            RefreshDeviceList();
            return;
        }

        _keyboardBridgeService.ToggleRemoteControl();
        RefreshDashboard();
    }

    private async void DisconnectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PairedDevice is null)
        {
            return;
        }

        var deviceName = _settings.PairedDevice.DeviceName;
        var answer = MessageBox.Show(
            this,
            $"{deviceName} ile eşleşme kaldırılsın mı? Yeniden bağlanmak için iki cihazı tekrar eşleştirmeniz gerekir.",
            "Bağlantıyı kes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        DisconnectDeviceButton.IsEnabled = false;
        SetStatus($"{deviceName} bağlantısı kesiliyor...");
        var peerNotified = await _keyboardBridgeService.DisconnectPeerAsync();
        await ClearPairingAsync();
        SetStatus(peerNotified
            ? $"{deviceName} ile bağlantı kesildi."
            : $"{deviceName} çevrimdışı görünüyor; bu cihazdaki eşleşme kaldırıldı.");
    }

    private void GoToDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        _keyboardBridgeService.SetRemoteControl(false);
        ShowPage(AppPage.Devices);
        RefreshDeviceList();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsModal.Visibility = Visibility.Collapsed;
    }

    private void SettingsModal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsModal))
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tab)
        {
            ShowSettingsTab(tab);
        }
    }

    private void RepairFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsModal.Visibility = Visibility.Collapsed;
        _keyboardBridgeService.SetRemoteControl(false);
        ShowPage(AppPage.Devices);
        RefreshDeviceList();
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsModal.Visibility == Visibility.Visible)
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed ||
            IsInsideInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // WPF can throw if the mouse state changes between the click and drag start.
        }
    }

    private static bool IsInsideInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or Selector)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void DiscoveryService_PeerSeen(object? sender, PeerDevice peer)
    {
        Dispatcher.Invoke(() =>
        {
            _knownPeers[peer.DeviceId] = peer;
            RefreshDeviceList(keepSelection: true);
            _ = UpdatePairedEndpointIfNeededAsync(peer);
        });
    }

    private void PairingService_PairingCompleted(object? sender, PairingCompletedEventArgs e)
    {
        Dispatcher.Invoke(() => _ = HandlePairingCompletedAsync(e.Device));
    }

    private async Task HandlePairingCompletedAsync(PairedDevice device)
    {
        _settings.PairedDevice = device;
        _settings.OnboardingCompleted = true;
        await SaveSettingsAsync();
        SetStatus($"{device.DeviceName} ile eşleşildi.");
        RefreshDashboard();
        ShowPage(AppPage.Dashboard);
    }

    private void KeyboardBridgeService_StatusChanged(object? sender, KeyboardBridgeStatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _sentKeyboardPackets = e.SentPackets;
            _receivedKeyboardPackets = e.ReceivedPackets;
            SetStatus(e.Message);
            RefreshDashboard();
        });
    }

    private void KeyboardBridgeService_PeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        Dispatcher.Invoke(() => _ = HandleRemoteDisconnectAsync(e.DeviceName));
    }

    private async Task HandleRemoteDisconnectAsync(string deviceName)
    {
        await ClearPairingAsync();
        SetStatus($"{deviceName} bağlantıyı kesti. Yeniden bağlanmak için eşleştirme ekranını kullanın.");
    }

    private async Task ClearPairingAsync()
    {
        _keyboardBridgeService.SetRemoteControl(false);
        _settings.PairedDevice = null;
        _selectedPeer = null;
        PairingCodeText.Text = "Kod: ------";
        PairingCodeInput.Clear();
        await SaveSettingsAsync();
        RefreshDashboard();
    }

    private void HotkeyService_ToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _keyboardBridgeService.ToggleRemoteControl();
            RefreshDashboard();
        });
    }

    private void ShowPage(AppPage page)
    {
        _currentPage = page;

        WelcomePage.Visibility = page == AppPage.Welcome ? Visibility.Visible : Visibility.Collapsed;
        LanguagePage.Visibility = page == AppPage.Language ? Visibility.Visible : Visibility.Collapsed;
        ThemePage.Visibility = page == AppPage.Theme ? Visibility.Visible : Visibility.Collapsed;
        InputDevicesPage.Visibility = page == AppPage.InputDevices ? Visibility.Visible : Visibility.Collapsed;
        RolePage.Visibility = page == AppPage.Role ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = page == AppPage.Devices ? Visibility.Visible : Visibility.Collapsed;
        DashboardPage.Visibility = page == AppPage.Dashboard ? Visibility.Visible : Visibility.Collapsed;

        DashboardButton.Visibility = _settings.OnboardingCompleted && page == AppPage.Dashboard
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopBar.Visibility = page is AppPage.Devices or AppPage.Dashboard
            ? Visibility.Visible
            : Visibility.Collapsed;

        ResizeForPage(page);
        HighlightStep(page);
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var compact = _currentPage is AppPage.Devices or AppPage.Dashboard && ActualWidth < 900;

        SidebarPanel.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);

        ThemeOptionsGrid.Columns = 3;
        InputDeviceOptionsGrid.Columns = 2;
        RoleOptionsGrid.Columns = 2;

        ApplyDevicesLayout(compact);
        ApplyDashboardLayout(compact);
    }

    private void ResizeForPage(AppPage page)
    {
        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        var (targetWidth, targetHeight) = page switch
        {
            AppPage.Welcome or AppPage.Language => (430d, 332d),
            AppPage.Theme or AppPage.InputDevices or AppPage.Role => (620d, 460d),
            AppPage.Devices => (1000d, 690d),
            AppPage.Dashboard => (1000d, 700d),
            _ => (430d, 332d)
        };

        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        var currentHeight = ActualHeight > 0 ? ActualHeight : Height;
        var centerX = double.IsNaN(Left)
            ? SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2)
            : Left + (currentWidth / 2);
        var centerY = double.IsNaN(Top)
            ? SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height / 2)
            : Top + (currentHeight / 2);

        Width = targetWidth;
        Height = targetHeight;
        Left = Clamp(centerX - (targetWidth / 2), SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - targetWidth);
        Top = Clamp(centerY - (targetHeight / 2), SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - targetHeight);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    private void ApplyDevicesLayout(bool compact)
    {
        if (compact)
        {
            DevicesListColumn.Width = new GridLength(1, GridUnitType.Star);
            DevicesPairColumn.Width = new GridLength(0);

            Grid.SetRow(DevicesListCard, 0);
            Grid.SetColumn(DevicesListCard, 0);
            DevicesListCard.Margin = new Thickness(0, 0, 0, 16);

            Grid.SetRow(DevicesPairingPanel, 1);
            Grid.SetColumn(DevicesPairingPanel, 0);
            DevicesPairingPanel.Margin = new Thickness(0);
            return;
        }

        DevicesListColumn.Width = new GridLength(1.1, GridUnitType.Star);
        DevicesPairColumn.Width = new GridLength(0.9, GridUnitType.Star);

        Grid.SetRow(DevicesListCard, 0);
        Grid.SetColumn(DevicesListCard, 0);
        DevicesListCard.Margin = new Thickness(0, 0, 12, 0);

        Grid.SetRow(DevicesPairingPanel, 0);
        Grid.SetColumn(DevicesPairingPanel, 1);
        DevicesPairingPanel.Margin = new Thickness(12, 0, 0, 0);
    }

    private void ApplyDashboardLayout(bool compact)
    {
        if (compact)
        {
            DashboardLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            DashboardRightColumn.Width = new GridLength(0);

            MoveDashboardCard(DashboardPairedCard, 0, 0, new Thickness(0, 0, 0, 14));
            MoveDashboardCard(DashboardHotkeyCard, 1, 0, new Thickness(0, 0, 0, 14));
            MoveDashboardCard(DashboardSettingsCard, 2, 0, new Thickness(0, 0, 0, 14));
            MoveDashboardCard(DashboardPairingCard, 3, 0, new Thickness(0));
            return;
        }

        DashboardLeftColumn.Width = new GridLength(1.1, GridUnitType.Star);
        DashboardRightColumn.Width = new GridLength(0.9, GridUnitType.Star);

        MoveDashboardCard(DashboardPairedCard, 0, 0, new Thickness(0, 0, 12, 18));
        MoveDashboardCard(DashboardHotkeyCard, 0, 1, new Thickness(12, 0, 0, 18));
        MoveDashboardCard(DashboardSettingsCard, 1, 0, new Thickness(0, 0, 12, 0));
        MoveDashboardCard(DashboardPairingCard, 1, 1, new Thickness(12, 0, 0, 0));
    }

    private static void MoveDashboardCard(FrameworkElement card, int row, int column, Thickness margin)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        card.Margin = margin;
    }

    private void HighlightStep(AppPage page)
    {
        var steps = new[]
        {
            (AppPage.Welcome, StepWelcome),
            (AppPage.Language, StepLanguage),
            (AppPage.Theme, StepTheme),
            (AppPage.InputDevices, StepInputDevices),
            (AppPage.Role, StepRole),
            (AppPage.Devices, StepDevices),
            (AppPage.Dashboard, StepDashboard)
        };

        foreach (var (stepPage, border) in steps)
        {
            border.Background = stepPage == page
                ? Resources["AccentDarkBrush"] as Brush
                : Brushes.Transparent;
            border.Opacity = stepPage == page ? 1 : 0.62;
        }
    }

    private void RefreshDeviceList(bool keepSelection = false)
    {
        if (_currentPage != AppPage.Devices)
        {
            return;
        }

        var selectedDeviceId = keepSelection ? _selectedPeer?.DeviceId : null;
        var devices = _discoveryService.KnownPeers
            .Concat(_knownPeers.Values)
            .GroupBy(peer => peer.DeviceId)
            .Select(group => group.OrderByDescending(peer => peer.LastSeenUtc).First())
            .Where(peer => peer.DeviceId != _settings.DeviceId)
            .OrderBy(peer => peer.DeviceName)
            .ToList();

        DeviceList.ItemsSource = devices;

        if (selectedDeviceId is not null)
        {
            DeviceList.SelectedItem = devices.FirstOrDefault(peer => peer.DeviceId == selectedDeviceId);
            _selectedPeer = DeviceList.SelectedItem as PeerDevice;
        }

        if (devices.Count == 0)
        {
            SetStatus("Aynı ağda KeyBridge bekleniyor.");
        }
    }

    private void RefreshDashboard()
    {
        SyncSettingsText();
        OpenDashboardAfterPairButton.IsEnabled = _settings.PairedDevice is not null;
        DashboardSettingsCard.Visibility = Visibility.Visible;
        DashboardPairingCard.Visibility = Visibility.Visible;

        if (_settings.PairedDevice is null)
        {
            PairedDeviceNameText.Text = "Cihaz bağlı değil";
            PairedDeviceText.Text = "Aynı ağdaki ikinci bilgisayarı eşleştirerek klavye ve fare paylaşımını başlatın.";
            DashboardSummaryText.Text = "Başlamak için bir bilgisayar eşleştirin.";
            ConnectionStatusText.Text = "BAĞLI DEĞİL";
            ConnectionStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#332725"));
            ConnectionStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F97362"));
            ToggleRemoteButton.IsEnabled = true;
            ToggleRemoteButton.Content = "Bilgisayar eşleştir";
            DisconnectDeviceButton.Visibility = Visibility.Collapsed;
            BridgeStatsText.Text = "Henüz giriş aktarımı yapılmadı.";
            CurrentControlText.Text = "Kontrol: Bu bilgisayar";
            EnabledInputsText.Text = ControlSelectionText();
            return;
        }

        PairedDeviceNameText.Text = _settings.PairedDevice.DeviceName;
        PairedDeviceText.Text = $"{_settings.PairedDevice.IpAddress}:{_settings.PairedDevice.KeyboardPort}";

        var remoteActive = _keyboardBridgeService.IsRemoteControlActive;
        var hasInputControl = _settings.EnableKeyboardControl || _settings.EnableMouseControl;

        DashboardSummaryText.Text = remoteActive
            ? $"{_settings.PairedDevice.DeviceName} kontrol ediliyor. Geri dönmek için Ctrl + Alt + K kullanın."
            : "Kontrol bu bilgisayarda.";
        ConnectionStatusText.Text = remoteActive ? "UZAK KONTROL AÇIK" : "BAĞLI";
        ConnectionStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(remoteActive ? "#17382F" : "#17342E"));
        ConnectionStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35D6AA"));
        BridgeStatsText.Text = $"Gönderilen {_sentKeyboardPackets:N0} · Alınan {_receivedKeyboardPackets:N0}";
        CurrentControlText.Text = remoteActive
            ? $"Kontrol: {_settings.PairedDevice.DeviceName}"
            : "Kontrol: Bu bilgisayar";
        EnabledInputsText.Text = ControlSelectionText();
        ToggleRemoteButton.IsEnabled = hasInputControl;
        ToggleRemoteButton.Content = hasInputControl
            ? remoteActive ? "Bu bilgisayara dön" : "Diğer bilgisayara geç"
            : "Kontrol türü seç";
        DisconnectDeviceButton.Visibility = Visibility.Visible;
        DisconnectDeviceButton.IsEnabled = true;
    }

    private async Task UpdatePairedEndpointIfNeededAsync(PeerDevice peer)
    {
        if (_settings.PairedDevice is null || _settings.PairedDevice.DeviceId != peer.DeviceId)
        {
            return;
        }

        if (_settings.PairedDevice.IpAddress == peer.IpAddress &&
            _settings.PairedDevice.KeyboardPort == peer.KeyboardPort)
        {
            return;
        }

        _settings.PairedDevice.IpAddress = peer.IpAddress;
        _settings.PairedDevice.KeyboardPort = peer.KeyboardPort;
        await SaveSettingsAsync();
        RefreshDashboard();
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings);
        _keyboardBridgeService.UpdateSettings(_settings);
    }

    private void SyncSettingsText()
    {
        _syncingSettingsUi = true;
        try
        {
            SelectedThemeText.Text = $"Seçili tema: {ThemeName(_settings.Theme)}";
            SelectedControlText.Text = ControlSelectionText();
            SelectedRoleText.Text = $"Seçili rol: {RoleName(_settings.Role)}";
            HotkeyText.Text = $"Bilgisayarlar arası geçiş: {_settings.ToggleHotkey.Replace("+", " + ")}";
            SidebarSubtitle.Text = $"{RoleName(_settings.Role)} - {Environment.MachineName}";

            KeyboardControlSetupCheckBox.IsChecked = _settings.EnableKeyboardControl;
            MouseControlSetupCheckBox.IsChecked = _settings.EnableMouseControl;
            KeyboardControlCheckBox.IsChecked = _settings.EnableKeyboardControl;
            MouseControlCheckBox.IsChecked = _settings.EnableMouseControl;
            AutoStartCheckBox.IsChecked = _settings.StartWithWindows;
            InputNextButton.IsEnabled = _settings.EnableKeyboardControl || _settings.EnableMouseControl;
            LanguageComboBox.SelectedIndex = _settings.Language == AppLanguage.Turkish ? 1 : 2;
            SettingsLanguageComboBox.SelectedIndex = _settings.Language == AppLanguage.Turkish ? 0 : 1;
        }
        finally
        {
            _syncingSettingsUi = false;
        }
    }

    private void SetStatus(string message)
    {
        TopStatusText.Text = message;
        SidebarStatus.Text = message;
    }

    private void ShowSettingsTab(string tab)
    {
        SettingsGeneralPage.Visibility = tab == "General" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDownloadsPage.Visibility = tab == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        SettingsThemePage.Visibility = tab == "Theme" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAboutPage.Visibility = tab == "About" ? Visibility.Visible : Visibility.Collapsed;

        SettingsTitleText.Text = tab switch
        {
            "Downloads" => "Yayın Paketleri",
            "Theme" => "Tema",
            "About" => "Hakkında",
            _ => "Genel Ayarlar"
        };

        SetSettingsNavButtonState(SettingsGeneralNavButton, tab == "General");
        SetSettingsNavButtonState(SettingsDownloadsNavButton, tab == "Downloads");
        SetSettingsNavButtonState(SettingsThemeNavButton, tab == "Theme");
        SetSettingsNavButtonState(SettingsAboutNavButton, tab == "About");
    }

    private static void SetSettingsNavButtonState(Button button, bool active)
    {
        if (active)
        {
            button.SetResourceReference(BackgroundProperty, "AccentDarkBrush");
            button.SetResourceReference(BorderBrushProperty, "AccentDarkBrush");
            button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            return;
        }

        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                key.SetValue(StartupRegistryName, $"\"{executablePath}\"");
            }
        }
        else
        {
            key.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        SetBrush("AppBackgroundBrush", "Transparent");
        SetBrush("PanelBrush", theme switch
        {
            AppTheme.Light => "#FFFFFF",
            AppTheme.Purple => "#2A2344",
            _ => "#141414"
        });
        SetBrush("PanelAltBrush", theme switch
        {
            AppTheme.Light => "#EEF3F8",
            AppTheme.Purple => "#352B57",
            _ => "#3A3A3A"
        });
        SetBrush("BorderBrushSoft", theme switch
        {
            AppTheme.Light => "#CCD8E4",
            AppTheme.Purple => "#5A4A86",
            _ => "#2A2A2A"
        });
        SetBrush("TextPrimaryBrush", theme switch
        {
            AppTheme.Light => "#18212B",
            AppTheme.Purple => "#F7F1FF",
            _ => "#F3F7FA"
        });
        SetBrush("TextSecondaryBrush", theme switch
        {
            AppTheme.Light => "#546474",
            AppTheme.Purple => "#C7BBDD",
            _ => "#AAB6C2"
        });
        SetBrush("AccentBrush", theme switch
        {
            AppTheme.Light => "#0D9F86",
            AppTheme.Purple => "#BFA2FF",
            _ => "#28C2A0"
        });
        SetBrush("AccentDarkBrush", theme switch
        {
            AppTheme.Light => "#CFECE6",
            AppTheme.Purple => "#493875",
            _ => "#0D6F5D"
        });
        SetBrush("WarningBrush", theme switch
        {
            AppTheme.Purple => "#FFCB77",
            _ => "#F2B84B"
        });
    }

    private void SetBrush(string resourceName, string color)
    {
        Resources[resourceName] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string ThemeName(AppTheme theme)
    {
        return theme switch
        {
            AppTheme.Light => "Light",
            AppTheme.Purple => "Purple",
            _ => "Koyu"
        };
    }

    private static string RoleName(DeviceRole role)
    {
        return role == DeviceRole.Primary ? "1. cihaz" : "2. cihaz";
    }

    private string ControlSelectionText()
    {
        return (_settings.EnableKeyboardControl, _settings.EnableMouseControl) switch
        {
            (true, true) => "Klavye ve fare kontrolü seçili.",
            (true, false) => "Sadece klavye kontrolü seçili.",
            (false, true) => "Sadece fare kontrolü seçili.",
            _ => "Devam etmek için klavye veya fare seçin."
        };
    }

    private enum AppPage
    {
        Welcome,
        Language,
        Theme,
        InputDevices,
        Role,
        Devices,
        Dashboard
    }
}
