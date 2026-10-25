using LibreHardwareMonitor.Hardware;
using Argus.Models;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Argus.Services;

public sealed class HardwareMonitorService : IDisposable
{
    // SMART data barely changes, and reading it on every 1-second refresh is wasteful.
    private static readonly TimeSpan StorageRefreshInterval = TimeSpan.FromSeconds(30);

    // VPN, virtual-machine and tunnel adapters carry traffic that also passes through the physical
    // adapter; counting them as well would double the network rate.
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual", "vpn", "tap-", "tap adapter", "wireguard", "wintun", "tunnel", "hyper-v", "vethernet",
        "vmware", "virtualbox", "npcap", "zerotier", "tailscale", "openvpn", "nordlynx", "loopback"
    };

    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        // RAM usage comes from GlobalMemoryStatusEx. LibreHardwareMonitor's memory module probes DIMM
        // sensors over SMBus, which on some boards (e.g. ASUS B850 + NCT6701D) makes the Super I/O fan
        // chip return garbage, so it stays off.
        IsMemoryEnabled = false,
        IsStorageEnabled = true,
        IsNetworkEnabled = false,
        IsControllerEnabled = true,
        IsMotherboardEnabled = true
    };
    private long previousReceivedBytes;
    private long previousSentBytes;
    private DateTime lastNetworkSampleUtc = DateTime.UtcNow;
    private DateTime storageUpdatedAtUtc = DateTime.MinValue;
    private readonly FrameRateService frameRateService = new();
    private readonly bool computerOpened;

    public HardwareMonitorService()
    {
        try
        {
            computer.Open();
            computerOpened = true;
            LogDetectedHardware();
        }
        catch (Exception exception)
        {
            computerOpened = false;
            AppLog.Error("LibreHardwareMonitor could not be opened; hardware sensors are unavailable", exception);
        }
    }

    public HardwareReading Read()
    {
        var readings = new SensorReadings();
        (double memoryUsagePercent, double memoryUsedGb, double memoryTotalGb) = GetMemoryDetails();
        NetworkSpeed networkSpeed = ReadNetworkSpeed();

        if (computerOpened)
        {
            bool updateStorage = DateTime.UtcNow - storageUpdatedAtUtc >= StorageRefreshInterval;
            if (updateStorage)
            {
                storageUpdatedAtUtc = DateTime.UtcNow;
            }

            try
            {
                foreach (IHardware hardware in computer.Hardware)
                {
                    bool update = hardware.HardwareType != HardwareType.Storage || updateStorage;
                    ReadHardware(hardware, update, readings);
                }
            }
            catch (Exception exception)
            {
                AppLog.WarnOnce("hardware-enumeration", "Enumerating hardware failed", exception);
            }
        }

        FrameRateReading frameRate = frameRateService.ReadFps();

        GpuSensors? gpu = SelectPrimaryGpu(readings.Gpus);
        double? vramUsedMb = gpu?.VramUsedMb;
        double? vramTotalMb = gpu?.VramTotalMb;
        if (vramUsedMb is null && vramTotalMb is not null && gpu?.VramFreeMb is double vramFreeMb)
        {
            vramUsedMb = vramTotalMb - vramFreeMb;
        }

        double? vramUsagePercent = gpu?.VramUsagePercent;
        if (vramUsagePercent is null && vramUsedMb is not null && vramTotalMb is > 0)
        {
            vramUsagePercent = (vramUsedMb / vramTotalMb) * 100d;
        }

        return new HardwareReading(
            readings.CpuTemperature,
            readings.CpuUsage,
            readings.CpuPower,
            gpu?.CoreTemperature ?? gpu?.OtherTemperature,
            gpu?.Load,
            gpu?.Power,
            vramUsedMb / 1024d,
            vramTotalMb / 1024d,
            vramUsagePercent,
            memoryUsagePercent,
            memoryUsedGb,
            memoryTotalGb,
            readings.Fans,
            readings.Disks,
            frameRate.Fps,
            frameRate.OnePercentLow,
            frameRate.Application,
            networkSpeed.DownloadKilobytesPerSecond,
            networkSpeed.UploadKilobytesPerSecond,
            networkSpeed.LinkSpeedText);
    }

    public void Dispose()
    {
        frameRateService.Dispose();
        if (computerOpened)
        {
            computer.Close();
        }
    }

    private void LogDetectedHardware()
    {
        foreach (IHardware hardware in computer.Hardware)
        {
            AppLog.Info($"Detected {hardware.HardwareType}: {hardware.Name}");
            foreach (IHardware child in hardware.SubHardware)
            {
                AppLog.Info($"Detected {child.HardwareType}: {child.Name} (on {hardware.Name})");
            }
        }
    }

    private static void ReadHardware(IHardware hardware, bool update, SensorReadings readings)
    {
        GpuSensors? gpu = null;
        if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            gpu = new GpuSensors(hardware.HardwareType);
            readings.Gpus.Add(gpu);
        }

        DiskSensors? disk = hardware.HardwareType == HardwareType.Storage ? new DiskSensors() : null;

        try
        {
            if (update)
            {
                hardware.Update();
            }

            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not float value)
                {
                    continue;
                }

                if (disk is not null)
                {
                    ReadDiskSensor(disk, sensor, value);
                }
                else if (sensor.SensorType == SensorType.Temperature && IsValidTemperature(value))
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        readings.CpuTemperature = Max(readings.CpuTemperature, value);
                    }
                    else if (hardware.HardwareType is HardwareType.Motherboard)
                    {
                        if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            readings.CpuTemperature = Max(readings.CpuTemperature, value);
                        }
                    }
                    else if (gpu is not null)
                    {
                        if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.CoreTemperature = Max(gpu.CoreTemperature, value);
                        }
                        else
                        {
                            gpu.OtherTemperature = Max(gpu.OtherTemperature, value);
                        }
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        if (sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))
                        {
                            readings.CpuUsage = value;
                        }
                    }
                    else if (gpu is not null)
                    {
                        if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.Load = value;
                        }
                        // "GPU Memory" is VRAM in use; "GPU Memory Controller" is bandwidth and must not match.
                        else if (sensor.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramUsagePercent = value;
                        }
                    }
                }
                else if (sensor.SensorType == SensorType.Power && value > 0)
                {
                    // Whole-package power ("Package" on AMD, "CPU Package" on Intel; "GPU Package" on NVIDIA),
                    // not the per-core or per-rail values.
                    if (hardware.HardwareType == HardwareType.Cpu &&
                        sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    {
                        readings.CpuPower = value;
                    }
                    else if (gpu is not null)
                    {
                        bool isTotal = sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                       sensor.Name.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
                                       sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase);
                        if (isTotal)
                        {
                            gpu.Power = value;
                            gpu.HasTotalPower = true;
                        }
                        else if (!gpu.HasTotalPower)
                        {
                            // No whole-GPU sensor (e.g. integrated GPUs): use the largest rail as an estimate.
                            gpu.Power = Math.Max(gpu.Power ?? 0, value);
                        }
                    }
                }
                else if (sensor.SensorType is SensorType.SmallData or SensorType.Data)
                {
                    if (gpu is not null)
                    {
                        // Prefer the driver-reported "GPU Memory *" values; D3D dedicated memory is the fallback
                        // (e.g. Intel GPUs only report the D3D figures).
                        if (sensor.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramUsedMb = value;
                        }
                        else if (sensor.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramTotalMb = value;
                        }
                        else if (sensor.Name.Equals("GPU Memory Free", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramFreeMb = value;
                        }
                        else if (sensor.Name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramUsedMb ??= value;
                        }
                        else if (sensor.Name.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase))
                        {
                            gpu.VramTotalMb ??= value;
                        }
                    }
                }
                else if (sensor.SensorType == SensorType.Fan && value > 0)
                {
                    // Short sensor names read better ("GPU Fan 1"); add the hardware name only to tell duplicates apart.
                    string fanLabel = readings.Fans.Any(f => f.Name == sensor.Name) ? $"{hardware.Name} {sensor.Name}" : sensor.Name;
                    if (!readings.Fans.Any(f => f.Name == fanLabel))
                    {
                        readings.Fans.Add(new FanReading(sensor.Identifier.ToString(), fanLabel, value));
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce($"hardware:{hardware.Identifier}", $"Reading {hardware.Name} failed", exception);
        }

        if (disk is { Temperature: not null } or { LifePercent: not null })
        {
            readings.Disks.Add(new DiskHealth(hardware.Name, disk.Temperature, disk.WarningTemperature, disk.CriticalTemperature, disk.LifePercent));
        }

        try
        {
            foreach (IHardware child in hardware.SubHardware)
            {
                ReadHardware(child, update, readings);
            }
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce($"subhardware:{hardware.Identifier}", $"Reading sub-hardware of {hardware.Name} failed", exception);
        }
    }

    // NVMe drives report "Composite Temperature" plus per-sensor values and the drive's own warning and
    // critical limits; SATA drives report "Temperature". Remaining life is "Life" (NVMe) or "Remaining Life".
    private static void ReadDiskSensor(DiskSensors disk, ISensor sensor, float value)
    {
        if (sensor.SensorType == SensorType.Temperature)
        {
            if (sensor.Name.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase))
            {
                disk.WarningTemperature = value;
            }
            else if (sensor.Name.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase))
            {
                disk.CriticalTemperature = value;
            }
            else if (IsValidTemperature(value) &&
                     (sensor.Name.Equals("Composite Temperature", StringComparison.OrdinalIgnoreCase) ||
                      sensor.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase) ||
                      disk.Temperature is null))
            {
                disk.Temperature = value;
            }
        }
        else if (sensor.SensorType == SensorType.Level &&
                 (sensor.Name.Equals("Life", StringComparison.OrdinalIgnoreCase) ||
                  sensor.Name.Equals("Remaining Life", StringComparison.OrdinalIgnoreCase)))
        {
            disk.LifePercent = value;
        }
    }

    // With an integrated and a dedicated GPU (most laptops, and desktops with an iGPU enabled), show the
    // dedicated one: it has the most dedicated memory (an iGPU only reserves a few hundred MB). Ties go to
    // NVIDIA, then AMD, then Intel.
    private static GpuSensors? SelectPrimaryGpu(List<GpuSensors> gpus)
    {
        return gpus
            .OrderByDescending(gpu => gpu.VramTotalMb ?? 0)
            .ThenBy(gpu => gpu.Type switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                _ => 2
            })
            .FirstOrDefault();
    }

    private sealed class SensorReadings
    {
        public double? CpuTemperature { get; set; }
        public double? CpuUsage { get; set; }
        public double? CpuPower { get; set; }
        public List<GpuSensors> Gpus { get; } = new();
        public List<FanReading> Fans { get; } = new();
        public List<DiskHealth> Disks { get; } = new();
    }

    private sealed class GpuSensors(HardwareType type)
    {
        public HardwareType Type { get; } = type;
        public double? CoreTemperature { get; set; }
        public double? OtherTemperature { get; set; }
        public double? Load { get; set; }
        public double? Power { get; set; }
        public bool HasTotalPower { get; set; }
        public double? VramUsagePercent { get; set; }
        public double? VramUsedMb { get; set; }
        public double? VramTotalMb { get; set; }
        public double? VramFreeMb { get; set; }
    }

    private sealed class DiskSensors
    {
        public double? Temperature { get; set; }
        public double? WarningTemperature { get; set; }
        public double? CriticalTemperature { get; set; }
        public double? LifePercent { get; set; }
    }

    private static bool IsValidTemperature(float value) => value >= 10 && value <= 130;

    private static double Max(double? current, float candidate) => Math.Max(current ?? double.MinValue, candidate);

    private NetworkSpeed ReadNetworkSpeed()
    {
        long totalReceived = 0;
        long totalSent = 0;
        long maxSpeedBps = 0;

        NetworkInterface[] networkInterfaces;
        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("network-interfaces", "Listing network adapters failed", exception);
            networkInterfaces = Array.Empty<NetworkInterface>();
        }

        foreach (NetworkInterface networkInterface in networkInterfaces)
        {
            try
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up || IsVirtualAdapter(networkInterface))
                {
                    continue;
                }

                if (networkInterface.Speed > maxSpeedBps && networkInterface.Speed < 100_000_000_000L)
                {
                    maxSpeedBps = networkInterface.Speed;
                }

                IPv4InterfaceStatistics statistics = networkInterface.GetIPv4Statistics();
                totalReceived += checked((long)statistics.BytesReceived);
                totalSent += checked((long)statistics.BytesSent);
            }
            catch (Exception exception)
            {
                AppLog.WarnOnce($"network:{networkInterface.Id}", $"Reading network adapter {networkInterface.Name} failed", exception);
            }
        }

        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = Math.Max((now - lastNetworkSampleUtc).TotalSeconds, 0.5d);
        // Totals drop when an adapter goes down; clamp so that sample reads as 0 instead of negative.
        double downloadKilobytesPerSecond = previousReceivedBytes == 0
            ? 0d
            : Math.Max(0d, (totalReceived - previousReceivedBytes) / elapsedSeconds / 1024d);
        double uploadKilobytesPerSecond = previousSentBytes == 0
            ? 0d
            : Math.Max(0d, (totalSent - previousSentBytes) / elapsedSeconds / 1024d);

        previousReceivedBytes = totalReceived;
        previousSentBytes = totalSent;
        lastNetworkSampleUtc = now;

        string? linkSpeedText = FormatLinkSpeed(maxSpeedBps);

        return new NetworkSpeed(downloadKilobytesPerSecond, uploadKilobytesPerSecond, linkSpeedText);
    }

    private static bool IsVirtualAdapter(NetworkInterface networkInterface)
    {
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        string description = networkInterface.Description + " " + networkInterface.Name;
        return VirtualAdapterKeywords.Any(keyword => description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FormatLinkSpeed(long speedBps)
    {
        if (speedBps <= 0)
        {
            return null;
        }

        if (speedBps >= 1_000_000_000)
        {
            return $"{speedBps / 1_000_000_000d:0.#} Gbps";
        }

        if (speedBps >= 1_000_000)
        {
            return $"{speedBps / 1_000_000d:0} Mbps";
        }

        return $"{speedBps / 1_000d:0} Kbps";
    }

    private static (double usagePercent, double usedGb, double totalGb) GetMemoryDetails()
    {
        MemoryStatus status = new();
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysicalMemory == 0)
        {
            return (0, 0, 0);
        }

        double totalGb = status.TotalPhysicalMemory / (1024d * 1024d * 1024d);
        double freeGb = status.AvailablePhysicalMemory / (1024d * 1024d * 1024d);
        double usedGb = totalGb - freeGb;
        double percent = (usedGb / totalGb) * 100d;

        return (percent, usedGb, totalGb);
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtualMemory;
        public ulong AvailableVirtualMemory;
        public ulong AvailableExtendedVirtualMemory;

        public MemoryStatus() => Length = (uint)Marshal.SizeOf<MemoryStatus>();
    }
}

public sealed record HardwareReading(
    double? CpuTemperature,
    double? CpuUsage,
    double? CpuPower,
    double? GpuTemperature,
    double? GpuUsage,
    double? GpuPower,
    double? VramUsedGb,
    double? VramTotalGb,
    double? VramUsagePercent,
    double MemoryUsagePercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    IReadOnlyList<FanReading> Fans,
    IReadOnlyList<DiskHealth> Disks,
    double Fps,
    double? FpsOnePercentLow,
    string? FpsApplication,
    double DownloadKilobytesPerSecond,
    double UploadKilobytesPerSecond,
    string? NetworkLinkSpeedText);

public sealed record NetworkSpeed(
    double DownloadKilobytesPerSecond,
    double UploadKilobytesPerSecond,
    string? LinkSpeedText);
