using System.Globalization;
using System.IO;
using Argus.Models;

namespace Argus.Services;

public sealed class SystemInfoService
{
    public SystemInfo Read()
    {
        DriveInfo systemDrive = new(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        long freeSpaceGb = systemDrive.AvailableFreeSpace / (1024L * 1024L * 1024L);
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string uptimeText = uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
            : $"{uptime.Hours}h {uptime.Minutes}m";

        IReadOnlyList<DriveFreeSpace> drives = GetDriveFreeSpace();

        return new SystemInfo(
            Environment.MachineName,
            GetFriendlyOperatingSystemName(),
            uptimeText,
            $"{freeSpaceGb.ToString("N0", CultureInfo.InvariantCulture)} GB free",
            drives);
    }

    private static IReadOnlyList<DriveFreeSpace> GetDriveFreeSpace()
    {
        var drives = new List<DriveFreeSpace>();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady && !string.IsNullOrWhiteSpace(d.Name)))
        {
            string name = drive.Name.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            long freeSpaceGb = drive.AvailableFreeSpace / (1024L * 1024L * 1024L);
            long totalSizeGb = drive.TotalSize / (1024L * 1024L * 1024L);
            string driveLetter = name.TrimEnd(':', '\\', '/');
            drives.Add(new DriveFreeSpace(driveLetter, freeSpaceGb, totalSizeGb));
        }

        return drives;
    }

    private static string GetFriendlyOperatingSystemName()
    {
        Version version = Environment.OSVersion.Version;

        if (version.Major == 10 && version.Minor == 0)
        {
            return version.Build >= 22000 ? "Windows 11" : "Windows 10";
        }

        if (version.Major == 6 && version.Minor == 3)
        {
            return "Windows 8.1";
        }

        if (version.Major == 6 && version.Minor == 2)
        {
            return "Windows 8";
        }

        if (version.Major == 6 && version.Minor == 1)
        {
            return "Windows 7";
        }

        return $"Windows {version}";
    }
}
