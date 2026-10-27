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
            settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(settingsPath)) ?? new DisplaySettings()
                : new DisplaySettings();
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Settings file {settingsPath} could not be read; using defaults", exception);
            settings = new DisplaySettings();
        }

        // A hand-edited file can contain "FanNames": null; the rest of the app expects a dictionary.
        settings.FanNames ??= new Dictionary<string, string>();
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

        try
        {
            string? directory = Path.GetDirectoryName(settingsPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            // Write a temporary file and swap it in, so a crash or power loss mid-write can't leave a
            // truncated settings file behind.
            string temporaryPath = settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("settings-save", $"Saving settings to {settingsPath} failed", exception);
        }

        return startupUpdated;
    }
}