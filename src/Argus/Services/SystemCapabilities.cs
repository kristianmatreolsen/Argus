using System.Security.Principal;
using Microsoft.Win32;

namespace Argus.Services;

// What this session can read: low-level sensors (CPU temperature, motherboard fans) need administrator
// rights and the PawnIO driver, which LibreHardwareMonitor uses for port and register access.
public static class SystemCapabilities
{
    public static bool IsElevated { get; } = CheckElevated();

    public static bool IsPawnIoInstalled => CheckPawnIoInstalled();

    public const string PawnIoUrl = "https://pawnio.eu/";

    private static bool CheckElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CheckPawnIoInstalled()
    {
        try
        {
            using RegistryKey? service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return service is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
