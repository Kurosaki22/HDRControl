using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using HDRControl.App.Controls;
using HDRControl.App.Models;
using HDRControl.App.Services;
using HDRControl.App.ViewModels;

namespace HDRControl.App;

public sealed partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SystemTrayService _trayService;
    private readonly AppIconService _appIconService;
    private readonly IntPtr _windowHandle;
    private bool _isExitRequested;
    private bool _hasInitialized;
    private bool _isCapturingHotkey;
    private bool _isClosePromptOpen;
    private bool _trayAvailable;
    private TaskCompletionSource<DialogChoice>? _dialogResult;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(
            new DisplayConfigService(),
            new AppSettingsService(),
            new AutoStartService());
        DataContext = ViewModel;
        ApplyTheme();

        // The services need the window handle before the window is shown, e.g. for a start in the tray.
        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        _appIconService = new AppIconService(_windowHandle);
        _hotkeyService = new GlobalHotkeyService(_windowHandle);
        _hotkeyService.HotkeyPressed += HandleGlobalHotkey;
        _trayService = new SystemTrayService(_windowHandle, _appIconService.SmallIcon);
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

    public void Launch(bool isAutoStart)
    {
        if (isAutoStart && ViewModel.StartMinimizedToTray && _trayAvailable)
        {
            // Loaded does not fire until the window is shown, but the hotkeys must work from the tray.
            InitializeViewModel();
            return;
        }

        Show();
        Activate();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs args) => InitializeViewModel();

    private async void InitializeViewModel()
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        await ViewModel.InitializeAsync();
    }

    private void NotificationClose_Click(object sender, RoutedEventArgs args) =>
        ViewModel.DismissNotification();

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

    private void StartMinimizedOption_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox option)
        {
            ViewModel.SetStartMinimizedToTray(option.IsChecked == true);
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.SetUseDarkMode(!ViewModel.UseDarkMode);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (!SystemParameters.HighContrast)
        {
            App.ApplyTheme(ViewModel.UseDarkMode);
        }
    }

    private async void ResetHotkeys_Click(object sender, RoutedEventArgs args)
    {
        var choice = await ShowDialogAsync(
            "Reset all hotkeys?",
            "Every display goes back to its default hotkey. Hotkeys you set yourself are removed.",
            primary: "Reset hotkeys",
            secondary: null,
            close: "Cancel",
            defaultChoice: DialogChoice.Close);
        if (choice == DialogChoice.Primary)
        {
            ViewModel.ResetHotkeys();
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            // Create it so the button works before the first error was logged.
            Directory.CreateDirectory(ExceptionLog.LogDirectory);
            Process.Start("explorer.exe", $"\"{ExceptionLog.LogDirectory}\"");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ViewModel.ReportError("Could not open log folder", error.Message);
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

    private void HotkeyTextBox_KeyDown(object sender, KeyEventArgs args)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        args.Handled = true;
        // With Alt held, WPF reports the real key in SystemKey.
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyGesture.IsModifierKey(virtualKey))
        {
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            SetHotkey(textBox, null);
            return;
        }

        var gesture = HotkeyGesture.Capture(virtualKey);
        if (gesture is null)
        {
            ViewModel.ReportWarning(
                "Hotkey not recorded",
                "Use Ctrl, Alt, or Win together with a supported key. Backspace clears a hotkey.");
            return;
        }

        var target = IsAllDisplaysHotkey(textBox) ? null : textBox.DataContext as MonitorViewModel;
        // Hotkeys held by other apps never get here: Windows hands those keys to their owner.
        var owner = ViewModel.FindHotkeyOwner(gesture, target);
        if (owner is not null)
        {
            ViewModel.ReportWarning(
                "Hotkey already in use",
                $"{gesture.DisplayText} is already used by {owner}.");
            return;
        }

        SetHotkey(textBox, gesture.DisplayText);
    }

    private static bool IsAllDisplaysHotkey(TextBox textBox) =>
        string.Equals(textBox.Tag as string, "all-displays", StringComparison.Ordinal);

    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs args)
    {
        _isCapturingHotkey = true;
        _hotkeyService.UpdateRegistrations([], null);
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs args)
    {
        _isCapturingHotkey = false;
        RegisterGlobalHotkeys();
    }

    private void SetHotkey(TextBox textBox, string? hotkey)
    {
        if (IsAllDisplaysHotkey(textBox))
        {
            ViewModel.SetAllDisplaysHotkey(hotkey);
        }
        else if (textBox.DataContext is MonitorViewModel monitor)
        {
            ViewModel.SetMonitorHotkey(monitor, hotkey);
        }
    }

    private void RegisterGlobalHotkeys()
    {
        if (_isCapturingHotkey)
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
        // Another process may have hidden the window behind WPF's back; Hide first so Show really shows it.
        if (!IsWindowVisible(_windowHandle))
        {
            Hide();
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void QuitFromTray()
    {
        _isExitRequested = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs args)
    {
        if (!_isExitRequested)
        {
            args.Cancel = true;
            if (ViewModel.KeepInSystemTrayOnClose && _trayAvailable)
            {
                Hide();
                return;
            }

            if (!_isClosePromptOpen)
            {
                _isClosePromptOpen = true;
                // Closing must return before the dialog is awaited.
                Dispatcher.BeginInvoke(ShowCloseOptions);
            }

            return;
        }

        ViewModel.HotkeyConfigurationChanged -= RegisterGlobalHotkeys;
        _hotkeyService.Dispose();
        _trayService.Dispose();
        _appIconService.Dispose();
        Application.Current.Shutdown();
    }

    private async void ShowCloseOptions()
    {
        try
        {
            var choice = await ShowDialogAsync(
                "Close HDR Control?",
                _trayAvailable
                    ? "Would you like to quit the app or keep it running in the system tray?"
                    : "The system tray is unavailable. Would you like to quit or keep the app open?",
                primary: _trayAvailable ? "Keep in system tray" : "Quit HDR Control",
                secondary: "Stay open",
                close: _trayAvailable ? "Quit HDR Control" : null,
                defaultChoice: DialogChoice.Secondary);
            if (choice == DialogChoice.Primary && _trayAvailable)
            {
                Hide();
            }
            else if (choice == DialogChoice.Primary || choice == DialogChoice.Close)
            {
                _isExitRequested = true;
                Close();
            }
        }
        finally
        {
            _isClosePromptOpen = false;
        }
    }

    private Task<DialogChoice> ShowDialogAsync(
        string title,
        string message,
        string primary,
        string? secondary,
        string? close,
        DialogChoice defaultChoice)
    {
        _dialogResult?.TrySetResult(DialogChoice.None);
        _dialogResult = new TaskCompletionSource<DialogChoice>();
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        SetDialogButton(DialogPrimaryButton, primary, DialogChoice.Primary, defaultChoice);
        SetDialogButton(DialogSecondaryButton, secondary, DialogChoice.Secondary, defaultChoice);
        SetDialogButton(DialogCloseButton, close, DialogChoice.Close, defaultChoice);
        RootGrid.IsEnabled = false;
        DialogOverlay.Visibility = Visibility.Visible;
        DialogOverlay.PreviewKeyDown += DialogOverlay_PreviewKeyDown;

        var defaultButton = defaultChoice switch
        {
            DialogChoice.Primary => DialogPrimaryButton,
            DialogChoice.Secondary => DialogSecondaryButton,
            _ => DialogCloseButton
        };
        Dispatcher.BeginInvoke(() => defaultButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return _dialogResult.Task;
    }

    private static void SetDialogButton(Button button, string? text, DialogChoice choice, DialogChoice defaultChoice)
    {
        button.Content = text;
        button.Tag = choice;
        button.IsDefault = choice == defaultChoice;
        button.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DialogButton_Click(object sender, RoutedEventArgs args) =>
        CloseDialog((DialogChoice)((Button)sender).Tag);

    private void DialogOverlay_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            CloseDialog(DialogChoice.None);
        }
    }

    private void CloseDialog(DialogChoice choice)
    {
        DialogOverlay.PreviewKeyDown -= DialogOverlay_PreviewKeyDown;
        DialogOverlay.Visibility = Visibility.Collapsed;
        RootGrid.IsEnabled = true;
        _dialogResult?.TrySetResult(choice);
        _dialogResult = null;
    }

    private enum DialogChoice
    {
        None,
        Primary,
        Secondary,
        Close
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
