using System.Text.Json;
using System.IO;
using Argus.Models;

namespace Argus.Services;

public sealed class DisplaySettingsService
{
    private readonly string settingsPath = GetSettingsPath();

    private static string GetSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string newPath = Path.Combine(localAppData, "Argus", "settings.json");
        string legacyPath = Path.Combine(localAppData, "PulseTray", "settings.json");

        if (!File.Exists(newPath) && File.Exists(legacyPath))
        {
            return legacyPath;
        }

        return newPath;
    }

    public DisplaySettings Load()
    {
        try
        {
            DisplaySettings settings = JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(settingsPath)) ?? new DisplaySettings();
            settings.StartWithWindows = StartupService.IsStartupEnabled();
            return settings;
        }
        catch (Exception)
        {
            return new DisplaySettings { StartWithWindows = StartupService.IsStartupEnabled() };
        }
    }

    public void Save(DisplaySettings settings)
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        StartupService.SetStartup(settings.StartWithWindows);
    }
}