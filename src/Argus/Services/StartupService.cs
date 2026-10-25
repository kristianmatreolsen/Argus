using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Argus.Services;

// Starts Argus at sign-in. When Argus runs as administrator it registers a Task Scheduler task with the
// highest privileges, so it starts elevated (and can read CPU temperature and fans) without a UAC prompt.
// Without administrator rights it can only use the per-user Run key, which always starts unelevated.
public static class StartupService
{
    private const string AppName = "Argus";
    private const string TaskName = "Argus";
    private const string RunRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsStartupEnabled() => IsTaskRegistered() || IsRunKeyRegistered();

    public static bool SetStartup(bool enable)
    {
        if (!enable)
        {
            bool removed = RemoveRunKey();
            if (IsTaskRegistered())
            {
                removed &= RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _);
            }

            return removed;
        }

        if (SystemCapabilities.IsElevated && RegisterTask())
        {
            RemoveRunKey();
            return true;
        }

        return SetRunKey();
    }

    // A Run-key entry from an earlier unelevated session is replaced by the elevated task the first time
    // Argus runs as administrator, so the next sign-in already starts with full sensor access.
    public static void UpgradeToElevatedStartup()
    {
        if (SystemCapabilities.IsElevated && IsRunKeyRegistered() && !IsTaskRegistered() && RegisterTask())
        {
            RemoveRunKey();
        }
    }

    private static bool IsTaskRegistered()
    {
        if (!RunSchtasks($"/Query /TN \"{TaskName}\" /XML", out string xml))
        {
            return false;
        }

        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string? command = XDocument.Parse(xml).Descendants(ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
            return IsCurrentExecutable(command);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RegisterTask()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        string user;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            user = identity.Name;
        }

        // ExecutionTimeLimit PT0S and the battery settings stop Task Scheduler from killing Argus after
        // its default 3-day limit or when a laptop is unplugged.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Argus at sign-in with administrator rights so it can read CPU temperature and fan sensors.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(exePath)}"</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        string xmlPath = Path.Combine(Path.GetTempPath(), $"argus-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out _);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            })!;
            output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                AppLog.Warn($"schtasks {arguments} timed out");
                return false;
            }

            // "/Query" failing just means the task does not exist; only log changes that failed.
            if (process.ExitCode != 0 && !arguments.StartsWith("/Query", StringComparison.Ordinal))
            {
                AppLog.Warn($"schtasks {arguments} failed (exit code {process.ExitCode}): {error.Trim()}");
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Running schtasks {arguments} failed", exception);
            return false;
        }
    }

    private static bool IsRunKeyRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
            return IsCurrentExecutable(key?.GetValue(AppName)?.ToString()?.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SetRunKey()
    {
        try
        {
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
        catch (Exception exception)
        {
            AppLog.Warn("Adding Argus to the Run key failed", exception);
            return false;
        }
    }

    private static bool RemoveRunKey()
    {
        try
        {
            // Only remove an entry that points at this copy; another copy's entry belongs to that copy.
            if (!IsRunKeyRegistered())
            {
                return true;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // An entry pointing at another Argus copy belongs to that copy; report it as not enabled for this one.
    private static bool IsCurrentExecutable(string? registeredPath)
    {
        string? currentPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(registeredPath)
            && !string.IsNullOrWhiteSpace(currentPath)
            && string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase);
    }
}
