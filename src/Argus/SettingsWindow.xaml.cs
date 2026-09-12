using System.Windows;
using Argus.Models;

namespace Argus;

public partial class SettingsWindow : Window
{
    private readonly DisplaySettings settings;

    public event Action? SettingsChanged;

    public SettingsWindow(DisplaySettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        GpuTemperatureCheckBox.IsChecked = settings.ShowGpuTemperature;
        GpuLoadCheckBox.IsChecked = settings.ShowGpuLoad;
        CpuTemperatureCheckBox.IsChecked = settings.ShowCpuTemperature;
        CpuLoadCheckBox.IsChecked = settings.ShowCpuLoad;
        MemoryCheckBox.IsChecked = settings.ShowMemory;
        NetworkCheckBox.IsChecked = settings.ShowNetwork;
        FpsCheckBox.IsChecked = settings.ShowFps;
        FanSpeedCheckBox.IsChecked = settings.ShowFanSpeed;
        SystemCheckBox.IsChecked = settings.ShowSystem;
        DevicesCheckBox.IsChecked = settings.ShowDevices;
        TrayBatteriesCheckBox.IsChecked = settings.ShowDeviceBatteriesInTray;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;

        foreach (var checkBox in new[]
        {
            GpuTemperatureCheckBox,
            GpuLoadCheckBox,
            CpuTemperatureCheckBox,
            CpuLoadCheckBox,
            MemoryCheckBox,
            NetworkCheckBox,
            FpsCheckBox,
            FanSpeedCheckBox,
            SystemCheckBox,
            DevicesCheckBox,
            TrayBatteriesCheckBox,
            StartWithWindowsCheckBox
        })
        {
            checkBox.Checked += (_, _) => UpdateSettings();
            checkBox.Unchecked += (_, _) => UpdateSettings();
        }
    }

    private void UpdateSettings()
    {
        settings.ShowGpuTemperature = GpuTemperatureCheckBox.IsChecked == true;
        settings.ShowGpuLoad = GpuLoadCheckBox.IsChecked == true;
        settings.ShowCpuTemperature = CpuTemperatureCheckBox.IsChecked == true;
        settings.ShowCpuLoad = CpuLoadCheckBox.IsChecked == true;
        settings.ShowMemory = MemoryCheckBox.IsChecked == true;
        settings.ShowNetwork = NetworkCheckBox.IsChecked == true;
        settings.ShowFps = FpsCheckBox.IsChecked == true;
        settings.ShowFanSpeed = FanSpeedCheckBox.IsChecked == true;
        settings.ShowSystem = SystemCheckBox.IsChecked == true;
        settings.ShowDevices = DevicesCheckBox.IsChecked == true;
        settings.ShowDeviceBatteriesInTray = TrayBatteriesCheckBox.IsChecked == true;
        settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        SettingsChanged?.Invoke();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}