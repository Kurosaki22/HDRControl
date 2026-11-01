using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Input;
using HDRControl.App.Models;
using HDRControl.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HDRControl.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush SelectedNavigationForeground =
        new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
    private static readonly Brush DefaultNavigationForeground =
        new SolidColorBrush(Color.FromArgb(255, 51, 54, 58));
    private static readonly TimeSpan SuccessNotificationDuration = TimeSpan.FromSeconds(3);
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
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;
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
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            ReportError("Could not read startup setting", error.Message);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? HotkeyConfigurationChanged;

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
        ? "Choose how HDR Control starts and closes."
        : "Choose which screens use high dynamic range.";

    public Brush DisplaysNavigationForeground =>
        IsSettingsVisible ? DefaultNavigationForeground : SelectedNavigationForeground;

    public Brush SettingsNavigationForeground =>
        IsSettingsVisible ? SelectedNavigationForeground : DefaultNavigationForeground;

    public bool IsBusy => IsRefreshing || IsApplying;
    public bool CanSelectMonitors => !IsBusy;

    public string AllDisplaysHotkeyDisplayText => string.IsNullOrWhiteSpace(Settings.AllDisplaysHotkey)
        ? "No hotkey"
        : Settings.AllDisplaysHotkey;

    public bool KeepInSystemTrayOnClose => Settings.KeepInSystemTrayOnClose;

    public bool StartWithWindows => _startWithWindows;

    public bool CanConfigureAutoStart => _canConfigureAutoStart;

    public bool StartMinimizedToTray => Settings.StartMinimizedToTray;

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

    private InfoBarSeverity NotificationSeverity
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
        InfoBarSeverity.Success => "",
        InfoBarSeverity.Warning => "",
        InfoBarSeverity.Error => "",
        _ => ""
    };

    public Brush NotificationIconBrush => _notificationSeverity switch
    {
        InfoBarSeverity.Success => SuccessNotificationBrush,
        InfoBarSeverity.Warning => WarningNotificationBrush,
        InfoBarSeverity.Error => ErrorNotificationBrush,
        _ => InformationalNotificationBrush
    };

    public string AppVersionLabel =>
        $"Version {typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}";

    public Task InitializeAsync() => RefreshAsync();

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
        OnPropertyChanged(nameof(DisplaysNavigationForeground));
        OnPropertyChanged(nameof(SettingsNavigationForeground));
    }

    public void SetMonitorHotkey(MonitorViewModel monitor, string? hotkey)
    {
        var normalized = NormalizeHotkey(hotkey);
        var oldHotkey = monitor.Hotkey;
        monitor.SetHotkey(normalized);
        if (normalized is null)
        {
            Settings.MonitorHotkeys.Remove(monitor.Identity);
        }
        else
        {
            Settings.MonitorHotkeys[monitor.Identity] = normalized;
        }

        if (!TrySaveSettings())
        {
            monitor.SetHotkey(oldHotkey);
            RestoreMonitorHotkey(monitor.Identity, oldHotkey);
            return;
        }

        HotkeyConfigurationChanged?.Invoke();
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
            ShowNotification(InfoBarSeverity.Success, "Hotkeys reset", "All hotkeys are back to their defaults.");
        }
    }

    public void SetAllDisplaysHotkey(string? hotkey)
    {
        var normalized = NormalizeHotkey(hotkey);
        var oldHotkey = Settings.AllDisplaysHotkey;
        Settings.AllDisplaysHotkey = normalized;
        if (!TrySaveSettings())
        {
            Settings.AllDisplaysHotkey = oldHotkey;
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
        ShowNotification(InfoBarSeverity.Error, title, message);

    public void ReportWarning(string title, string message) =>
        ShowNotification(InfoBarSeverity.Warning, title, message);

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
                ShowNotification(InfoBarSeverity.Error, "Could not update display", applyError);
            }
            else
            {
                ShowNotification(
                    InfoBarSeverity.Success,
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

    public async Task ToggleAllDisplaysHdrAsync()
    {
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
                    InfoBarSeverity.Error,
                    "Some displays could not be updated",
                    string.Join(Environment.NewLine, errors));
            }
            else
            {
                ShowNotification(
                    InfoBarSeverity.Success,
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
                ShowNotification(InfoBarSeverity.Error, "Could not load displays", refreshError);
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
                    InfoBarSeverity.Warning,
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
            if (!Settings.MonitorHotkeys.ContainsKey(display.Identity))
            {
                Settings.MonitorHotkeys.Add(
                    display.Identity,
                    display.Status is HdrStatus.Enabled or HdrStatus.Disabled
                        ? supportedDisplayIndex < 9 ? $"Ctrl+Alt+{++supportedDisplayIndex}" : null
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
                InfoBarSeverity.Error,
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

    private void RestoreMonitorHotkey(string identity, string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Settings.MonitorHotkeys.Remove(identity);
        }
        else
        {
            Settings.MonitorHotkeys[identity] = hotkey;
        }
    }

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
            ShowNotification(InfoBarSeverity.Error, "Settings could not be saved", error.Message);
            return false;
        }
    }

    private static string AddError(string? current, string next) =>
        current is null ? next : $"{current}{Environment.NewLine}{next}";

    private void ShowNotification(InfoBarSeverity severity, string title, string message)
    {
        NotificationSeverity = severity;
        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationOpen = true;

        var version = ++_notificationVersion;
        if (severity == InfoBarSeverity.Success)
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
