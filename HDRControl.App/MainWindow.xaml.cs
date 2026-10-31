using System.ComponentModel;
using System.Runtime.InteropServices;
using HDRControl.App.Controls;
using HDRControl.App.Models;
using HDRControl.App.Services;
using HDRControl.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Graphics;

namespace HDRControl.App;

public sealed partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SystemTrayService _trayService;
    private readonly AppIconService _appIconService;
    private bool _isExitRequested;
    private bool _hasInitialized;
    private bool _isCapturingShortcut;
    private bool _isClosePromptOpen;
    private bool _trayAvailable;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(
            new DisplayConfigService(),
            new AppSettingsService(),
            new AutoStartService());
        AppWindow.Resize(new SizeInt32(920, 680));
        AppWindow.Closing += AppWindow_Closing;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appIconService = new AppIconService(windowHandle);
        _hotkeyService = new GlobalHotkeyService(windowHandle);
        _hotkeyService.HotkeyPressed += HandleGlobalHotkey;
        _trayService = new SystemTrayService(windowHandle, _appIconService.SmallIcon);
        _trayService.OpenRequested += ShowFromTray;
        _trayService.QuitRequested += QuitFromTray;

        ViewModel.HotkeyConfigurationChanged += RegisterGlobalHotkeys;
        try
        {
            _trayService.SetVisible(true);
            _trayAvailable = true;
        }
        catch (Win32Exception error)
        {
            ViewModel.ReportError("System tray unavailable", error.Message);
        }

        RegisterGlobalHotkeys();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        await ViewModel.InitializeAsync();
    }

    private void Notification_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.DismissNotification();

    private void SettingsButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ShowSettingsPage();

    private void DisplaysNavigation_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ShowDisplaysPage();

    private void SettingsNavigation_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ShowSettingsPage();

    private void TrayOption_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox option)
        {
            ViewModel.SetKeepInSystemTrayOnClose(option.IsChecked == true);
        }
    }

    private void AutoStartOption_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox option)
        {
            ViewModel.SetStartWithWindows(option.IsChecked == true);
        }
    }

    private async void MonitorHdrToggle_Toggled(HdrPillToggle sender, bool isOn)
    {
        if (sender.DataContext is MonitorViewModel monitor && monitor.IsHdrEnabled != isOn)
        {
            await ViewModel.SetMonitorHdrEnabledAsync(monitor, isOn);
        }
    }

    private async void AllDisplaysHdrToggle_Toggled(HdrPillToggle sender, bool isOn)
    {
        if (ViewModel.IsAllDisplaysHdrEnabled != isOn)
        {
            await ViewModel.ToggleAllDisplaysHdrAsync();
        }
    }

    private void HotkeyTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        args.Handled = true;
        if (args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        if (args.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            SetShortcut(textBox, null);
            return;
        }

        var gesture = HotkeyGesture.Capture(args.Key);
        if (gesture is null)
        {
            ViewModel.ReportWarning(
                "Shortcut not recorded",
                "Use Ctrl, Alt, or Win together with a supported key. Backspace clears a shortcut.");
            return;
        }

        SetShortcut(textBox, gesture.DisplayText);
    }

    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs args)
    {
        _isCapturingShortcut = true;
        _hotkeyService.UpdateRegistrations([], null);
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs args)
    {
        _isCapturingShortcut = false;
        RegisterGlobalHotkeys();
    }

    private void SetShortcut(TextBox textBox, string? shortcut)
    {
        if (string.Equals(textBox.Tag as string, "all-displays", StringComparison.Ordinal))
        {
            ViewModel.SetAllDisplaysHotkey(shortcut);
        }
        else if (textBox.DataContext is MonitorViewModel monitor)
        {
            ViewModel.SetMonitorHotkey(monitor, shortcut);
        }
    }

    private void RegisterGlobalHotkeys()
    {
        if (_isCapturingShortcut)
        {
            return;
        }

        var warnings = _hotkeyService.UpdateRegistrations(
            ViewModel.Monitors,
            ViewModel.Settings.AllDisplaysHotkey);
        ViewModel.ShowHotkeyRegistrationWarnings(warnings);
    }

    private void HandleGlobalHotkey(string target)
    {
        if (target == GlobalHotkeyService.AllDisplaysHotkeyTarget)
        {
            _ = ViewModel.ToggleAllDisplaysHdrAsync();
            return;
        }

        var monitor = ViewModel.Monitors.FirstOrDefault(item => item.Identity == target);
        if (monitor is not null)
        {
            _ = ViewModel.SetMonitorHdrEnabledAsync(monitor, !monitor.IsHdrEnabled);
        }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void QuitFromTray()
    {
        _isExitRequested = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isExitRequested)
        {
            if (ViewModel.KeepInSystemTrayOnClose && _trayAvailable)
            {
                args.Cancel = true;
                AppWindow.Hide();
                return;
            }

            args.Cancel = true;
            if (!_isClosePromptOpen)
            {
                _isClosePromptOpen = true;
                if (!RootGrid.DispatcherQueue.TryEnqueue(ShowCloseOptions))
                {
                    _isClosePromptOpen = false;
                    ViewModel.ReportError(
                        "Could not open close options",
                        "The application could not show the close confirmation.");
                }
            }

            return;
        }

        ViewModel.HotkeyConfigurationChanged -= RegisterGlobalHotkeys;
        _hotkeyService.Dispose();
        _trayService.Dispose();
        _appIconService.Dispose();
    }

    private async void ShowCloseOptions()
    {
        try
        {
            var quitRequested = false;
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Close HDR Control?",
                Content = _trayAvailable
                    ? "Would you like to quit the app or keep it running in the system tray?"
                    : "The system tray is unavailable. Would you like to quit or keep the app open?",
                PrimaryButtonText = _trayAvailable ? "Keep in system tray" : "Quit HDR Control",
                SecondaryButtonText = "Stay open",
                CloseButtonText = _trayAvailable ? "Quit HDR Control" : string.Empty,
                DefaultButton = ContentDialogButton.Secondary,
                RequestedTheme = ElementTheme.Light
            };

            dialog.CloseButtonClick += (_, _) => quitRequested = true;
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (_trayAvailable)
                {
                    AppWindow.Hide();
                }
                else
                {
                    _isExitRequested = true;
                    Close();
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                return;
            }
            else if (quitRequested)
            {
                _isExitRequested = true;
                Close();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            ViewModel.ReportError("Could not show close options", error.Message);
        }
        finally
        {
            _isClosePromptOpen = false;
        }
    }
}
