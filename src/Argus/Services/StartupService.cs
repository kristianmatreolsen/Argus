using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Argus.Services;

// Starts Argus at sign-in. When Argus runs as administrator it registers a Task Scheduler task with the
// highest privileges, so it starts elevated (and can read CPU temperature and fans) without a UAC prompt.
// Without administrator rights it can only use the per-user Run key, which always starts unelevated.
// The task is managed through the Task Scheduler COM API (not schtasks.exe): no temporary XML file that
// another process could swap before an elevated registration, and no console output to decode.
public static class StartupService
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

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
                removed &= DeleteTask();
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
        try
        {
            dynamic? folder = GetRootTaskFolder();
            if (folder is null)
            {
                return false;
            }

            string xml = folder.GetTask(TaskName).Xml;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string? command = XDocument.Parse(xml).Descendants(ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
            return IsCurrentExecutable(command);
        }
        catch (Exception exception) when (exception is FileNotFoundException or COMException or UnauthorizedAccessException)
        {
            // No task with this name (the COM binder reports it as FileNotFoundException), or no access to it.
            return false;
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("task-query", "Reading the Argus logon task failed", exception);
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

        try
        {
            dynamic? folder = GetRootTaskFolder();
            if (folder is null)
            {
                return false;
            }

            folder.RegisterTask(TaskName, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            AppLog.Info($"Registered the Argus logon task for {exePath}");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Registering the Argus logon task failed", exception);
            return false;
        }
    }

    private static bool DeleteTask()
    {
        try
        {
            dynamic? folder = GetRootTaskFolder();
            if (folder is null)
            {
                return false;
            }

            folder.DeleteTask(TaskName, 0);
            AppLog.Info("Removed the Argus logon task");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Removing the Argus logon task failed", exception);
            return false;
        }
    }

    private static dynamic? GetRootTaskFolder()
    {
        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null)
        {
            return null;
        }

        dynamic scheduler = Activator.CreateInstance(schedulerType)!;
        scheduler.Connect();
        return scheduler.GetFolder("\\");
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
