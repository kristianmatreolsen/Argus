using System.Globalization;

namespace Argus.Models;

public sealed record SystemSnapshot(
    DateTimeOffset CapturedAt,
    double? CpuTemperature,
    double? GpuTemperature,
    double CpuUsage,
    double MemoryUsage,
    IReadOnlyList<DeviceBattery> Devices);

public sealed record SystemInfo(
    string MachineName,
    string OperatingSystem,
    string Uptime,
    string SystemDriveFreeSpace,
    IReadOnlyList<DriveFreeSpace> Drives);

public sealed record DriveFreeSpace(string Name, long FreeSpaceGb, long TotalSizeGb)
{
    public string DisplayName => $"Drive {Name}:";

    public string FreeSpaceText => TotalSizeGb > 0
        ? $"{FreeSpaceGb.ToString("N0", CultureInfo.InvariantCulture)} GB free of {TotalSizeGb.ToString("N0", CultureInfo.InvariantCulture)} GB"
        : $"{FreeSpaceGb.ToString("N0", CultureInfo.InvariantCulture)} GB free";

    public int UsedPercentage => TotalSizeGb > 0
        ? (int)Math.Clamp(Math.Round((TotalSizeGb - FreeSpaceGb) * 100d / TotalSizeGb), 0, 100)
        : 0;

    public int FreePercentage => 100 - UsedPercentage;

    public string DisplayBadge => $"{FreePercentage}% free";

    public string PercentageBrush => UsedPercentage switch
    {
        > 90 => "#EF4444", // Red
        > 75 => "#F59E0B", // Amber
        _ => "#10B981"    // Green
    };

    public string BadgeBackground => UsedPercentage switch
    {
        > 90 => "#450A0A",
        > 75 => "#451A03",
        _ => "#064E3B"
    };

    public string BadgeBorder => UsedPercentage switch
    {
        > 90 => "#DC2626",
        > 75 => "#D97706",
        _ => "#059669"
    };
}

public sealed record DeviceBattery(string Name, int? Percentage, bool IsConnected, string Status)
{
    public string DisplayPercentage => Percentage is int value ? $"{value}%" : "--";

    public string PercentageBrush => Percentage switch
    {
        > 50 => "#10B981", // Green
        > 20 => "#F59E0B", // Amber
        _ when Percentage.HasValue => "#EF4444", // Red
        _ => "#94A3B8" // Muted gray for unknown
    };

    public string BadgeBackground => Percentage switch
    {
        > 50 => "#064E3B",
        > 20 => "#451A03",
        _ when Percentage.HasValue => "#450A0A",
        _ => "#1E293B"
    };

    public string BadgeBorder => Percentage switch
    {
        > 50 => "#059669",
        > 20 => "#D97706",
        _ when Percentage.HasValue => "#DC2626",
        _ => "#334155"
    };
}