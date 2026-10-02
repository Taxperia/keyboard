using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using KeyBridge.Models;
using KeyBridge.Services;
using Microsoft.Win32;

namespace KeyBridge;

public partial class FileTransferWindow : Window
{
    private readonly PairedDevice _device;
    private readonly FileTransferService _transferService;
    private readonly ObservableCollection<LocalFileItem> _localItems = [];
    private readonly ObservableCollection<QueuedTransferItem> _queue = [];
    private string _currentDirectory;
    private bool _isSending;

    public FileTransferWindow(PairedDevice device, FileTransferService transferService)
    {
        _device = device;
        _transferService = transferService;
        _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        InitializeComponent();

        Owner = Application.Current.MainWindow;
        RemoteDeviceText.Text = $"Hedef cihaz: {_device.DeviceName} · {_device.IpAddress}";
        LocalFilesList.ItemsSource = _localItems;
        TransferQueueList.ItemsSource = _queue;
        _transferService.StatusChanged += TransferService_StatusChanged;
        Closed += (_, _) => _transferService.StatusChanged -= TransferService_StatusChanged;
        LoadDirectory(_currentDirectory);
    }

    private void LoadDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                AddLog("Klasör bulunamadı.");
                return;
            }

            _currentDirectory = directory.FullName;
            LocalPathTextBox.Text = _currentDirectory;
            _localItems.Clear();

            foreach (var child in directory.EnumerateDirectories().OrderBy(item => item.Name))
            {
                _localItems.Add(LocalFileItem.FromDirectory(child));
            }

            foreach (var child in directory.EnumerateFiles().OrderBy(item => item.Name))
            {
                _localItems.Add(LocalFileItem.FromFile(child));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AddLog($"Klasör açılamadı: {ex.Message}");
        }
    }

    private void QueuePath(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || _queue.Any(item => string.Equals(item.Path, info.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _queue.Add(new QueuedTransferItem(info.FullName, info.Name, FormatSize(info.Length)));
        AddLog($"Kuyruğa eklendi: {info.Name}");
    }

    private void QueueSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalFilesList.SelectedItem is LocalFileItem { IsDirectory: false } item)
        {
            QueuePath(item.Path);
        }
    }

    private void RemoveQueuedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSending && TransferQueueList.SelectedItem is QueuedTransferItem item)
        {
            _queue.Remove(item);
        }
    }

    private void LocalFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalFilesList.SelectedItem is not LocalFileItem item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            LoadDirectory(item.Path);
        }
        else
        {
            QueuePath(item.Path);
        }
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentDirectory);
        if (parent is not null)
        {
            LoadDirectory(parent.FullName);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadDirectory(_currentDirectory);

    private void LocalPathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LoadDirectory(LocalPathTextBox.Text.Trim());
            e.Handled = true;
        }
    }

    private void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Aktarım kuyruğuna eklenecek dosyaları seçin",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = _currentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            foreach (var fileName in dialog.FileNames)
            {
                QueuePath(fileName);
            }
        }
    }

    private async void SendQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending || _queue.Count == 0)
        {
            return;
        }

        _isSending = true;
        SendQueueButton.IsEnabled = false;
        TransferProgress.Visibility = Visibility.Visible;

        foreach (var item in _queue.ToList())
        {
            item.Status = "Gönderiliyor";
            try
            {
                await _transferService.SendFileAsync(_device, item.Path);
                item.Status = "Tamamlandı";
            }
            catch (Exception ex)
            {
                item.Status = "Hata";
                AddLog($"{item.Name}: {ex.Message}");
            }
        }

        TransferProgress.Visibility = Visibility.Collapsed;
        SendQueueButton.IsEnabled = true;
        _isSending = false;
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSending)
        {
            _queue.Clear();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TransferService_StatusChanged(object? sender, string message) =>
        Dispatcher.Invoke(() => AddLog(message));

    private void AddLog(string message)
    {
        TransferLogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        TransferLogList.ScrollIntoView(TransferLogList.Items[^1]);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private sealed record LocalFileItem(
        string Path,
        string Name,
        string Kind,
        string SizeText,
        string ModifiedText,
        bool IsDirectory)
    {
        public static LocalFileItem FromDirectory(DirectoryInfo info) =>
            new(info.FullName, info.Name, "Klasör", "", info.LastWriteTime.ToString("g"), true);

        public static LocalFileItem FromFile(FileInfo info) =>
            new(info.FullName, info.Name, "Dosya", FormatSize(info.Length), info.LastWriteTime.ToString("g"), false);
    }

    private sealed class QueuedTransferItem(string path, string name, string sizeText) : INotifyPropertyChanged
    {
        private string _status = "Bekliyor";

        public string Path { get; } = path;

        public string Name { get; } = name;

        public string SizeText { get; } = sizeText;

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
