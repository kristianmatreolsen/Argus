using System.Globalization;
using System.IO;
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
    private bool loading = true;

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
        PinnedOnTopCheckBox.IsChecked = settings.PinnedOnTop;
        TemperatureAlertsCheckBox.IsChecked = settings.TemperatureAlerts;
        foreach (RadioButton option in GraphSpanOptions.Children.OfType<RadioButton>())
        {
            option.IsChecked = option.Tag is string seconds && seconds == settings.GraphSpanSeconds.ToString(CultureInfo.InvariantCulture);
        }

        ShowThresholds();
        VersionRun.Text = $"Argus {App.Version}";
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
            StartWithWindowsCheckBox,
            PinnedOnTopCheckBox,
            TemperatureAlertsCheckBox
        })
        {
            checkBox.Checked += (_, _) => UpdateSettings();
            checkBox.Unchecked += (_, _) => UpdateSettings();
        }

        loading = false;
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
        settings.PinnedOnTop = PinnedOnTopCheckBox.IsChecked == true;
        settings.TemperatureAlerts = TemperatureAlertsCheckBox.IsChecked == true;
        SettingsChanged?.Invoke();
    }

    private void GraphSpanRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int seconds))
        {
            return;
        }

        settings.GraphSpanSeconds = seconds;
        SettingsChanged?.Invoke();
    }

    private void ShowThresholds()
    {
        CpuWarningTextBox.Text = settings.CpuTempWarning.ToString("0", CultureInfo.CurrentCulture);
        CpuCriticalTextBox.Text = settings.CpuTempCritical.ToString("0", CultureInfo.CurrentCulture);
        GpuWarningTextBox.Text = settings.GpuTempWarning.ToString("0", CultureInfo.CurrentCulture);
        GpuCriticalTextBox.Text = settings.GpuTempCritical.ToString("0", CultureInfo.CurrentCulture);
    }

    // Accepts whole degrees between 30 and 120 with warning below critical; anything else is reverted.
    private void ThresholdTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        bool valid =
            TryReadThreshold(CpuWarningTextBox, out double cpuWarning) &
            TryReadThreshold(CpuCriticalTextBox, out double cpuCritical) &
            TryReadThreshold(GpuWarningTextBox, out double gpuWarning) &
            TryReadThreshold(GpuCriticalTextBox, out double gpuCritical);
        if (!valid || cpuWarning >= cpuCritical || gpuWarning >= gpuCritical)
        {
            ShowThresholds();
            return;
        }

        if (cpuWarning == settings.CpuTempWarning && cpuCritical == settings.CpuTempCritical &&
            gpuWarning == settings.GpuTempWarning && gpuCritical == settings.GpuTempCritical)
        {
            return;
        }

        settings.CpuTempWarning = cpuWarning;
        settings.CpuTempCritical = cpuCritical;
        settings.GpuTempWarning = gpuWarning;
        settings.GpuTempCritical = gpuCritical;
        SettingsChanged?.Invoke();
    }

    private void ThresholdTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ThresholdTextBox_LostFocus(sender, e);
            Keyboard.ClearFocus();
        }
    }

    private static bool TryReadThreshold(TextBox textBox, out double value) =>
        double.TryParse(textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= 30 and <= 120;

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.Folder);
            System.Diagnostics.Process.Start("explorer.exe", AppLog.Folder);
        }
        catch (Exception exception)
        {
            AppLog.Warn("Opening the log folder failed", exception);
        }
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