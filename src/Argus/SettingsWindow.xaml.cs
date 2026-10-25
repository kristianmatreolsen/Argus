using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Argus.Models;
using Argus.Services;

namespace Argus;

public partial class SettingsWindow : Window
{
    private readonly DisplaySettings settings;
    private bool updatingStartupCheckBox;

    public event Action? SettingsChanged;

    public SettingsWindow(DisplaySettings settings, IReadOnlyList<FanReading> fans)
    {
        InitializeComponent();
        this.settings = settings;
        FanNamesList.ItemsSource = fans
            .Select(fan => new FanNameRow(
                fan.Id,
                fan.Name,
                $"{fan.Rpm:0} {fan.Unit} now",
                settings.FanNames.TryGetValue(fan.Id, out string? name) ? name : string.Empty))
            .ToList();
        NoFansText.Visibility = fans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        StartupHintText.Text = SystemCapabilities.IsElevated
            ? "Starts with administrator rights through Task Scheduler, without a UAC prompt."
            : "Argus is not running as administrator, so it would start without CPU temperature and fan readings. Restart it as administrator first to start with full access.";

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
        if (updatingStartupCheckBox)
        {
            return;
        }

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

    public void SetStartWithWindows(bool enabled)
    {
        updatingStartupCheckBox = true;
        StartWithWindowsCheckBox.IsChecked = enabled;
        updatingStartupCheckBox = false;
    }

    private void FanNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string id } textBox)
        {
            return;
        }

        string name = textBox.Text.Trim();
        settings.FanNames.TryGetValue(id, out string? existing);
        if (name == (existing ?? string.Empty))
        {
            return;
        }

        if (name.Length == 0)
        {
            settings.FanNames.Remove(id);
        }
        else
        {
            settings.FanNames[id] = name;
        }

        SettingsChanged?.Invoke();
    }

    private void FanNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            FanNameTextBox_LostFocus(textBox, e);
            Keyboard.ClearFocus();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record FanNameRow(string Id, string DefaultName, string RpmText, string CustomName);
}