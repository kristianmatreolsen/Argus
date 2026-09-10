namespace PulseTray.Models;

public sealed record SystemSnapshot(
    DateTimeOffset CapturedAt,
    double? CpuTemperature,
    double? GpuTemperature,
    double CpuUsage,
    double MemoryUsage,
    IReadOnlyList<DeviceBattery> Devices);

public sealed record DeviceBattery(string Name, int? Percentage, bool IsConnected, string Status);