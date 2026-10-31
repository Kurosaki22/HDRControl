using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HDRControl.App.Models;

public sealed class MonitorViewModel : INotifyPropertyChanged
{
    private string? _hotkey;

    public MonitorViewModel(MonitorInfo monitor, string? hotkey)
    {
        Monitor = monitor;
        _hotkey = hotkey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MonitorInfo Monitor { get; }
    public string Identity => Monitor.Identity;
    public string Name => Monitor.Name;
    public bool IsHdrEnabled => Monitor.Status == HdrStatus.Enabled;
    public string StatusLabel => Monitor.Status switch
    {
        HdrStatus.Enabled => "HDR On",
        HdrStatus.Disabled => "HDR Off",
        HdrStatus.NotSupported => "Not supported",
        _ => "Unknown"
    };

    public string StatusDetail => Monitor.Diagnostic
        ?? (Monitor.Status == HdrStatus.NotSupported
            ? "Not supported"
            : Monitor.Status == HdrStatus.Unknown
                ? "Unable to determine HDR status."
                : StatusLabel);

    public bool CanSelect => Monitor.Status is HdrStatus.Enabled or HdrStatus.Disabled;

    public string Hotkey
    {
        get => _hotkey ?? string.Empty;
        private set
        {
            if (_hotkey == value)
            {
                return;
            }

            _hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyDisplayText));
        }
    }

    public string HotkeyDisplayText => string.IsNullOrWhiteSpace(Hotkey)
        ? "No shortcut"
        : Hotkey;

    public void SetHotkey(string? hotkey) => Hotkey = hotkey ?? string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
