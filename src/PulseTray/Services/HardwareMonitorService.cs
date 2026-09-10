using LibreHardwareMonitor.Hardware;

namespace PulseTray.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
        IsControllerEnabled = false,
        IsMotherboardEnabled = false
    };

    public HardwareMonitorService()
    {
        computer.Open();
    }

    public HardwareReading Read()
    {
        double? cpuTemperature = null;
        double? gpuTemperature = null;
        double cpuUsage = 0;
        double memoryUsage = 0;

        foreach (IHardware hardware in computer.Hardware)
        {
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not float value)
                {
                    continue;
                }

                if (sensor.SensorType == SensorType.Temperature && hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuTemperature = Max(cpuTemperature, value);
                }
                else if (sensor.SensorType == SensorType.Temperature &&
                         (hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel))
                {
                    gpuTemperature = Max(gpuTemperature, value);
                }
                else if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))
                {
                    cpuUsage = value;
                }
                else if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    memoryUsage = value;
                }
            }
        }

        return new HardwareReading(cpuTemperature, gpuTemperature, cpuUsage, memoryUsage);
    }

    public void Dispose() => computer.Close();

    private static double Max(double? current, float candidate) => Math.Max(current ?? double.MinValue, candidate);
}

public sealed record HardwareReading(double? CpuTemperature, double? GpuTemperature, double CpuUsage, double MemoryUsage);