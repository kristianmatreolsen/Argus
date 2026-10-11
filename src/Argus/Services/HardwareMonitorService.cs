using LibreHardwareMonitor.Hardware;
using Argus.Models;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
        IsControllerEnabled = true,
        IsMotherboardEnabled = true
    };
    private long previousReceivedBytes;
    private long previousSentBytes;
    private DateTime lastNetworkSampleUtc = DateTime.UtcNow;
    private readonly NvidiaFrameViewService nvidiaFrameView = new();
    private readonly FrameRateService frameRateService = new();
    private readonly System.Diagnostics.PerformanceCounter? fpsCounter;
    private readonly bool computerOpened;

    public HardwareMonitorService()
    {
        try
        {
            computer.Open();
            computerOpened = true;
        }
        catch (Exception)
        {
            computerOpened = false;
        }

        try
        {
            fpsCounter = new System.Diagnostics.PerformanceCounter(
                "DXGI Graphics",
                "Frames per second",
                "_Total",
                readOnly: true);
        }
        catch (Exception)
        {
            fpsCounter = null;
        }
    }

    public HardwareReading Read()
    {
        double? cpuTemperature = null;
        double? gpuTemperature = null;
        double? gpuCoreTemperature = null;
        double? gpuUsage = null;
        double? vramUsagePercent = null;
        double? vramUsedMb = null;
        double? vramTotalMb = null;
        double? vramFreeMb = null;
        double? fanRpm = null;
        double? fps = null;
        double? cpuUsage = null;
        var fans = new List<FanReading>();
        (double memoryUsagePercent, double memoryUsedGb, double memoryTotalGb) = GetMemoryDetails();
        NetworkSpeed networkSpeed = ReadNetworkSpeed();

        if (computerOpened)
        {
            try
            {
                foreach (IHardware hardware in computer.Hardware)
                {
                    ReadHardware(
                        hardware,
                        ref cpuTemperature,
                        ref gpuTemperature,
                        ref gpuCoreTemperature,
                        ref gpuUsage,
                        ref vramUsagePercent,
                        ref vramUsedMb,
                        ref vramTotalMb,
                        ref vramFreeMb,
                        ref fanRpm,
                        fans,
                        ref fps,
                        ref cpuUsage);
                }
            }
            catch (Exception)
            {
            }
        }

        cpuTemperature ??= ReadAcpiTemperature();
        cpuTemperature ??= ReadHwiNfoTemperature();
        double nvidiaFps = nvidiaFrameView.ReadFps();
        if (nvidiaFps > 0)
        {
            fps = nvidiaFps;
        }
        else
        {
            double presentMonFps = frameRateService.ReadFps();
            if (presentMonFps > 0)
            {
                fps = presentMonFps;
            }
        }

        fps ??= ReadPerformanceCounterFps();
        fps = Math.Max(0d, fps ?? 0d);

        double gpuFanRpm = nvidiaFrameView.ReadGpuFanRpm();
        if (gpuFanRpm > 0 && !fans.Any(fan => fan.Name.Equals("NVIDIA GPU Fan", StringComparison.OrdinalIgnoreCase)))
        {
            fans.Add(new FanReading("NVIDIA GPU Fan", gpuFanRpm));
            fanRpm = Max(fanRpm, (float)gpuFanRpm);
        }

        double? finalGpuTemp = gpuCoreTemperature ?? gpuTemperature;

        if (vramUsedMb is null && vramTotalMb is not null && vramFreeMb is not null)
        {
            vramUsedMb = vramTotalMb - vramFreeMb;
        }

        if (vramUsagePercent is null && vramUsedMb is not null && vramTotalMb is not null && vramTotalMb > 0)
        {
            vramUsagePercent = (vramUsedMb / vramTotalMb) * 100d;
        }

        double? vramUsedGb = vramUsedMb.HasValue ? vramUsedMb.Value / 1024d : null;
        double? vramTotalGb = vramTotalMb.HasValue ? vramTotalMb.Value / 1024d : null;

        return new HardwareReading(
            cpuTemperature,
            cpuUsage,
            finalGpuTemp,
            gpuUsage,
            vramUsedGb,
            vramTotalGb,
            vramUsagePercent,
            memoryUsagePercent,
            memoryUsedGb,
            memoryTotalGb,
            fanRpm,
            fans,
            fps,
            networkSpeed.DownloadKilobytesPerSecond,
            networkSpeed.UploadKilobytesPerSecond,
            networkSpeed.LinkSpeedText);
    }

    public void Dispose()
    {
        frameRateService.Dispose();
        nvidiaFrameView.Dispose();
        fpsCounter?.Dispose();
        if (computerOpened)
        {
            computer.Close();
        }
    }

    private static void ReadHardware(
        IHardware hardware,
        ref double? cpuTemperature,
        ref double? gpuTemperature,
        ref double? gpuCoreTemperature,
        ref double? gpuUsage,
        ref double? vramUsagePercent,
        ref double? vramUsedMb,
        ref double? vramTotalMb,
        ref double? vramFreeMb,
        ref double? fanRpm,
        List<FanReading> fans,
        ref double? fps,
        ref double? cpuUsage)
    {
        try
        {
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not float value)
                {
                    continue;
                }

                if (value >= 0 && (sensor.Name.Contains("FPS", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Frame Rate", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Framerate", StringComparison.OrdinalIgnoreCase)))
                {
                    fps = value;
                }

                if (sensor.SensorType == SensorType.Temperature && IsValidTemperature(value))
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        cpuTemperature = Max(cpuTemperature, value);
                    }
                    else if (hardware.HardwareType is HardwareType.Motherboard)
                    {
                        if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuTemperature = Max(cpuTemperature, value);
                        }
                    }
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                        {
                            gpuCoreTemperature = Max(gpuCoreTemperature, value);
                        }
                        else
                        {
                            gpuTemperature = Max(gpuTemperature, value);
                        }
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        if (sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))
                        {
                            cpuUsage = value;
                        }
                    }
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
                        {
                            gpuUsage = value;
                        }
                        else if (sensor.Name.Contains("GPU Memory", StringComparison.OrdinalIgnoreCase) ||
                                 sensor.Name.Contains("Memory Controller", StringComparison.OrdinalIgnoreCase))
                        {
                            vramUsagePercent = value;
                        }
                    }
                }
                else if (sensor.SensorType is SensorType.SmallData or SensorType.Data)
                {
                    if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        if (sensor.Name.Contains("GPU Memory Used", StringComparison.OrdinalIgnoreCase) ||
                            sensor.Name.Contains("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                        {
                            vramUsedMb = value;
                        }
                        else if (sensor.Name.Contains("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                        {
                            vramTotalMb = value;
                        }
                        else if (sensor.Name.Contains("GPU Memory Free", StringComparison.OrdinalIgnoreCase))
                        {
                            vramFreeMb = value;
                        }
                    }
                }
                else if (sensor.SensorType == SensorType.Fan && value > 0)
                {
                    fanRpm = Max(fanRpm, value);
                    string fanLabel = $"{hardware.Name} - {sensor.Name}";
                    if (!fans.Any(f => f.Name == fanLabel))
                    {
                        fans.Add(new FanReading(fanLabel, value));
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        try
        {
            foreach (IHardware child in hardware.SubHardware)
            {
                ReadHardware(
                    child,
                    ref cpuTemperature,
                    ref gpuTemperature,
                    ref gpuCoreTemperature,
                    ref gpuUsage,
                    ref vramUsagePercent,
                    ref vramUsedMb,
                    ref vramTotalMb,
                    ref vramFreeMb,
                    ref fanRpm,
                    fans,
                    ref fps,
                    ref cpuUsage);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool IsValidTemperature(float value) => value >= 10 && value <= 130;

    private static double Max(double? current, float candidate) => Math.Max(current ?? double.MinValue, candidate);

    private static double? ReadAcpiTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            double? temperature = null;
            foreach (ManagementObject zone in searcher.Get())
            {
                if (zone["CurrentTemperature"] is uint value)
                {
                    double celsius = value / 10d - 273.15d;
                    if (IsValidTemperature((float)celsius))
                    {
                        temperature = Max(temperature, (float)celsius);
                    }
                }
            }

            return temperature;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? ReadHwiNfoTemperature()
    {
        try
        {
            using MemoryMappedFile map = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            int length = checked((int)view.Capacity);
            byte[] data = new byte[length];
            view.ReadArray(0, data, 0, data.Length);

            byte[] label = Encoding.ASCII.GetBytes("CPU (Tctl/Tdie)");
            for (int labelOffset = 0; labelOffset <= data.Length - label.Length; labelOffset++)
            {
                if (!Matches(data, labelOffset, label))
                {
                    continue;
                }

                for (int valueOffset = labelOffset + 256; valueOffset <= labelOffset + 512 && valueOffset + sizeof(double) <= data.Length; valueOffset += sizeof(double))
                {
                    double value = BitConverter.ToDouble(data, valueOffset);
                    if (IsValidTemperature((float)value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static bool Matches(byte[] data, int offset, byte[] value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (data[offset + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private double? ReadPerformanceCounterFps()
    {
        if (fpsCounter is null)
        {
            return null;
        }

        try
        {
            float sample = fpsCounter.NextValue();
            return sample > 0 ? sample : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

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
        catch (Exception)
        {
            networkInterfaces = Array.Empty<NetworkInterface>();
        }

        foreach (NetworkInterface networkInterface in networkInterfaces)
        {
            try
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.OperationalStatus != OperationalStatus.Up)
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
            catch (Exception)
            {
            }
        }

        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = Math.Max((now - lastNetworkSampleUtc).TotalSeconds, 0.5d);
        double downloadKilobytesPerSecond = previousReceivedBytes == 0
            ? 0d
            : (totalReceived - previousReceivedBytes) / elapsedSeconds / 1024d;
        double uploadKilobytesPerSecond = previousSentBytes == 0
            ? 0d
            : (totalSent - previousSentBytes) / elapsedSeconds / 1024d;

        previousReceivedBytes = totalReceived;
        previousSentBytes = totalSent;
        lastNetworkSampleUtc = now;

        string? linkSpeedText = FormatLinkSpeed(maxSpeedBps);

        return new NetworkSpeed(downloadKilobytesPerSecond, uploadKilobytesPerSecond, linkSpeedText);
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
    double? GpuTemperature,
    double? GpuUsage,
    double? VramUsedGb,
    double? VramTotalGb,
    double? VramUsagePercent,
    double MemoryUsagePercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double? FanRpm,
    IReadOnlyList<FanReading> Fans,
    double? Fps,
    double DownloadKilobytesPerSecond,
    double UploadKilobytesPerSecond,
    string? NetworkLinkSpeedText);

public sealed record NetworkSpeed(
    double DownloadKilobytesPerSecond,
    double UploadKilobytesPerSecond,
    string? LinkSpeedText);