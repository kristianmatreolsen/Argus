using System.Globalization;

namespace Argus.Models;

// Id is the sensor's stable identifier (e.g. "/lpc/nct6701d/0/fan/2"); custom fan names are keyed on it.
public sealed record FanReading(string Id, string Name, double Rpm, string Unit = "RPM");

public sealed record SystemInfo(
    string MachineName,
    string Uptime,
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

// A physical drive's SMART health. Colours follow the drive's own warning/critical temperature limits
// when it reports them (NVMe does), otherwise 60/70°C.
public sealed record DiskHealth(string Name, double? Temperature, double? WarningTemperature, double? CriticalTemperature, double? LifePercent)
{
    public string TemperatureText => Temperature is double value ? $"{value:0}°C" : "--";

    public string LifeText => LifePercent is double value ? $"{value:0}% life" : string.Empty;

    public string TemperatureBrush => Temperature switch
    {
        double value when value >= (CriticalTemperature ?? 70) => "#EF4444",
        double value when value >= (WarningTemperature ?? 60) => "#F59E0B",
        double => "#10B981",
        _ => "#94A3B8"
    };

    public string LifeBrush => LifePercent switch
    {
        < 20 => "#EF4444",
        < 50 => "#F59E0B",
        _ => "#94A3B8"
    };
}

public sealed record DeviceBattery(string Name, int? Percentage, string Status)
{
    public string DisplayPercentage => Percentage is int value ? $"{value}%" : "--";

    // "Connected" is the normal case, so only unusual states (e.g. "Last verified") are shown.
    public string StatusText => Status == "Connected" ? string.Empty : Status;

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