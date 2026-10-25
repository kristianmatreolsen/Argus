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
    private readonly Dictionary<string, int> lastVerifiedHidBattery = new();

    public IReadOnlyList<DeviceBattery> Read()
    {
        var devices = new List<DeviceBattery>();
        try
        {
            string selector = Battery.GetDeviceSelector();
            DeviceInformationCollection batteryDevices = DeviceInformation.FindAllAsync(selector).AsTask().GetAwaiter().GetResult();
            foreach (DeviceInformation device in batteryDevices)
            {
                TryAddWindowsBattery(devices, device);
            }
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("battery-windows", "Listing Windows battery devices failed", exception);
        }

        try
        {
            AddPairedBluetoothDevices(devices, BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("battery-bluetooth", "Listing paired Bluetooth devices failed", exception);
        }

        try
        {
            ReadBluetoothLeBatteries(devices);
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("battery-ble", "Reading Bluetooth LE batteries failed", exception);
        }

        try
        {
            ReadHidBatteries(devices);
        }
        catch (Exception exception)
        {
            AppLog.WarnOnce("battery-hid", "Reading USB headset batteries failed", exception);
        }

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

            AddOrReplaceDevice(devices, new DeviceBattery(GetDisplayName(device.Name, "Windows battery device"), percentage, "Connected"));
        }
        catch (Exception)
        {
            // Some battery providers disappear or reject access during enumeration.
        }
    }

    // Lists paired devices that are connected right now; everything ever paired (old phones, car audio)
    // would otherwise show up as "Battery unavailable". Devices that don't report a connection state are kept.
    private static void AddPairedBluetoothDevices(List<DeviceBattery> devices, string selector)
    {
        const string isConnectedProperty = "System.Devices.Aep.IsConnected";
        DeviceInformationCollection pairedDevices;
        try
        {
            pairedDevices = DeviceInformation.FindAllAsync(selector, new[] { isConnectedProperty }).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            pairedDevices = DeviceInformation.FindAllAsync(selector).AsTask().GetAwaiter().GetResult();
        }

        foreach (DeviceInformation device in pairedDevices)
        {
            if (device.Properties.TryGetValue(isConnectedProperty, out object? connected) && connected is false)
            {
                continue;
            }

            string name = GetDisplayName(device.Name, "Bluetooth device");
            AddOrReplaceDevice(devices, new DeviceBattery(name, null, "Battery unavailable"));
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

                AddOrReplaceDevice(devices, new DeviceBattery(name, percentage, "Connected"));
            }
            catch (Exception)
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

    // Wired/USB-dongle headsets have no standard way to report battery over HID, so each supported model
    // is listed with the feature report layout it uses. Add an entry here to support another model.
    private static readonly HidBatteryModel[] KnownHidBatteryModels =
    {
        // Feature report: byte 65 = 0xB7 marker, byte 66 = battery %, byte 67 = 0x02/0x03 when connected.
        new(0x1038, 0x2232, "SteelSeries Arctis Nova 5", MinimumReportLength: 68, report =>
            report[65] == 0xB7 && report[67] is 0x02 or 0x03 && report[66] <= 100 ? report[66] : null)
    };

    private void ReadHidBatteries(List<DeviceBattery> devices)
    {
        foreach (HidBatteryModel model in KnownHidBatteryModels)
        {
            // A headset exposes several HID interfaces; use the first that answers with a valid report.
            HidDevice[] interfaces = DeviceList.Local.GetHidDevices(model.VendorId, model.ProductId).ToArray();
            if (interfaces.Length == 0)
            {
                continue;
            }

            string name = GetProductName(interfaces[0]) ?? model.Name;
            int? percentage = interfaces
                .Where(device => device.GetMaxFeatureReportLength() >= model.MinimumReportLength)
                .Select(device => TryReadHidBattery(device, model))
                .FirstOrDefault(value => value is not null);

            string key = $"{model.VendorId:X4}:{model.ProductId:X4}";
            if (percentage is not null)
            {
                lastVerifiedHidBattery[key] = percentage.Value;
                AddOrReplaceDevice(devices, new DeviceBattery(name, percentage, "Connected"));
            }
            else if (lastVerifiedHidBattery.TryGetValue(key, out int lastKnown))
            {
                AddOrReplaceDevice(devices, new DeviceBattery(name, lastKnown, "Last verified"));
            }
            else
            {
                AddOrReplaceDevice(devices, new DeviceBattery(name, null, "Battery unavailable"));
            }
        }
    }

    private static int? TryReadHidBattery(HidDevice device, HidBatteryModel model)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HidStream stream = device.Open();
                byte[] report = new byte[device.GetMaxFeatureReportLength()];
                stream.GetFeature(report);
                if (model.ParseBattery(report) is int percentage)
                {
                    return percentage;
                }
            }
            catch (Exception)
            {
                // The device may be busy with its vendor software or deny this HID interface.
            }
        }

        return null;
    }

    private static string? GetProductName(HidDevice device)
    {
        try
        {
            string name = device.GetProductName();
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record HidBatteryModel(int VendorId, int ProductId, string Name, int MinimumReportLength, Func<byte[], int?> ParseBattery);
}