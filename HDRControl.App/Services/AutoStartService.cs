using Microsoft.Win32;

namespace HDRControl.App.Services;

public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "HDRControl";
    private const string LegacyAppValueName = "HdrSwitcher";

    public bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(AppValueName) is not null
            || runKey?.GetValue(LegacyAppValueName) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");

        if (!enabled)
        {
            runKey.DeleteValue(AppValueName, throwOnMissingValue: false);
            runKey.DeleteValue(LegacyAppValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        runKey.SetValue(AppValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        runKey.DeleteValue(LegacyAppValueName, throwOnMissingValue: false);
    }
}
