using System.Management;
using PulseTray.Models;

namespace PulseTray.Services;

public sealed class DeviceBatteryService
{
    public IReadOnlyList<DeviceBattery> Read()
    {
        var devices = new List<DeviceBattery>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, BatteryStatus, EstimatedChargeRemaining FROM Win32_Battery");

        foreach (ManagementObject battery in searcher.Get())
        {
            string name = battery["Name"]?.ToString() ?? "Windows battery device";
            int? percentage = battery["EstimatedChargeRemaining"] is uint value ? (int)value : null;
            bool connected = battery["BatteryStatus"] is uint status && status != 0;
            devices.Add(new DeviceBattery(name, percentage, connected, connected ? "Connected" : "Disconnected"));
        }

        return devices;
    }
}