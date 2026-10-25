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