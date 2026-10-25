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

    // Startup state as last loaded or applied. Checking the real state means running schtasks.exe, which
    // is too slow for saves that happen often (e.g. remembering the pinned window position).
    private bool appliedStartWithWindows;

    public DisplaySettings Load()
    {
        DisplaySettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(settingsPath)) ?? new DisplaySettings();
        }
        catch (Exception)
        {
            settings = new DisplaySettings();
        }

        settings.StartWithWindows = appliedStartWithWindows = StartupService.IsStartupEnabled();
        return settings;
    }

    public bool Save(DisplaySettings settings)
    {
        // Only touch the startup registration when the user actually changed the setting, so saving layout
        // changes never rewrites or removes it.
        bool startupUpdated = true;
        if (settings.StartWithWindows != appliedStartWithWindows)
        {
            startupUpdated = StartupService.SetStartup(settings.StartWithWindows);
            if (!startupUpdated)
            {
                settings.StartWithWindows = StartupService.IsStartupEnabled();
            }

            appliedStartWithWindows = settings.StartWithWindows;
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