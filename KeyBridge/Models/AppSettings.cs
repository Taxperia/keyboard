using System;

namespace KeyBridge.Models;

public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    public string DeviceName { get; set; } = Environment.MachineName;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public AppLanguage Language { get; set; } = AppLanguage.Turkish;

    public DeviceRole Role { get; set; } = DeviceRole.Primary;

    public bool OnboardingCompleted { get; set; }

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+K";

    public bool EnableKeyboardControl { get; set; } = true;

    public bool EnableMouseControl { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public PairedDevice? PairedDevice { get; set; }
}
