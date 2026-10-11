using System.Globalization;
using System.IO;
using Argus.Models;

namespace Argus.Services;

public sealed class SystemInfoService
{
    public SystemInfo Read()
    {
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string uptimeText = uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
            : $"{uptime.Hours}h {uptime.Minutes}m";

        IReadOnlyList<DriveFreeSpace> drives = GetDriveFreeSpace();

        return new SystemInfo(
            Environment.MachineName,
            uptimeText,
            drives);
    }

    private static IReadOnlyList<DriveFreeSpace> GetDriveFreeSpace()
    {
        var drives = new List<DriveFreeSpace>();
        DriveInfo[] availableDrives;
        try
        {
            availableDrives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return drives;
        }

        foreach (DriveInfo drive in availableDrives)
        {
            try
            {
                if (!drive.IsReady || string.IsNullOrWhiteSpace(drive.Name))
                {
                    continue;
                }

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
            catch (Exception)
            {
            }
        }

        return drives;
    }

}
