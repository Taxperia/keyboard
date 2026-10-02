using System.Configuration;
using System.Windows;

namespace KeyBridge;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\KeyBridge.SingleInstance";

    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: false, SingleInstanceMutexName);

        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
        }
        catch (System.Threading.AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            MessageBox.Show(
                "KeyBridge zaten çalışıyor. Açık olan pencereyi kullanın.",
                "KeyBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

