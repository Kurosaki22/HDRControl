using Microsoft.Win32;

namespace HDRControl.App.Services;

public sealed class AutoStartService
{
    public const string StartupArgument = "--autostart";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "HDRControl";
    private const string LegacyAppValueName = "HdrSwitcher";

    public bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(AppValueName) is not null
            || runKey?.GetValue(LegacyAppValueName) is not null;
    }

    // Only a dead path is replaced, so a debug build or a test run never takes over the installed app's entry.
    public void RepairPath()
    {
        // The UI tests set this; a test run must never rewrite the user's startup entry.
        if (Environment.GetEnvironmentVariable("HDRCONTROL_SETTINGS_DIRECTORY") is { Length: > 0 })
        {
            return;
        }

        string? command;
        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            command = (runKey?.GetValue(AppValueName) ?? runKey?.GetValue(LegacyAppValueName)) as string;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var path = command.StartsWith('"')
            ? command[1..].Split('"')[0]
            : command.Split(" --")[0].Trim();
        if (!File.Exists(path))
        {
            SetEnabled(true);
        }
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
        runKey.SetValue(AppValueName, $"\"{executablePath}\" {StartupArgument}", RegistryValueKind.String);
        runKey.DeleteValue(LegacyAppValueName, throwOnMissingValue: false);
    }
}
