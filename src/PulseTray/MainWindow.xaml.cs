using System.Windows;
using System.Windows.Threading;
using PulseTray.Models;
using PulseTray.Services;

namespace PulseTray;

public partial class MainWindow : Window
{
    private readonly HardwareMonitorService hardwareMonitor;
    private readonly DeviceBatteryService deviceBattery;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow(HardwareMonitorService hardwareMonitor, DeviceBatteryService deviceBattery)
    {
        InitializeComponent();
        this.hardwareMonitor = hardwareMonitor;
        this.deviceBattery = deviceBattery;
        refreshTimer.Tick += (_, _) => Refresh();
        Refresh();
        refreshTimer.Start();
    }

    public void Dispose()
    {
        refreshTimer.Stop();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Refresh()
    {
        HardwareReading hardware = hardwareMonitor.Read();
        IReadOnlyList<DeviceBattery> devices = deviceBattery.Read();

        CpuText.Text = FormatMetric(hardware.CpuTemperature, "°C");
        GpuText.Text = FormatMetric(hardware.GpuTemperature, "°C");
        UpdatedText.Text = DateTime.Now.ToString("HH:mm:ss");
        DeviceList.ItemsSource = devices;
        EmptyDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatMetric(double? value, string suffix) => value is null ? "--" : $"{value:0}{suffix}";
}