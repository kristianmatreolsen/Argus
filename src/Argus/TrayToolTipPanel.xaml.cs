using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Argus.Models;
using Argus.Services;

namespace Argus;

public partial class TrayToolTipPanel : UserControl
{
    private static readonly Brush LabelBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);
    private static readonly Brush ValueBrush = CreateFrozenBrush(0xF8, 0xFA, 0xFC);

    public TrayToolTipPanel()
    {
        InitializeComponent();
    }

    // Shows the metrics enabled in Settings: CPU and GPU (load · temperature), RAM, FPS while a game is
    // drawing frames, and device batteries when "Device battery percentages" is on.
    public void Update(HardwareReading hardware, IReadOnlyList<DeviceBattery> devices, DisplaySettings settings)
    {
        MetricsPanel.Children.Clear();
        if (settings.ShowCpuLoad || settings.ShowCpuTemperature)
        {
            AddMetric("CPU", JoinParts(
                settings.ShowCpuLoad ? Format(hardware.CpuUsage, "%") : null,
                settings.ShowCpuTemperature ? Format(hardware.CpuTemperature, "°C") : null));
        }

        if (settings.ShowGpuLoad || settings.ShowGpuTemperature)
        {
            AddMetric("GPU", JoinParts(
                settings.ShowGpuLoad ? Format(hardware.GpuUsage, "%") : null,
                settings.ShowGpuTemperature ? Format(hardware.GpuTemperature, "°C") : null));
        }

        if (settings.ShowMemory)
        {
            AddMetric("RAM", Format(hardware.MemoryUsagePercent, "%"));
        }

        if (settings.ShowFps && hardware.Fps > 0)
        {
            AddMetric("FPS", $"{hardware.Fps:0}", hardware.FpsApplication);
        }

        bool showDevices = settings.ShowDeviceBatteriesInTray && devices.Count > 0;
        DevicesList.ItemsSource = showDevices ? devices : null;
        DevicesList.Visibility = showDevices ? Visibility.Visible : Visibility.Collapsed;
        MetricsPanel.Visibility = MetricsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesDivider.Visibility = showDevices && MetricsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddMetric(string label, string value, string? detail = null)
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
        item.Children.Add(new TextBlock { Text = label, Foreground = LabelBrush, FontSize = 9.5, FontWeight = FontWeights.Bold });
        item.Children.Add(new TextBlock { Text = value, Foreground = ValueBrush, FontSize = 13, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrEmpty(detail))
        {
            item.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = LabelBrush,
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        MetricsPanel.Children.Add(item);
    }

    private static string JoinParts(params string?[] parts) => string.Join(" · ", parts.Where(part => part is not null));

    private static string Format(double? value, string suffix) => value is null ? "--" : $"{value:0}{suffix}";

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
