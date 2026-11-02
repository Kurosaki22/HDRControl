using System.Text.Json;
using HDRControl.App.Models;

namespace HDRControl.App.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // The UI tests use their own folder; there, settings are saved even with dummy displays.
    private static readonly string? TestSettingsDirectory =
        Environment.GetEnvironmentVariable("HDRCONTROL_SETTINGS_DIRECTORY") is { Length: > 0 } directory
            ? directory
            : null;

    private readonly string _settingsDirectory = TestSettingsDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HDRControl");
    private readonly string _legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HdrSwitcher",
        "settings.json");
    private string SettingsPath => Path.Combine(_settingsDirectory, "settings.json");

    public AppSettings Load()
    {
        var settingsPath = File.Exists(SettingsPath) || TestSettingsDirectory is not null
            ? SettingsPath
            : _legacySettingsPath;
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("The HDR Control settings file is empty.");
    }

    public void Save(AppSettings settings)
    {
        if (DummyDisplays.IsActive && TestSettingsDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(_settingsDirectory);

        var temporaryPath = $"{SettingsPath}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
