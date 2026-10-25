namespace Argus.Models;

public sealed class DisplaySettings
{
    public bool ShowGpuTemperature { get; set; } = true;
    public bool ShowGpuLoad { get; set; } = true;
    public bool ShowCpuTemperature { get; set; } = true;
    public bool ShowCpuLoad { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowFps { get; set; } = true;
    public bool ShowFanSpeed { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowDevices { get; set; } = true;
    public bool ShowDeviceBatteriesInTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    // Pinned: the dashboard stays open as a normal window (e.g. on a second monitor) at the saved bounds,
    // instead of opening next to the tray and hiding when it loses focus.
    public bool IsPinned { get; set; }
    public double? PinnedLeft { get; set; }
    public double? PinnedTop { get; set; }
    public double? PinnedWidth { get; set; }
    public double? PinnedHeight { get; set; }

    // Keeps the pinned dashboard above other windows (e.g. a borderless game on another monitor).
    public bool PinnedOnTop { get; set; }

    // How much history the graphs show, in seconds: 30, 120, 300 or 600.
    public int GraphSpanSeconds { get; set; } = 30;

    // Temperature thresholds in °C. Warning turns the graph amber, critical red; staying at or above
    // critical for 30 seconds raises a notification when TemperatureAlerts is on.
    public double CpuTempWarning { get; set; } = 85;
    public double CpuTempCritical { get; set; } = 95;
    public double GpuTempWarning { get; set; } = 80;
    public double GpuTempCritical { get; set; } = 87;
    public bool TemperatureAlerts { get; set; } = true;

    // Custom fan names keyed by FanReading.Id.
    public Dictionary<string, string> FanNames { get; set; } = new();

    public List<string> SectionOrder { get; set; } = new() { "MetricsSection", "SystemSection", "DevicesSection" };
    public List<string> CardOrder { get; set; } = new()
    {
        "GpuTemperatureCard",
        "GpuLoadCard",
        "CpuTemperatureCard",
        "CpuLoadCard",
        "MemoryCard",
        "NetworkCard",
        "FpsCard",
        "FanSpeedCard"
    };
}