using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using HidSharp;
using Argus.Models;

namespace Argus.Services;

public sealed class DeviceBatteryService
{
    private int? lastVerifiedSteelSeriesBattery;

    public IReadOnlyList<DeviceBattery> Read()
    {
        var devices = new List<DeviceBattery>();
        string selector = Battery.GetDeviceSelector();
        DeviceInformationCollection batteryDevices = DeviceInformation.FindAllAsync(selector).AsTask().GetAwaiter().GetResult();

        foreach (DeviceInformation device in batteryDevices)
        {
            TryAddWindowsBattery(devices, device);
        }

        AddPairedBluetoothDevices(devices, BluetoothDevice.GetDeviceSelectorFromPairingState(true));

        ReadBluetoothLeBatteries(devices);
        ReadSteelSeriesBattery(devices);

        return devices
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryAddWindowsBattery(List<DeviceBattery> devices, DeviceInformation device)
    {
        try
        {
            Battery battery = Battery.FromIdAsync(device.Id).AsTask().GetAwaiter().GetResult();
            BatteryReport report = battery.GetReport();
            int? remaining = report.RemainingCapacityInMilliwattHours;
            int? full = report.FullChargeCapacityInMilliwattHours;
            int? percentage = remaining is int remainingCapacity && full is int fullCapacity && fullCapacity > 0
                ? (int)Math.Round(remainingCapacity * 100d / fullCapacity)
                : null;

            AddOrReplaceDevice(devices, new DeviceBattery(GetDisplayName(device.Name, "Windows battery device"), percentage, true, "Connected"));
        }
        catch (Exception)
        {
            // Some battery providers disappear or reject access during enumeration.
        }
    }

    private static void AddPairedBluetoothDevices(List<DeviceBattery> devices, string selector)
    {
        DeviceInformationCollection pairedDevices = DeviceInformation.FindAllAsync(selector).AsTask().GetAwaiter().GetResult();
        foreach (DeviceInformation device in pairedDevices)
        {
            string name = GetDisplayName(device.Name, "Bluetooth device");
            AddOrReplaceDevice(devices, new DeviceBattery(name, null, true, "Battery unavailable"));
        }
    }

    private static void AddOrReplaceDevice(List<DeviceBattery> devices, DeviceBattery candidate)
    {
        int index = devices.FindIndex(existing => existing.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            devices[index] = candidate;
            return;
        }

        devices.Add(candidate);
    }

    private static void ReadBluetoothLeBatteries(List<DeviceBattery> devices)
    {
        DeviceInformationCollection pairedDevices = DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask().GetAwaiter().GetResult();

        foreach (DeviceInformation deviceInformation in pairedDevices)
        {
            try
            {
                using BluetoothLEDevice? device = BluetoothLEDevice.FromIdAsync(deviceInformation.Id).AsTask().GetAwaiter().GetResult();
                if (device is null)
                {
                    continue;
                }

                GattDeviceServicesResult services = device.GetGattServicesForUuidAsync(
                    GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
                GattDeviceService? batteryService = services.Services.FirstOrDefault();
                if (batteryService is null)
                {
                    continue;
                }

                GattCharacteristicsResult characteristics = batteryService.GetCharacteristicsForUuidAsync(
                    GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
                GattCharacteristic? batteryLevel = characteristics.Characteristics.FirstOrDefault();
                if (batteryLevel is null)
                {
                    continue;
                }

                GattReadResult reading = batteryLevel.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().GetAwaiter().GetResult();
                if (reading.Status != GattCommunicationStatus.Success || reading.Value is null)
                {
                    continue;
                }

                using DataReader reader = DataReader.FromBuffer(reading.Value);
                int percentage = reader.ReadByte();
                string name = GetDisplayName(device.Name, deviceInformation.Name, "Bluetooth device");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                AddOrReplaceDevice(devices, new DeviceBattery(name, percentage, true, "Connected"));
            }
            catch (Exception) when (!string.IsNullOrWhiteSpace(deviceInformation.Name))
            {
                // Some paired devices deny GATT access or do not expose the battery service.
            }
        }
    }

    private static string GetDisplayName(string? primary, string? fallback = null, string defaultValue = "Device")
    {
        string? candidate = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        return string.IsNullOrWhiteSpace(candidate) ? defaultValue : candidate.Trim();
    }

    private void ReadSteelSeriesBattery(List<DeviceBattery> devices)
    {
        foreach (HidDevice device in DeviceList.Local.GetHidDevices(0x1038, 0x2232))
        {
            const string name = "SteelSeries Arctis Nova 5";
            if (device.GetMaxFeatureReportLength() < 68)
            {
                AddOrReplaceDevice(devices, new DeviceBattery(name, null, true, "Battery unavailable"));
                continue;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using HidStream stream = device.Open();
                    byte[] feature = new byte[device.GetMaxFeatureReportLength()];
                    stream.GetFeature(feature);

                    if (feature[65] != 0xB7 || (feature[67] != 0x02 && feature[67] != 0x03) || feature[66] > 100)
                    {
                        continue;
                    }

                    lastVerifiedSteelSeriesBattery = feature[66];
                    AddOrReplaceDevice(devices, new DeviceBattery(name, feature[66], true, "Connected"));
                    return;
                }
                catch (Exception)
                {
                    // The headset may be busy with SteelSeries GG or deny this HID interface.
                }
            }

            string status = lastVerifiedSteelSeriesBattery is null ? "Battery unavailable" : "Last verified";
            AddOrReplaceDevice(devices, new DeviceBattery(name, lastVerifiedSteelSeriesBattery, true, status));
            return;
        }
    }
}