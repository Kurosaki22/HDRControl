namespace HDRControl.App.Models;

public sealed class AppSettings
{
    public bool KeepInSystemTrayOnClose { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public string? AllDisplaysHotkey { get; set; } = "Ctrl+Alt+0";
    public Dictionary<string, string?> MonitorHotkeys { get; set; } = new(StringComparer.Ordinal);
}
