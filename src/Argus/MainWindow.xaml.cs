using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Argus.Models;
using Argus.Services;

namespace Argus;

public partial class MainWindow : Window
{
    private readonly HardwareMonitorService hardwareMonitor;
    private readonly DeviceBatteryService deviceBattery;
    private readonly SystemInfoService systemInfo;
    private readonly DisplaySettings settings;
    private readonly DisplaySettingsService displaySettingsService = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SystemRefreshInterval = TimeSpan.FromSeconds(30);
    private bool closeFromApplication;
    private int refreshInProgress;
    private IReadOnlyList<DeviceBattery> cachedDevices = Array.Empty<DeviceBattery>();
    private SystemInfo? cachedSystem;
    private DateTime batteryRefreshedAtUtc;
    private DateTime systemRefreshedAtUtc;

    private Point _sectionStartPoint;
    private bool _isSectionMouseDown;
    private FrameworkElement? _draggedSectionHeader;

    private Point _cardStartPoint;
    private bool _isCardMouseDown;
    private FrameworkElement? _draggedCard;

    private const int MaxHistoryPoints = 30;
    private readonly List<double> gpuTempHistory = new();
    private readonly List<double> gpuLoadHistory = new();
    private readonly List<double> cpuTempHistory = new();
    private readonly List<double> cpuLoadHistory = new();
    private readonly List<double> memoryHistory = new();
    private readonly List<double> downloadHistory = new();
    private readonly List<double> uploadHistory = new();
    private readonly List<double> fpsHistory = new();
    private readonly List<double> fanHistory = new();

    public event Action<string>? TelemetryUpdated;
    public string CurrentTelemetry { get; private set; } = "Argus";

    public MainWindow(HardwareMonitorService hardwareMonitor, DeviceBatteryService deviceBattery, SystemInfoService systemInfo, DisplaySettings settings)
    {
        InitializeComponent();
        this.hardwareMonitor = hardwareMonitor;
        this.deviceBattery = deviceBattery;
        this.systemInfo = systemInfo;
        this.settings = settings;
        ApplyDisplaySettings();
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
        refreshTimer.Start();
    }

    public void Dispose()
    {
        refreshTimer.Stop();
    }

    public void ApplyDisplaySettings()
    {
        GpuTemperatureCard.Visibility = settings.ShowGpuTemperature ? Visibility.Visible : Visibility.Collapsed;
        GpuLoadCard.Visibility = settings.ShowGpuLoad ? Visibility.Visible : Visibility.Collapsed;
        CpuTemperatureCard.Visibility = settings.ShowCpuTemperature ? Visibility.Visible : Visibility.Collapsed;
        CpuLoadCard.Visibility = settings.ShowCpuLoad ? Visibility.Visible : Visibility.Collapsed;
        MemoryCard.Visibility = settings.ShowMemory ? Visibility.Visible : Visibility.Collapsed;
        NetworkCard.Visibility = settings.ShowNetwork ? Visibility.Visible : Visibility.Collapsed;
        FpsCard.Visibility = settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        FanSpeedCard.Visibility = settings.ShowFanSpeed ? Visibility.Visible : Visibility.Collapsed;

        SystemSection.Visibility = settings.ShowSystem ? Visibility.Visible : Visibility.Collapsed;
        DevicesSection.Visibility = settings.ShowDevices ? Visibility.Visible : Visibility.Collapsed;

        ApplySectionOrder();
        ApplyCardOrder();
    }

    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        Left = SystemParameters.WorkArea.Right - ActualWidth - 12;
        Top = Math.Max(SystemParameters.WorkArea.Top + 12, SystemParameters.WorkArea.Bottom - ActualHeight - 72);
        Activate();
    }

    public void CloseFromApplication()
    {
        closeFromApplication = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closeFromApplication)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref refreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            TelemetrySnapshot snapshot = await Task.Run(ReadTelemetry);
            ApplySnapshot(snapshot);
        }
        catch (Exception)
        {
            // Keep the tray app responsive if a device disappears during a refresh.
        }
        finally
        {
            Volatile.Write(ref refreshInProgress, 0);
        }
    }

    private TelemetrySnapshot ReadTelemetry()
    {
        DateTime now = DateTime.UtcNow;
        HardwareReading hardware = hardwareMonitor.Read();

        if (cachedSystem is null || now - systemRefreshedAtUtc >= SystemRefreshInterval)
        {
            cachedSystem = systemInfo.Read();
            systemRefreshedAtUtc = now;
        }

        if (cachedDevices.Count == 0 || now - batteryRefreshedAtUtc >= BatteryRefreshInterval)
        {
            cachedDevices = deviceBattery.Read();
            batteryRefreshedAtUtc = now;
        }

        return new TelemetrySnapshot(hardware, cachedDevices, cachedSystem!);
    }

    private void ApplySnapshot(TelemetrySnapshot snapshot)
    {
        HardwareReading hardware = snapshot.Hardware;
        IReadOnlyList<DeviceBattery> devices = snapshot.Devices;
        SystemInfo system = snapshot.System;

        // GPU TEMP
        GpuTempText.Text = FormatMetric(hardware.GpuTemperature, "°C");
        GpuTemperatureCard.ToolTip = hardware.GpuTemperature.HasValue
            ? $"GPU Core Temperature: {hardware.GpuTemperature.Value:0}°C"
            : "GPU Temperature: Sensor Unavailable";

        // GPU LOAD
        GpuLoadText.Text = hardware.GpuUsage.HasValue ? $"{hardware.GpuUsage.Value:0}%" : "--%";
        string vramInfo = (hardware.VramUsedGb.HasValue && hardware.VramTotalGb.HasValue)
            ? $"{hardware.VramUsedGb.Value:0.0} GB / {hardware.VramTotalGb.Value:0.0} GB Used"
            : (hardware.VramUsagePercent.HasValue ? $"{hardware.VramUsagePercent.Value:0}% Used" : "Unavailable");
        GpuLoadCard.ToolTip = $"GPU Core Load: {(hardware.GpuUsage.HasValue ? $"{hardware.GpuUsage.Value:0}%" : "--%")}\nDedicated VRAM: {vramInfo}";

        // CPU TEMP
        CpuTempText.Text = FormatMetric(hardware.CpuTemperature, "°C");
        CpuTemperatureCard.ToolTip = hardware.CpuTemperature.HasValue
            ? $"CPU Package Temperature: {hardware.CpuTemperature.Value:0}°C"
            : "CPU Temperature: Admin Access Required or Sensor Unavailable";

        // CPU LOAD
        CpuUsageText.Text = FormatMetric(hardware.CpuUsage, "%");
        CpuLoadCard.ToolTip = $"CPU Total Utilization: {hardware.CpuUsage:0}%";

        // MEMORY
        MemoryUsageText.Text = FormatMetric(hardware.MemoryUsagePercent, "%");
        MemoryCard.ToolTip = $"RAM Memory Usage: {hardware.MemoryUsagePercent:0}%\nDetailed RAM: {hardware.MemoryUsedGb:0.0} GB / {hardware.MemoryTotalGb:0.0} GB Used";

        // NETWORK
        NetDownText.Text = FormatRate(hardware.DownloadKilobytesPerSecond, "KB/s");
        NetUpText.Text = FormatRate(hardware.UploadKilobytesPerSecond, "KB/s");
        string linkSpeedStr = hardware.NetworkLinkSpeedText ?? "Active Network Adapter";
        NetworkCard.ToolTip = $"Network Connection • Link Capacity: {linkSpeedStr}\n↓ Download: {FormatRate(hardware.DownloadKilobytesPerSecond, "KB/s")}\n↑ Upload: {FormatRate(hardware.UploadKilobytesPerSecond, "KB/s")}";

        // FPS & FAN
        FpsText.Text = hardware.Fps.HasValue && hardware.Fps.Value > 0 ? $"{hardware.Fps.Value:0} FPS" : "-- FPS";
        FpsCard.ToolTip = hardware.Fps.HasValue && hardware.Fps.Value > 0
            ? $"3D Render Speed: {hardware.Fps.Value:0} FPS"
            : "Frames Per Second: No active 3D application detected";

        if (hardware.Fans.Count > 0)
        {
            FanText.Text = $"{hardware.Fans[0].Rpm:0} RPM";
            string fanDetails = string.Join("\n", hardware.Fans.Select(f => $"• {f.Name}: {f.Rpm:0} RPM"));
            FanSpeedCard.ToolTip = $"System Fans Detected ({hardware.Fans.Count}):\n{fanDetails}";
        }
        else
        {
            FanText.Text = FormatFan(hardware.FanRpm);
            FanSpeedCard.ToolTip = hardware.FanRpm.HasValue
                ? $"Cooling Fan Speed: {hardware.FanRpm.Value:0} RPM"
                : "Fan Speed: Sensor Unavailable";
        }

        // SYSTEM & DEVICES
        MachineText.Text = system.MachineName;
        UptimeText.Text = system.Uptime;
        DrivesList.ItemsSource = system.Drives;
        DeviceList.ItemsSource = devices;
        EmptyDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AddHistoryPoint(gpuTempHistory, hardware.GpuTemperature ?? 0);
        AddHistoryPoint(gpuLoadHistory, hardware.GpuUsage ?? 0);
        AddHistoryPoint(cpuTempHistory, hardware.CpuTemperature ?? 0);
        AddHistoryPoint(cpuLoadHistory, hardware.CpuUsage);
        AddHistoryPoint(memoryHistory, hardware.MemoryUsagePercent);
        AddHistoryPoint(downloadHistory, hardware.DownloadKilobytesPerSecond);
        AddHistoryPoint(uploadHistory, hardware.UploadKilobytesPerSecond);
        AddHistoryPoint(fpsHistory, hardware.Fps ?? 0);
        AddHistoryPoint(fanHistory, hardware.FanRpm ?? 0);

        RenderAllSparklines();

        CurrentTelemetry = BuildTelemetry(hardware, devices, system);
        TelemetryUpdated?.Invoke(CurrentTelemetry);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OpenSettingsWindow();
        }
    }

    // --- SECTION DRAG & DROP & EXPAND/COLLAPSE ---
    private void SectionHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement header)
        {
            _sectionStartPoint = e.GetPosition(this);
            _draggedSectionHeader = header;
            _isSectionMouseDown = true;
        }
    }

    private void SectionHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isSectionMouseDown && e.LeftButton == MouseButtonState.Pressed && _draggedSectionHeader is not null)
        {
            Point currentPoint = e.GetPosition(this);
            Vector diff = _sectionStartPoint - currentPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isSectionMouseDown = false;
                string? sectionName = _draggedSectionHeader.Tag as string;
                if (!string.IsNullOrEmpty(sectionName))
                {
                    var data = new DataObject("ArgusSection", sectionName);
                    DragDrop.DoDragDrop(_draggedSectionHeader, data, DragDropEffects.Move);
                }
            }
        }
    }

    private void SectionHeader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSectionMouseDown && sender is FrameworkElement header)
        {
            _isSectionMouseDown = false;
            string? sectionName = header.Tag as string;
            if (sectionName == "MetricsSection") ToggleMetrics();
            else if (sectionName == "SystemSection") ToggleSystem();
            else if (sectionName == "DevicesSection") ToggleDevices();
        }
    }

    private void SectionHeader_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ArgusSection"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void SectionHeader_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ArgusSection") && sender is FrameworkElement targetHeader)
        {
            string? sourceName = e.Data.GetData("ArgusSection") as string;
            string? targetName = targetHeader.Tag as string;

            if (!string.IsNullOrEmpty(sourceName) && !string.IsNullOrEmpty(targetName) && sourceName != targetName)
            {
                UIElement? sourceElem = MainSectionContainer.Children.OfType<FrameworkElement>().FirstOrDefault(el => el.Name == sourceName);
                UIElement? targetElem = MainSectionContainer.Children.OfType<FrameworkElement>().FirstOrDefault(el => el.Name == targetName);

                if (sourceElem is not null && targetElem is not null)
                {
                    int sourceIdx = MainSectionContainer.Children.IndexOf(sourceElem);
                    int targetIdx = MainSectionContainer.Children.IndexOf(targetElem);

                    MainSectionContainer.Children.RemoveAt(sourceIdx);
                    MainSectionContainer.Children.Insert(targetIdx, sourceElem);

                    settings.SectionOrder = MainSectionContainer.Children.OfType<FrameworkElement>()
                        .Select(el => el.Name)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();
                    displaySettingsService.Save(settings);
                }
            }
        }
    }

    private void ToggleMetrics()
    {
        if (MetricsGrid.Visibility == Visibility.Visible)
        {
            MetricsGrid.Visibility = Visibility.Collapsed;
            MetricsChevron.Text = "▼";
        }
        else
        {
            MetricsGrid.Visibility = Visibility.Visible;
            MetricsChevron.Text = "▲";
        }
    }

    private void ToggleSystem()
    {
        if (SystemContent.Visibility == Visibility.Visible)
        {
            SystemContent.Visibility = Visibility.Collapsed;
            SystemChevron.Text = "▼";
        }
        else
        {
            SystemContent.Visibility = Visibility.Visible;
            SystemChevron.Text = "▲";
        }
    }

    private void ToggleDevices()
    {
        if (DevicesContent.Visibility == Visibility.Visible)
        {
            DevicesContent.Visibility = Visibility.Collapsed;
            DevicesChevron.Text = "▼";
        }
        else
        {
            DevicesContent.Visibility = Visibility.Visible;
            DevicesChevron.Text = "▲";
        }
    }

    public void ApplySectionOrder()
    {
        if (settings.SectionOrder is null || settings.SectionOrder.Count == 0) return;

        var elements = MainSectionContainer.Children.OfType<FrameworkElement>().ToList();
        MainSectionContainer.Children.Clear();

        foreach (string name in settings.SectionOrder)
        {
            var match = elements.FirstOrDefault(el => el.Name == name);
            if (match is not null)
            {
                MainSectionContainer.Children.Add(match);
                elements.Remove(match);
            }
        }

        foreach (var remaining in elements)
        {
            MainSectionContainer.Children.Add(remaining);
        }
    }

    // --- CARD DRAG & DROP REORDER ---
    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement card)
        {
            _cardStartPoint = e.GetPosition(this);
            _draggedCard = card;
            _isCardMouseDown = true;
        }
    }

    private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isCardMouseDown && e.LeftButton == MouseButtonState.Pressed && _draggedCard is not null)
        {
            Point currentPoint = e.GetPosition(this);
            Vector diff = _cardStartPoint - currentPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isCardMouseDown = false;
                string cardName = _draggedCard.Name;
                if (!string.IsNullOrEmpty(cardName))
                {
                    var data = new DataObject("ArgusCard", cardName);
                    DragDrop.DoDragDrop(_draggedCard, data, DragDropEffects.Move);
                }
            }
        }
    }

    private void Card_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isCardMouseDown = false;
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ArgusCard"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ArgusCard") && sender is FrameworkElement targetCard)
        {
            string? sourceName = e.Data.GetData("ArgusCard") as string;
            string targetName = targetCard.Name;

            if (!string.IsNullOrEmpty(sourceName) && !string.IsNullOrEmpty(targetName) && sourceName != targetName)
            {
                UIElement? sourceElem = MetricsGrid.Children.OfType<FrameworkElement>().FirstOrDefault(el => el.Name == sourceName);
                UIElement? targetElem = MetricsGrid.Children.OfType<FrameworkElement>().FirstOrDefault(el => el.Name == targetName);

                if (sourceElem is not null && targetElem is not null)
                {
                    int sourceIdx = MetricsGrid.Children.IndexOf(sourceElem);
                    int targetIdx = MetricsGrid.Children.IndexOf(targetElem);

                    MetricsGrid.Children.RemoveAt(sourceIdx);
                    MetricsGrid.Children.Insert(targetIdx, sourceElem);

                    settings.CardOrder = MetricsGrid.Children.OfType<FrameworkElement>()
                        .Select(el => el.Name)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();
                    displaySettingsService.Save(settings);
                }
            }
        }
    }

    public void ApplyCardOrder()
    {
        if (settings.CardOrder is null || settings.CardOrder.Count == 0) return;

        var elements = MetricsGrid.Children.OfType<FrameworkElement>().ToList();
        MetricsGrid.Children.Clear();

        foreach (string name in settings.CardOrder)
        {
            var match = elements.FirstOrDefault(el => el.Name == name);
            if (match is not null)
            {
                MetricsGrid.Children.Add(match);
                elements.Remove(match);
            }
        }

        foreach (var remaining in elements)
        {
            MetricsGrid.Children.Add(remaining);
        }
    }

    private void AddHistoryPoint(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > MaxHistoryPoints)
        {
            history.RemoveAt(0);
        }
    }

    private void SparklineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderAllSparklines();
    }

    private void RenderAllSparklines()
    {
        double currentGpuTemp = gpuTempHistory.Count > 0 ? gpuTempHistory[^1] : 0;
        double currentGpuLoad = gpuLoadHistory.Count > 0 ? gpuLoadHistory[^1] : 0;
        double currentCpuTemp = cpuTempHistory.Count > 0 ? cpuTempHistory[^1] : 0;
        double currentCpuLoad = cpuLoadHistory.Count > 0 ? cpuLoadHistory[^1] : 0;
        double currentMem = memoryHistory.Count > 0 ? memoryHistory[^1] : 0;
        double currentFps = fpsHistory.Count > 0 ? fpsHistory[^1] : 0;
        double currentFan = fanHistory.Count > 0 ? fanHistory[^1] : 0;

        var (gpuTempStroke, gpuTempFill) = GetThresholdColors(currentGpuTemp, warningVal: 70, criticalVal: 82);
        var (gpuLoadStroke, gpuLoadFill) = GetThresholdColors(currentGpuLoad, warningVal: 75, criticalVal: 90);
        var (cpuTempStroke, cpuTempFill) = GetThresholdColors(currentCpuTemp, warningVal: 75, criticalVal: 88);
        var (cpuLoadStroke, cpuLoadFill) = GetThresholdColors(currentCpuLoad, warningVal: 75, criticalVal: 90);
        var (memStroke, memFill) = GetThresholdColors(currentMem, warningVal: 80, criticalVal: 92);

        RenderSparkline(GpuTempSparklineCanvas, gpuTempHistory, minVal: 0, maxVal: 100, unitLabel: "°C", strokeColor: gpuTempStroke, fillColor: gpuTempFill, warningThreshold: 70);
        RenderSparkline(GpuLoadSparklineCanvas, gpuLoadHistory, minVal: 0, maxVal: 100, unitLabel: "%", strokeColor: gpuLoadStroke, fillColor: gpuLoadFill, warningThreshold: 75);
        RenderSparkline(CpuTempSparklineCanvas, cpuTempHistory, minVal: 0, maxVal: 100, unitLabel: "°C", strokeColor: cpuTempStroke, fillColor: cpuTempFill, warningThreshold: 75);
        RenderSparkline(CpuLoadSparklineCanvas, cpuLoadHistory, minVal: 0, maxVal: 100, unitLabel: "%", strokeColor: cpuLoadStroke, fillColor: cpuLoadFill, warningThreshold: 75);
        RenderSparkline(MemorySparklineCanvas, memoryHistory, minVal: 0, maxVal: 100, unitLabel: "%", strokeColor: memStroke, fillColor: memFill, warningThreshold: 80);
        RenderDualSparkline(NetworkSparklineCanvas, downloadHistory, uploadHistory, downColor: Color.FromRgb(0x73, 0xBF, 0x69), upColor: Color.FromRgb(0xB8, 0x77, 0xD9));
        RenderSparkline(FpsSparklineCanvas, fpsHistory, minVal: 0, maxVal: Math.Max(currentFps * 1.25d, 144), unitLabel: " FPS", strokeColor: Color.FromRgb(0x38, 0xBD, 0xF8), fillColor: Color.FromArgb(0x35, 0x38, 0xBD, 0xF8));
        RenderSparkline(FanSparklineCanvas, fanHistory, minVal: 0, maxVal: Math.Max(currentFan * 1.2d, 2500), unitLabel: " RPM", strokeColor: Color.FromRgb(0x06, 0xB6, 0xD4), fillColor: Color.FromArgb(0x35, 0x06, 0xB6, 0xD4));
    }

    private static (Color stroke, Color fill) GetThresholdColors(double currentVal, double warningVal, double criticalVal)
    {
        if (currentVal >= criticalVal)
        {
            Color red = Color.FromRgb(0xF2, 0x49, 0x5C);
            return (red, Color.FromArgb(0x45, 0xF2, 0x49, 0x5C));
        }
        if (currentVal >= warningVal)
        {
            Color orange = Color.FromRgb(0xFF, 0x98, 0x30);
            return (orange, Color.FromArgb(0x45, 0xFF, 0x98, 0x30));
        }
        Color blue = Color.FromRgb(0x57, 0x94, 0xF2);
        return (blue, Color.FromArgb(0x35, 0x57, 0x94, 0xF2));
    }

    private static void RenderSparkline(Canvas canvas, List<double> history, double minVal, double maxVal, string unitLabel, Color strokeColor, Color fillColor, double? warningThreshold = null)
    {
        canvas.Children.Clear();
        if (history.Count < 2)
        {
            return;
        }

        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double range = maxVal - minVal;
        if (range <= 0)
        {
            range = 1;
        }

        const double paddingY = 2;
        double usableHeight = height - (paddingY * 2);

        // 1. Mid grid guide line
        double midY = height - paddingY - (0.5 * usableHeight);
        var gridLine = new Line
        {
            X1 = 0,
            Y1 = midY,
            X2 = width,
            Y2 = midY,
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            StrokeThickness = 0.8,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        };
        canvas.Children.Add(gridLine);

        // 2. Warning threshold line
        if (warningThreshold.HasValue)
        {
            double warnNorm = Math.Clamp((warningThreshold.Value - minVal) / range, 0, 1);
            double warnY = height - paddingY - (warnNorm * usableHeight);
            var warnLine = new Line
            {
                X1 = 0,
                Y1 = warnY,
                X2 = width,
                Y2 = warnY,
                Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0x98, 0x30)),
                StrokeThickness = 0.8,
                StrokeDashArray = new DoubleCollection { 3, 2 }
            };
            canvas.Children.Add(warnLine);
        }

        // 3. Max and Min Y-axis labels
        var maxText = new TextBlock
        {
            Text = $"{maxVal}{unitLabel}",
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontWeight = FontWeights.Bold
        };
        Canvas.SetRight(maxText, 2);
        Canvas.SetTop(maxText, 0);
        canvas.Children.Add(maxText);

        var minText = new TextBlock
        {
            Text = $"{minVal}{unitLabel}",
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontWeight = FontWeights.Bold
        };
        Canvas.SetRight(minText, 2);
        Canvas.SetBottom(minText, 0);
        canvas.Children.Add(minText);

        // 4. Area and trend line
        double stepX = width / (history.Count - 1);
        PointCollection linePoints = new();
        PointCollection areaPoints = new() { new Point(0, height) };

        for (int i = 0; i < history.Count; i++)
        {
            double norm = Math.Clamp((history[i] - minVal) / range, 0, 1);
            double x = i * stepX;
            double y = height - paddingY - (norm * usableHeight);
            Point pt = new(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);
        }

        areaPoints.Add(new Point(width, height));

        var polygon = new Polygon
        {
            Points = areaPoints,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(fillColor, 0.0),
                    new GradientStop(Color.FromArgb(0, fillColor.R, fillColor.G, fillColor.B), 1.0)
                }
            }
        };

        var polyline = new Polyline
        {
            Points = linePoints,
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round
        };

        canvas.Children.Add(polygon);
        canvas.Children.Add(polyline);
    }

    private static void RenderDualSparkline(Canvas canvas, List<double> history1, List<double> history2, Color downColor, Color upColor)
    {
        canvas.Children.Clear();
        int count = Math.Max(history1.Count, history2.Count);
        if (count < 2)
        {
            return;
        }

        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double max1 = history1.Count > 0 ? history1.Max() : 0;
        double max2 = history2.Count > 0 ? history2.Max() : 0;
        double peakObserved = Math.Max(max1, max2);
        double maxVal = Math.Max(peakObserved * 1.25d, 10d); // Minimum 10 KB/s scale

        // Grid guide line
        var gridLine = new Line
        {
            X1 = 0,
            Y1 = height * 0.5,
            X2 = width,
            Y2 = height * 0.5,
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            StrokeThickness = 0.8,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        };
        canvas.Children.Add(gridLine);

        // Dynamic scale max label placed at top right
        string maxTextStr = FormatRate(maxVal, "KB/s");
        var maxText = new TextBlock
        {
            Text = maxTextStr,
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            FontWeight = FontWeights.Bold
        };
        Canvas.SetRight(maxText, 2);
        Canvas.SetTop(maxText, 2);
        canvas.Children.Add(maxText);

        DrawSinglePolyline(canvas, history1, width, height, maxVal, downColor, Color.FromArgb(0x30, downColor.R, downColor.G, downColor.B), isDashed: false);
        DrawSinglePolyline(canvas, history2, width, height, maxVal, upColor, Color.FromArgb(0x20, upColor.R, upColor.G, upColor.B), isDashed: true);
    }

    private static void DrawSinglePolyline(Canvas canvas, List<double> history, double width, double height, double maxVal, Color strokeColor, Color fillColor, bool isDashed)
    {
        if (history.Count < 2)
        {
            return;
        }

        double stepX = width / (history.Count - 1);
        const double paddingY = 2;
        double usableHeight = height - (paddingY * 2);

        PointCollection linePoints = new();
        PointCollection areaPoints = new() { new Point(0, height) };

        for (int i = 0; i < history.Count; i++)
        {
            double norm = Math.Clamp(history[i] / maxVal, 0, 1);
            double x = i * stepX;
            double y = height - paddingY - (norm * usableHeight);
            Point pt = new(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);
        }

        areaPoints.Add(new Point(width, height));

        var polygon = new Polygon
        {
            Points = areaPoints,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(fillColor, 0.0),
                    new GradientStop(Color.FromArgb(0, fillColor.R, fillColor.G, fillColor.B), 1.0)
                }
            }
        };

        var polyline = new Polyline
        {
            Points = linePoints,
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round
        };

        if (isDashed)
        {
            polyline.StrokeDashArray = new DoubleCollection { 3, 2 };
        }

        canvas.Children.Add(polygon);
        canvas.Children.Add(polyline);
    }

    private sealed record TelemetrySnapshot(
        HardwareReading Hardware,
        IReadOnlyList<DeviceBattery> Devices,
        SystemInfo System);

    private static string FormatMetric(double? value, string suffix) => value is null ? "--" : $"{value:0}{suffix}";

    private static string FormatFan(double? value) => value is null or <= 0 ? "--" : $"{value:0} RPM";

    private static string FormatNetworkSpeed(double downloadKilobytesPerSecond, double uploadKilobytesPerSecond)
    {
        string download = FormatRate(downloadKilobytesPerSecond, "KB/s");
        string upload = FormatRate(uploadKilobytesPerSecond, "KB/s");
        return $"↓ {download}\n↑ {upload}";
    }

    private static string FormatRate(double value, string unit)
    {
        if (value >= 1024 * 1024)
        {
            return $"{value / (1024d * 1024d):0.#} GB/s";
        }

        if (value >= 1024)
        {
            double mb = value / 1024d;
            return mb >= 100 ? $"{mb:0} MB/s" : $"{mb:0.#} MB/s";
        }

        return $"{value:0} {unit}";
    }

    private string BuildTelemetry(HardwareReading hardware, IReadOnlyList<DeviceBattery> devices, SystemInfo system)
    {
        var telemetry = new System.Text.StringBuilder("Argus\n");
        var summary = new List<string>();
        if (settings.ShowCpuLoad)
        {
            summary.Add($"Load {FormatMetric(hardware.CpuUsage, "%")}");
        }

        if (settings.ShowMemory)
        {
            summary.Add($"Memory {FormatMetric(hardware.MemoryUsagePercent, "%")}");
        }

        if (summary.Count > 0)
        {
            telemetry.AppendLine(string.Join("  ", summary));
        }

        if (settings.ShowNetwork)
        {
            telemetry.AppendLine($"NET ↓ {FormatRate(hardware.DownloadKilobytesPerSecond, "KB/s")}  ↑ {FormatRate(hardware.UploadKilobytesPerSecond, "KB/s")}");
        }

        if (!settings.ShowDeviceBatteriesInTray || devices.Count == 0)
        {
            return telemetry.ToString().TrimEnd();
        }

        const int maxVisibleDeviceLines = 3;
        int displayed = 0;
        foreach (DeviceBattery device in devices)
        {
            if (displayed >= maxVisibleDeviceLines)
            {
                telemetry.AppendLine("...");
                break;
            }

            string name = FormatDeviceName(device.Name);
            telemetry.AppendLine($"{name}: {device.DisplayPercentage}");
            displayed++;
        }

        return telemetry.ToString().TrimEnd();
    }

    private static string FormatDeviceName(string name)
    {
        const int maxNameLength = 18;
        string trimmed = name.TrimEnd(':', '\\', '/');
        if (trimmed.Length <= maxNameLength)
        {
            return trimmed;
        }

        return string.Concat(trimmed.AsSpan(0, maxNameLength - 1), "…");
    }
}