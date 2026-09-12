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