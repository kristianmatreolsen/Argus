using Microsoft.Win32;

namespace Argus.Services;

public static class StartupService
{
    private const string AppName = "Argus";
    private const string RunRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
            string? registeredPath = key?.GetValue(AppName)?.ToString()?.Trim().Trim('"');
            string? currentPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(registeredPath) || string.IsNullOrWhiteSpace(currentPath))
            {
                return false;
            }

            if (string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using RegistryKey? writableKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            writableKey?.DeleteValue(AppName, throwOnMissingValue: false);
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetStartup(bool enable)
    {
        try
        {
            if (!enable)
            {
                using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
                existingKey?.DeleteValue(AppName, throwOnMissingValue: false);
                return true;
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunRegistryKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(AppName, $"\"{exePath}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
