using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Input;
using HDRControl.App.Models;
using HDRControl.App.Services;
using System.Windows;
using System.Windows.Media;

namespace HDRControl.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan SuccessNotificationDuration = TimeSpan.FromSeconds(6);
    private static readonly Brush SuccessNotificationBrush =
        new SolidColorBrush(Color.FromArgb(255, 36, 138, 90));
    private static readonly Brush WarningNotificationBrush =
        new SolidColorBrush(Color.FromArgb(255, 196, 120, 0));
    private static readonly Brush ErrorNotificationBrush =
        new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
    private static readonly Brush InformationalNotificationBrush =
        new SolidColorBrush(Color.FromArgb(255, 10, 132, 255));
    private readonly DisplayConfigService _displayConfigService;
    private readonly AppSettingsService _settingsService;
    private readonly AutoStartService _autoStartService;
    private bool _isRefreshing;
    private bool _isApplying;
    private bool _isSettingsVisible;
    private bool _isNotificationOpen;
    private int _notificationVersion;
    private string _notificationTitle = string.Empty;
    private string _notificationMessage = string.Empty;
    private string _lastUpdatedText = "Detecting displays";
    private NotificationSeverity _notificationSeverity = NotificationSeverity.Informational;
    private bool _startWithWindows;
    private bool _canConfigureAutoStart;

    public MainViewModel(
        DisplayConfigService displayConfigService,
        AppSettingsService settingsService,
        AutoStartService autoStartService)
    {
        _displayConfigService = displayConfigService;
        _settingsService = settingsService;
        _autoStartService = autoStartService;
        Settings = settingsService.Load();
        Settings.MonitorHotkeys ??= new Dictionary<string, string?>(StringComparer.Ordinal);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        try
        {
            _startWithWindows = _autoStartService.IsEnabled();
            _canConfigureAutoStart = true;
            if (_startWithWindows)
            {
                // The entry still points to where the exe was when startup was turned on.
                _autoStartService.RepairPath();
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ReportError("Could not read startup setting", error.Message);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? HotkeyConfigurationChanged;
    public event Action<string, string>? NotificationShown;

    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public AppSettings Settings { get; }
    public ICommand RefreshCommand { get; }

    public string DetectedMonitorCountLabel => Monitors.Count == 1
        ? "1 display detected"
        : $"{Monitors.Count} displays detected";

    public string HdrCapableCountLabel
    {
        get
        {
            var count = Monitors.Count(monitor => monitor.CanSelect);
            return count == 1 ? "1 HDR-capable" : $"{count} HDR-capable";
        }
    }

    public bool IsAllDisplaysHdrEnabled
    {
        get
        {
            var capableMonitors = Monitors.Where(monitor => monitor.CanSelect).ToArray();
            return capableMonitors.Length > 0 && capableMonitors.All(monitor => monitor.IsHdrEnabled);
        }
    }

    public bool CanToggleAllDisplays => Monitors.Any(monitor => monitor.CanSelect) && !IsBusy;

    public string AllDisplaysStatusLabel
    {
        get
        {
            var capableMonitors = Monitors.Where(monitor => monitor.CanSelect).ToArray();
            if (capableMonitors.Length == 0)
            {
                return "No HDR-capable displays";
            }

            var enabledCount = capableMonitors.Count(monitor => monitor.IsHdrEnabled);
            return enabledCount == capableMonitors.Length
                ? "All HDR on"
                : enabledCount == 0
                    ? "All HDR off"
                    : "Mixed HDR states";
        }
    }

    public Visibility MonitorListVisibility =>
        Monitors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EmptyStateVisibility =>
        Monitors.Count > 0 || IsRefreshing
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility DisplaysPageVisibility =>
        IsSettingsVisible ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SettingsPageVisibility =>
        IsSettingsVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RefreshButtonVisibility =>
        IsSettingsVisible ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSettingsVisible => _isSettingsVisible;

    public string SettingsButtonLabel => IsSettingsVisible ? "Displays" : "Settings";

    public string CurrentPageTitle => IsSettingsVisible ? "Settings" : "Displays";

    public string CurrentPageSubtitle => IsSettingsVisible
        ? "Choose how HDR Control looks, starts and closes."
        : "Choose which screens use high dynamic range.";


    public bool IsBusy => IsRefreshing || IsApplying;
    public bool CanSelectMonitors => !IsBusy;

    public string AllDisplaysHotkeyDisplayText => string.IsNullOrWhiteSpace(Settings.AllDisplaysHotkey)
        ? "No hotkey"
        : Settings.AllDisplaysHotkey;

    public bool KeepInSystemTrayOnClose => Settings.KeepInSystemTrayOnClose;

    public bool StartWithWindows => _startWithWindows;

    public bool CanConfigureAutoStart => _canConfigureAutoStart;

    public bool StartMinimizedToTray => Settings.StartMinimizedToTray;

    public bool UseDarkMode => Settings.UseDarkMode;

    // Sun while light, moon while dark; the name says what a click does.
    public string ThemeGlyph => UseDarkMode ? "" : "";

    public string ThemeToggleName => UseDarkMode ? "Switch to light mode" : "Switch to dark mode";

    public bool CanConfigureStartMinimized => _canConfigureAutoStart && _startWithWindows;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanSelectMonitors));
                OnPropertyChanged(nameof(CanToggleAllDisplays));
                OnPropertyChanged(nameof(EmptyStateVisibility));
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanSelectMonitors));
                OnPropertyChanged(nameof(CanToggleAllDisplays));
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    public bool IsNotificationOpen
    {
        get => _isNotificationOpen;
        private set
        {
            if (SetProperty(ref _isNotificationOpen, value))
            {
                OnPropertyChanged(nameof(NotificationVisibility));
            }
        }
    }

    public Visibility NotificationVisibility => _isNotificationOpen ? Visibility.Visible : Visibility.Collapsed;

    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => SetProperty(ref _notificationMessage, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    private NotificationSeverity NotificationSeverity
    {
        get => _notificationSeverity;
        set
        {
            if (SetProperty(ref _notificationSeverity, value))
            {
                OnPropertyChanged(nameof(NotificationGlyph));
                OnPropertyChanged(nameof(NotificationIconBrush));
            }
        }
    }

    public string NotificationGlyph => _notificationSeverity switch
    {
        NotificationSeverity.Success => "",
        NotificationSeverity.Warning => "",
        NotificationSeverity.Error => "",
        _ => ""
    };

    public Brush NotificationIconBrush => _notificationSeverity switch
    {
        NotificationSeverity.Success => SuccessNotificationBrush,
        NotificationSeverity.Warning => WarningNotificationBrush,
        NotificationSeverity.Error => ErrorNotificationBrush,
        _ => InformationalNotificationBrush
    };

    public string AppVersionLabel =>
        $"Version {typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}";

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        // Shown after the first refresh, which closes notifications when every display reads fine.
        if (_settingsService.RecoveredBackupPath is { } backupPath)
        {
            ReportWarning(
                "Settings were reset",
                $"The settings file could not be read and was saved as {backupPath}.");
        }
    }

    public void DismissNotification() => IsNotificationOpen = false;

    public void ToggleSettingsPage()
    {
        SetSettingsPage(!_isSettingsVisible);
    }

    public void ShowDisplaysPage() => SetSettingsPage(false);

    public void ShowSettingsPage() => SetSettingsPage(true);

    private void SetSettingsPage(bool showSettings)
    {
        if (_isSettingsVisible == showSettings)
        {
            return;
        }

        _isSettingsVisible = showSettings;
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(DisplaysPageVisibility));
        OnPropertyChanged(nameof(SettingsPageVisibility));
        OnPropertyChanged(nameof(RefreshButtonVisibility));
        OnPropertyChanged(nameof(SettingsButtonLabel));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
    }

    public void SetMonitorHotkey(MonitorViewModel monitor, string? hotkey)
    {
        var normalized = NormalizeHotkey(hotkey);
        var oldHotkey = monitor.Hotkey;
        monitor.SetHotkey(normalized);
        // null is kept on purpose: a missing entry would get the default hotkey on the next refresh.
        Settings.MonitorHotkeys[monitor.Identity] = normalized;
        var released = ReleaseFromDisconnectedMonitors(normalized);

        if (!TrySaveSettings())
        {
            monitor.SetHotkey(oldHotkey);
            RestoreMonitorHotkey(monitor.Identity, oldHotkey);
            RestoreReleased(released);
            return;
        }

        HotkeyConfigurationChanged?.Invoke();
    }

    // A disconnected display keeps its saved hotkey for when it comes back. If that hotkey is given to
    // another row meanwhile, the entry is dropped so the display gets a free default hotkey on reconnect.
    private List<KeyValuePair<string, string?>> ReleaseFromDisconnectedMonitors(string? hotkey)
    {
        if (!HotkeyGesture.TryParse(hotkey, out var gesture) || gesture is null)
        {
            return [];
        }

        var connected = Monitors.Select(monitor => monitor.Identity).ToHashSet(StringComparer.Ordinal);
        var released = Settings.MonitorHotkeys
            .Where(entry => !connected.Contains(entry.Key) && IsSameGesture(gesture, entry.Value))
            .ToList();
        foreach (var entry in released)
        {
            Settings.MonitorHotkeys.Remove(entry.Key);
        }

        return released;
    }

    private void RestoreReleased(List<KeyValuePair<string, string?>> released)
    {
        foreach (var (identity, hotkey) in released)
        {
            Settings.MonitorHotkeys[identity] = hotkey;
        }
    }

    // Ctrl+Alt+<position> unless a saved display or the all-displays row holds it; then the first free Ctrl+Alt+1-9.
    private string? FindDefaultHotkey(int position)
    {
        var candidates = position is >= 1 and <= 9
            ? Enumerable.Range(1, 9).Prepend(position)
            : Enumerable.Range(1, 9);
        return candidates
            .Select(number => $"Ctrl+Alt+{number}")
            .FirstOrDefault(candidate => HotkeyGesture.TryParse(candidate, out var gesture)
                && gesture is not null
                && !IsSameGesture(gesture, Settings.AllDisplaysHotkey)
                && !Settings.MonitorHotkeys.Values.Any(saved => IsSameGesture(gesture, saved)));
    }

    // target is the row being edited, null for the all-displays row.
    public string? FindHotkeyOwner(HotkeyGesture gesture, MonitorViewModel? target)
    {
        if (target is not null && IsSameGesture(gesture, Settings.AllDisplaysHotkey))
        {
            return "All displays";
        }

        return Monitors
            .FirstOrDefault(monitor => monitor != target && monitor.CanSelect && IsSameGesture(gesture, monitor.Hotkey))
            ?.Name;
    }

    private static bool IsSameGesture(HotkeyGesture gesture, string? hotkey) =>
        HotkeyGesture.TryParse(hotkey, out var other)
        && other is not null
        && other.Modifiers == gesture.Modifiers
        && other.VirtualKey == gesture.VirtualKey;

    public void ResetHotkeys()
    {
        var oldAllDisplays = Settings.AllDisplaysHotkey;
        var oldMonitorHotkeys = new Dictionary<string, string?>(Settings.MonitorHotkeys, StringComparer.Ordinal);

        Settings.AllDisplaysHotkey = AppSettings.DefaultAllDisplaysHotkey;
        Settings.MonitorHotkeys.Clear();
        var supportedDisplayIndex = 0;
        foreach (var monitor in Monitors)
        {
            var hotkey = monitor.CanSelect && supportedDisplayIndex < 9 ? $"Ctrl+Alt+{++supportedDisplayIndex}" : null;
            Settings.MonitorHotkeys[monitor.Identity] = hotkey;
            monitor.SetHotkey(hotkey);
        }

        var saved = TrySaveSettings();
        if (!saved)
        {
            Settings.AllDisplaysHotkey = oldAllDisplays;
            Settings.MonitorHotkeys.Clear();
            foreach (var (identity, hotkey) in oldMonitorHotkeys)
            {
                Settings.MonitorHotkeys[identity] = hotkey;
            }

            foreach (var monitor in Monitors)
            {
                monitor.SetHotkey(oldMonitorHotkeys.GetValueOrDefault(monitor.Identity));
            }
        }

        OnPropertyChanged(nameof(AllDisplaysHotkeyDisplayText));
        HotkeyConfigurationChanged?.Invoke();
        if (saved)
        {
            ShowNotification(NotificationSeverity.Success, "Hotkeys reset", "All hotkeys are back to their defaults.");
        }
    }

    public void SetAllDisplaysHotkey(string? hotkey)
    {
        var normalized = NormalizeHotkey(hotkey);
        var oldHotkey = Settings.AllDisplaysHotkey;
        Settings.AllDisplaysHotkey = normalized;
        var released = ReleaseFromDisconnectedMonitors(normalized);
        if (!TrySaveSettings())
        {
            Settings.AllDisplaysHotkey = oldHotkey;
            RestoreReleased(released);
            OnPropertyChanged(nameof(AllDisplaysHotkeyDisplayText));
            return;
        }

        OnPropertyChanged(nameof(AllDisplaysHotkeyDisplayText));
        HotkeyConfigurationChanged?.Invoke();
    }

    public void SetKeepInSystemTrayOnClose(bool enabled)
    {
        if (Settings.KeepInSystemTrayOnClose == enabled)
        {
            return;
        }

        Settings.KeepInSystemTrayOnClose = enabled;
        if (!TrySaveSettings())
        {
            Settings.KeepInSystemTrayOnClose = !enabled;
        }

        OnPropertyChanged(nameof(KeepInSystemTrayOnClose));
    }

    public void SetStartWithWindows(bool enabled)
    {
        if (!_canConfigureAutoStart || _startWithWindows == enabled)
        {
            return;
        }

        try
        {
            _autoStartService.SetEnabled(enabled);
            _startWithWindows = enabled;
            OnPropertyChanged(nameof(StartWithWindows));
            OnPropertyChanged(nameof(CanConfigureStartMinimized));
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ReportError("Could not update startup setting", error.Message);
        }
    }

    public void SetUseDarkMode(bool enabled)
    {
        if (Settings.UseDarkMode == enabled)
        {
            return;
        }

        Settings.UseDarkMode = enabled;
        if (!TrySaveSettings())
        {
            Settings.UseDarkMode = !enabled;
        }

        OnPropertyChanged(nameof(UseDarkMode));
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeToggleName));
    }

    public void SetStartMinimizedToTray(bool enabled)
    {
        if (Settings.StartMinimizedToTray == enabled)
        {
            return;
        }

        try
        {
            // Startup entries written by older versions lack the argument that marks an automatic start.
            if (enabled && _startWithWindows)
            {
                _autoStartService.SetEnabled(true);
            }

            Settings.StartMinimizedToTray = enabled;
            if (!TrySaveSettings())
            {
                Settings.StartMinimizedToTray = !enabled;
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ReportError("Could not update startup setting", error.Message);
        }

        OnPropertyChanged(nameof(StartMinimizedToTray));
    }

    public void ShowHotkeyRegistrationWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0)
        {
            ReportWarning(
                "Some hotkeys are unavailable",
                string.Join(Environment.NewLine, warnings));
        }
        else if (NotificationTitle == "Some hotkeys are unavailable")
        {
            IsNotificationOpen = false;
        }
    }

    public void ReportError(string title, string message) =>
        ShowNotification(NotificationSeverity.Error, title, message);

    public void ReportWarning(string title, string message) =>
        ShowNotification(NotificationSeverity.Warning, title, message);

    public async Task SetMonitorHdrEnabledAsync(MonitorViewModel monitor, bool enabled)
    {
        if (IsBusy || !monitor.CanSelect)
        {
            return;
        }

        IsApplying = true;
        try
        {
            string? applyError = null;
            try
            {
                await Task.Run(() => _displayConfigService.SetHdrEnabled(monitor.Monitor, enabled));
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                applyError = $"{monitor.Name}: {error.Message}";
            }

            var refreshError = await LoadDisplaysAsync();
            if (refreshError is not null)
            {
                applyError = AddError(applyError, $"Display status could not be refreshed: {refreshError}");
            }

            if (applyError is not null)
            {
                ShowNotification(NotificationSeverity.Error, "Could not update display", applyError);
            }
            else
            {
                ShowNotification(
                    NotificationSeverity.Success,
                    $"HDR {(enabled ? "enabled" : "disabled")}",
                    $"HDR was {(enabled ? "enabled" : "disabled")} for {monitor.Name}.");
            }

            LastUpdatedText = $"Last updated: {DateTime.Now:HH:mm}";
        }
        finally
        {
            IsApplying = false;
            NotifyDashboardChanged();
        }
    }

    public async Task ToggleMonitorHdrAsync(string identity)
    {
        await RefreshQuietlyAsync();
        var monitor = Monitors.FirstOrDefault(item => item.Identity == identity);
        if (monitor is not null)
        {
            await SetMonitorHdrEnabledAsync(monitor, !monitor.IsHdrEnabled);
        }
    }

    public async Task ToggleAllDisplaysHdrAsync()
    {
        // A hotkey can fire long after the last refresh, and HDR may have been changed in Windows since.
        await RefreshQuietlyAsync();
        if (IsBusy)
        {
            return;
        }

        var capableMonitors = Monitors.Where(monitor => monitor.CanSelect).ToArray();
        if (capableMonitors.Length == 0)
        {
            return;
        }

        var enabled = capableMonitors.Any(monitor => !monitor.IsHdrEnabled);
        IsApplying = true;
        try
        {
            var errors = new List<string>();
            foreach (var monitor in capableMonitors)
            {
                try
                {
                    await Task.Run(() => _displayConfigService.SetHdrEnabled(monitor.Monitor, enabled));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    errors.Add($"{monitor.Name}: {error.Message}");
                }
            }

            var refreshError = await LoadDisplaysAsync();
            if (refreshError is not null)
            {
                errors.Add($"Display status could not be refreshed: {refreshError}");
            }

            if (errors.Count > 0)
            {
                ShowNotification(
                    NotificationSeverity.Error,
                    "Some displays could not be updated",
                    string.Join(Environment.NewLine, errors));
            }
            else
            {
                ShowNotification(
                    NotificationSeverity.Success,
                    $"HDR {(enabled ? "enabled" : "disabled")} for all displays",
                    $"HDR was {(enabled ? "enabled" : "disabled")} for {capableMonitors.Length} displays.");
            }

            LastUpdatedText = $"Last updated: {DateTime.Now:HH:mm}";
        }
        finally
        {
            IsApplying = false;
            NotifyDashboardChanged();
        }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var refreshError = await LoadDisplaysAsync();
            if (refreshError is not null)
            {
                ShowNotification(NotificationSeverity.Error, "Could not load displays", refreshError);
                return;
            }

            var diagnostics = Monitors
                .Where(monitor => monitor.Monitor.Diagnostic is not null)
                .Select(monitor => $"{monitor.Name}: {monitor.Monitor.Diagnostic}")
                .ToArray();

            LastUpdatedText = $"Last updated: {DateTime.Now:HH:mm}";
            if (diagnostics.Length > 0)
            {
                ShowNotification(
                    NotificationSeverity.Warning,
                    "Some display information is unavailable",
                    string.Join(Environment.NewLine, diagnostics));
            }
            else
            {
                IsNotificationOpen = false;
            }
        }
        finally
        {
            IsRefreshing = false;
            NotifyDashboardChanged();
        }
    }

    // Unlike RefreshAsync it leaves open notifications alone, e.g. the toast of the change that caused it.
    public async Task RefreshQuietlyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var refreshError = await LoadDisplaysAsync();
            if (refreshError is not null)
            {
                ShowNotification(NotificationSeverity.Error, "Could not load displays", refreshError);
                return;
            }

            LastUpdatedText = $"Last updated: {DateTime.Now:HH:mm}";
        }
        finally
        {
            IsRefreshing = false;
            NotifyDashboardChanged();
        }
    }

    private async Task<string?> LoadDisplaysAsync()
    {
        IReadOnlyList<MonitorInfo> displays;
        try
        {
            displays = await Task.Run(_displayConfigService.QueryAllDisplays);
        }
        catch (Win32Exception error)
        {
            ClearMonitors();
            HotkeyConfigurationChanged?.Invoke();
            return error.Message;
        }

        Monitors.Clear();
        var settingsChanged = false;
        var supportedDisplayIndex = 0;
        for (var index = 0; index < displays.Count; index++)
        {
            var display = displays[index];
            // New displays, and returning ones whose hotkey was given away, get a free default hotkey.
            if (!Settings.MonitorHotkeys.ContainsKey(display.Identity))
            {
                Settings.MonitorHotkeys.Add(
                    display.Identity,
                    display.Status is HdrStatus.Enabled or HdrStatus.Disabled
                        ? FindDefaultHotkey(++supportedDisplayIndex)
                        : null);
                settingsChanged = true;
            }
            else if (display.Status is HdrStatus.Enabled or HdrStatus.Disabled)
            {
                supportedDisplayIndex++;
            }

            Monitors.Add(new MonitorViewModel(display, Settings.MonitorHotkeys[display.Identity]));
        }

        if (settingsChanged && !TrySaveSettings())
        {
            ShowNotification(
                NotificationSeverity.Error,
                "Could not save hotkeys",
                "Display hotkeys are active for this session but could not be saved.");
        }

        NotifyDashboardChanged();
        HotkeyConfigurationChanged?.Invoke();
        return null;
    }

    private void ClearMonitors()
    {
        Monitors.Clear();
        NotifyDashboardChanged();
    }

    private string? NormalizeHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return null;
        }

        return HotkeyGesture.TryParse(hotkey, out var gesture) && gesture is not null
            ? gesture.DisplayText
            : throw new ArgumentException("Use a modifier and a supported key for a global hotkey.", nameof(hotkey));
    }

    private void RestoreMonitorHotkey(string identity, string? hotkey) =>
        Settings.MonitorHotkeys[identity] = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey;

    private bool TrySaveSettings()
    {
        try
        {
            _settingsService.Save(Settings);
            return true;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowNotification(NotificationSeverity.Error, "Settings could not be saved", error.Message);
            return false;
        }
    }

    private static string AddError(string? current, string next) =>
        current is null ? next : $"{current}{Environment.NewLine}{next}";

    private void ShowNotification(NotificationSeverity severity, string title, string message)
    {
        if (severity == NotificationSeverity.Error)
        {
            ExceptionLog.Write(title, message);
        }

        NotificationSeverity = severity;
        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationOpen = true;
        NotificationShown?.Invoke(title, message);

        var version = ++_notificationVersion;
        if (severity == NotificationSeverity.Success)
        {
            _ = CloseNotificationLaterAsync(version);
        }
    }

    private async Task CloseNotificationLaterAsync(int version)
    {
        await Task.Delay(SuccessNotificationDuration);
        if (version == _notificationVersion)
        {
            IsNotificationOpen = false;
        }
    }

    private void NotifyDashboardChanged()
    {
        OnPropertyChanged(nameof(DetectedMonitorCountLabel));
        OnPropertyChanged(nameof(HdrCapableCountLabel));
        OnPropertyChanged(nameof(IsAllDisplaysHdrEnabled));
        OnPropertyChanged(nameof(CanToggleAllDisplays));
        OnPropertyChanged(nameof(AllDisplaysStatusLabel));
        OnPropertyChanged(nameof(MonitorListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        NotifyCommandsCanExecuteChanged();
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectMonitors));
    }

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}
