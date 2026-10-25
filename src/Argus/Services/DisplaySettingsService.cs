using System.Text.Json;
using System.IO;
using Argus.Models;

namespace Argus.Services;

public sealed class DisplaySettingsService
{
    private readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Argus",
        "settings.json");

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

    public bool Save(DisplaySettings settings)
    {
        // Only touch the Run key when the user actually changed the setting, so saving layout
        // changes never rewrites or removes the startup registration.
        bool startupUpdated = true;
        if (settings.StartWithWindows != StartupService.IsStartupEnabled())
        {
            startupUpdated = StartupService.SetStartup(settings.StartWithWindows);
            if (!startupUpdated)
            {
                settings.StartWithWindows = StartupService.IsStartupEnabled();
            }
        }

        string? directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return startupUpdated;
    }
}