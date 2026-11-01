namespace HDRControl.App.Models;

public sealed class AppSettings
{
    public const string DefaultAllDisplaysHotkey = "Ctrl+Alt+0";

    public bool KeepInSystemTrayOnClose { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public string? AllDisplaysHotkey { get; set; } = DefaultAllDisplaysHotkey;
    public Dictionary<string, string?> MonitorHotkeys { get; set; } = new(StringComparer.Ordinal);
}
