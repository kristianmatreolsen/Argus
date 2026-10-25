using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
    private readonly DisplaySettingsService displaySettingsService;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer boundsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private const double PopupWidth = 420;
    private const double PopupHeight = 680;
    private DateTime hiddenByDeactivationUtc;
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SystemRefreshInterval = TimeSpan.FromSeconds(30);
    private bool closeFromApplication;
    private readonly bool pawnIoInstalledAtStartup = SystemCapabilities.IsPawnIoInstalled;
    private bool sensorAccessBannerDismissed;
    private SensorAccessAction sensorAccessAction;
    private int refreshInProgress;
    private int batteryRefreshInProgress;
    private volatile IReadOnlyList<DeviceBattery> cachedDevices = Array.Empty<DeviceBattery>();
    private SystemInfo? cachedSystem;
    private DateTime batteryRefreshedAtUtc;
    private DateTime systemRefreshedAtUtc;

    private Point _sectionStartPoint;
    private bool _isSectionMouseDown;
    private FrameworkElement? _draggedSectionHeader;

    private Point _cardStartPoint;
    private bool _isCardMouseDown;
    private FrameworkElement? _draggedCard;

    private static readonly Brush PrimaryTextBrush = CreateFrozenBrush(0xF8, 0xFA, 0xFC);
    private static readonly Brush MutedTextBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);
    private static readonly Brush AccentBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);

    // Warning / critical thresholds. Modern GPUs run 65–80°C under load and current desktop CPUs
    // boost up to ~95°C by design, so lower limits would flag normal gaming sessions.
    private const double GpuTempWarning = 80;
    private const double GpuTempCritical = 87;
    private const double CpuTempWarning = 85;
    private const double CpuTempCritical = 95;
    private const double CpuLoadWarning = 85;
    private const double CpuLoadCritical = 95;
    private const double MemoryWarning = 85;
    private const double MemoryCritical = 95;

    private const int MaxHistoryPoints = 30;
    private readonly List<double> gpuTempHistory = new();
    private readonly List<double> gpuLoadHistory = new();
    private readonly List<double> cpuTempHistory = new();
    private readonly List<double> cpuLoadHistory = new();
    private readonly List<double> memoryHistory = new();
    private readonly List<double> downloadHistory = new();
    private readonly List<double> uploadHistory = new();

    // Raised after every refresh with the values shown on the dashboard (used by the tray tooltip panel).
    public event Action<HardwareReading, IReadOnlyList<DeviceBattery>>? SnapshotApplied;

    // Latest fan readings with their sensor names, used by Settings to offer renaming.
    public IReadOnlyList<FanReading> CurrentFans { get; private set; } = Array.Empty<FanReading>();

    public MainWindow(
        HardwareMonitorService hardwareMonitor,
        DeviceBatteryService deviceBattery,
        SystemInfoService systemInfo,
        DisplaySettings settings,
        DisplaySettingsService displaySettingsService)
    {
        InitializeComponent();
        this.hardwareMonitor = hardwareMonitor;
        this.deviceBattery = deviceBattery;
        this.systemInfo = systemInfo;
        this.settings = settings;
        this.displaySettingsService = displaySettingsService;
        ApplyDisplaySettings();
        UpdatePinButton();
        boundsSaveTimer.Tick += (_, _) => SavePinnedBounds();
        LocationChanged += (_, _) => QueuePinnedBoundsSave();
        SizeChanged += (_, _) => QueuePinnedBoundsSave();
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

    // Tells the user why CPU temperature and motherboard fans show "--" and offers the fix, instead of
    // leaving the cards silently empty.
    private void UpdateSensorAccessBanner()
    {
        if (sensorAccessBannerDismissed)
        {
            SensorAccessBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool pawnIoInstalled = SystemCapabilities.IsPawnIoInstalled;
        if (!SystemCapabilities.IsElevated)
        {
            ShowSensorAccessBanner("CPU temperature and motherboard fans need administrator rights.", "Restart as admin", SensorAccessAction.RestartElevated);
        }
        else if (!pawnIoInstalled)
        {
            ShowSensorAccessBanner("CPU temperature and motherboard fans need the free PawnIO driver.", "Get PawnIO", SensorAccessAction.OpenPawnIoSite);
        }
        else if (!pawnIoInstalledAtStartup)
        {
            ShowSensorAccessBanner("PawnIO is installed. Restart Argus to start reading CPU temperature and fans.", "Restart", SensorAccessAction.RestartElevated);
        }
        else
        {
            SensorAccessBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSensorAccessBanner(string message, string buttonText, SensorAccessAction action)
    {
        SensorAccessText.Text = message;
        SensorAccessButton.Content = buttonText;
        sensorAccessAction = action;
        SensorAccessBanner.Visibility = Visibility.Visible;
    }

    private void SensorAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sensorAccessAction == SensorAccessAction.OpenPawnIoSite)
        {
            // Open the site through Explorer so the browser doesn't inherit Argus's administrator rights.
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", SystemCapabilities.PawnIoUrl);
            }
            catch (Exception)
            {
            }
        }
        else if (Application.Current is App app)
        {
            app.RestartAsAdministrator();
        }
    }

    private void DismissSensorAccessBanner_Click(object sender, RoutedEventArgs e)
    {
        sensorAccessBannerDismissed = true;
        SensorAccessBanner.Visibility = Visibility.Collapsed;
    }

    private enum SensorAccessAction
    {
        RestartElevated,
        OpenPawnIoSite
    }

    // --- WINDOW MODES ---
    // Popup (default): opens next to the tray and hides when it loses focus, like Windows' own tray flyouts.
    // Pinned: stays open where the user put it (e.g. on a second monitor), resizable, bounds remembered.

    public void ShowFromTray()
    {
        if (!settings.IsPinned || !TryShowPinned())
        {
            ShowNearTray();
        }
    }

    // Clicking the tray icon toggles the popup. That click first takes focus from the popup, which hides
    // it, so a click right after such a hide must not reopen it.
    public void ToggleFromTray()
    {
        if (!settings.IsPinned)
        {
            if (IsVisible)
            {
                Hide();
                return;
            }

            if (DateTime.UtcNow - hiddenByDeactivationUtc < TimeSpan.FromMilliseconds(400))
            {
                return;
            }
        }

        ShowFromTray();
    }

    // Opens the dashboard in the corner next to the notification area. The taskbar edge is found by
    // comparing the primary monitor's work area with its full size: the tray sits at the right end of a
    // top/bottom taskbar and at the bottom end of a left/right one.
    private void ShowNearTray()
    {
        UpdateSensorAccessBanner();
        const double margin = 12;
        Rect workArea = SystemParameters.WorkArea;
        ResizeMode = settings.IsPinned ? ResizeMode.CanResize : ResizeMode.NoResize;
        Width = PopupWidth;
        Height = PopupHeight;
        MaxHeight = workArea.Height - (margin * 2);

        Show();
        UpdateLayout();

        bool taskbarOnLeft = workArea.Left > 0;
        bool taskbarOnTop = workArea.Top > 0;
        Left = taskbarOnLeft ? workArea.Left + margin : workArea.Right - ActualWidth - margin;
        Top = taskbarOnTop ? workArea.Top + margin : workArea.Bottom - ActualHeight - margin;
        Activate();
    }

    // Returns false when there are no saved bounds or they are no longer on a connected monitor.
    private bool TryShowPinned()
    {
        if (settings.PinnedLeft is not double left || settings.PinnedTop is not double top)
        {
            return false;
        }

        double width = settings.PinnedWidth ?? PopupWidth;
        double height = settings.PinnedHeight ?? PopupHeight;
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.Contains(new Point(left + 40, top + 10)) || !virtualScreen.Contains(new Point(left + width - 40, top + 10)))
        {
            return false;
        }

        UpdateSensorAccessBanner();
        ResizeMode = ResizeMode.CanResize;
        MaxHeight = double.PositiveInfinity;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Show();
        Activate();
        return true;
    }

    private void SetPinned(bool pinned)
    {
        settings.IsPinned = pinned;
        UpdatePinButton();
        if (pinned)
        {
            ResizeMode = ResizeMode.CanResize;
            MaxHeight = double.PositiveInfinity;
            SavePinnedBounds();
        }
        else
        {
            displaySettingsService.Save(settings);
            ShowNearTray();
        }
    }

    private void UpdatePinButton()
    {
        PinButton.Content = settings.IsPinned ? "" : "";
        PinButton.Foreground = settings.IsPinned ? AccentBrush : MutedTextBrush;
        PinButton.ToolTip = settings.IsPinned
            ? "Unpin: open next to the tray and hide when clicking elsewhere"
            : "Pin: keep the dashboard open (e.g. on a second monitor)";
    }

    private void QueuePinnedBoundsSave()
    {
        if (settings.IsPinned && IsVisible && WindowState == WindowState.Normal)
        {
            boundsSaveTimer.Stop();
            boundsSaveTimer.Start();
        }
    }

    private void SavePinnedBounds()
    {
        boundsSaveTimer.Stop();
        if (WindowState == WindowState.Normal)
        {
            settings.PinnedLeft = Left;
            settings.PinnedTop = Top;
            settings.PinnedWidth = ActualWidth;
            settings.PinnedHeight = ActualHeight;
        }

        displaySettingsService.Save(settings);
    }

    // The window has no title bar, so the header moves it. Moving the popup pins it: the user evidently
    // wants it somewhere else. Buttons in the header handle their own clicks.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double left = Left;
        double top = Top;
        DragMove();
        if (Math.Abs(Left - left) > 4 || Math.Abs(Top - top) > 4)
        {
            if (settings.IsPinned)
            {
                SavePinnedBounds();
            }
            else
            {
                SetPinned(true);
            }
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!settings.IsPinned);

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !settings.IsPinned)
        {
            Hide();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (settings.IsPinned)
        {
            return;
        }

        // Let activation settle first: opening Settings from the dashboard activates another Argus window
        // and should not hide the dashboard.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!settings.IsPinned && IsVisible && !Application.Current.Windows.OfType<Window>().Any(window => window.IsActive))
            {
                Hide();
                hiddenByDeactivationUtc = DateTime.UtcNow;
            }
        });
    }

    // Windows 11 rounds the corners of borderless windows only when asked; Windows 10 ignores this.
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        const int DwmwaWindowCornerPreference = 33;
        int round = 2;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

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

        // Battery scans can take seconds (uncached Bluetooth LE reads), so they run on their own
        // task instead of delaying the 1-second hardware refresh.
        if (now - batteryRefreshedAtUtc >= BatteryRefreshInterval &&
            Interlocked.Exchange(ref batteryRefreshInProgress, 1) == 0)
        {
            batteryRefreshedAtUtc = now;
            _ = Task.Run(RefreshBatteries);
        }

        return new TelemetrySnapshot(hardware, cachedDevices, cachedSystem);
    }

    private void RefreshBatteries()
    {
        try
        {
            cachedDevices = deviceBattery.Read();
        }
        catch (Exception)
        {
            // Keep the previous device list if enumeration fails.
        }
        finally
        {
            Volatile.Write(ref batteryRefreshInProgress, 0);
        }
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
            : "CPU Temperature: Sensor Unavailable";

        // CPU LOAD
        CpuUsageText.Text = FormatMetric(hardware.CpuUsage, "%");
        CpuLoadCard.ToolTip = hardware.CpuUsage.HasValue
            ? $"CPU Total Utilization: {hardware.CpuUsage.Value:0}%"
            : "CPU Utilization: Sensor Unavailable";

        // MEMORY
        MemoryUsageText.Text = FormatMetric(hardware.MemoryUsagePercent, "%");
        MemoryCard.ToolTip = $"RAM Memory Usage: {hardware.MemoryUsagePercent:0}%\nDetailed RAM: {hardware.MemoryUsedGb:0.0} GB / {hardware.MemoryTotalGb:0.0} GB Used";

        // NETWORK
        NetDownText.Text = FormatRate(hardware.DownloadKilobytesPerSecond, "KB/s");
        NetUpText.Text = FormatRate(hardware.UploadKilobytesPerSecond, "KB/s");
        string linkSpeedStr = hardware.NetworkLinkSpeedText ?? "Active Network Adapter";
        NetworkCard.ToolTip = $"Network Connection • Link Capacity: {linkSpeedStr}\n↓ Download: {FormatRate(hardware.DownloadKilobytesPerSecond, "KB/s")}\n↑ Upload: {FormatRate(hardware.UploadKilobytesPerSecond, "KB/s")}";

        // FPS
        bool hasFps = hardware.Fps > 0;
        FpsText.Text = hasFps ? $"{hardware.Fps:0} FPS" : "--";
        FpsText.Foreground = hasFps ? PrimaryTextBrush : MutedTextBrush;
        FpsStatusText.Text = hasFps ? hardware.FpsApplication : "No game detected";
        FpsCard.ToolTip = hasFps
            ? $"{hardware.FpsApplication}: {hardware.Fps:0} displayed frames per second"
            : "The last app you used in the foreground is not drawing frames.";

        // FAN SPEED
        CurrentFans = hardware.Fans;
        IReadOnlyList<FanReading> fans = ApplyFanNames(hardware.Fans);
        if (fans.Count > 1)
        {
            FanText.Visibility = Visibility.Collapsed;
            FansList.ItemsSource = fans;
            FansScroll.Visibility = Visibility.Visible;
            string fanDetails = string.Join("\n", fans.Select(f => $"• {f.Name}: {f.Rpm:0} {f.Unit}"));
            FanSpeedCard.ToolTip = $"System Fans Detected ({fans.Count}):\n{fanDetails}";
        }
        else if (fans.Count == 1)
        {
            FansScroll.Visibility = Visibility.Collapsed;
            FanText.Visibility = Visibility.Visible;
            FanText.Text = $"{fans[0].Rpm:0} {fans[0].Unit}";
            FanSpeedCard.ToolTip = $"System Fan: {fans[0].Name} ({fans[0].Rpm:0} {fans[0].Unit})";
        }
        else
        {
            FansScroll.Visibility = Visibility.Collapsed;
            FanText.Visibility = Visibility.Visible;
            FanText.Text = "No active RPM";
            FanSpeedCard.ToolTip = "No fan tachometer is currently reporting a non-zero RPM value.";
        }

        // SYSTEM & DEVICES
        MachineText.Text = system.MachineName;
        UptimeText.Text = system.Uptime;
        DrivesList.ItemsSource = system.Drives;
        DeviceList.ItemsSource = devices;
        EmptyDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Only record real readings so an unavailable sensor doesn't draw a drop to zero.
        AddHistoryPoint(gpuTempHistory, hardware.GpuTemperature);
        AddHistoryPoint(gpuLoadHistory, hardware.GpuUsage);
        AddHistoryPoint(cpuTempHistory, hardware.CpuTemperature);
        AddHistoryPoint(cpuLoadHistory, hardware.CpuUsage);
        AddHistoryPoint(memoryHistory, hardware.MemoryUsagePercent);
        AddHistoryPoint(downloadHistory, hardware.DownloadKilobytesPerSecond);
        AddHistoryPoint(uploadHistory, hardware.UploadKilobytesPerSecond);

        RenderAllSparklines();

        SnapshotApplied?.Invoke(hardware, devices);
    }

    // Applies custom names, then lists motherboard fans before GPU fans, each group sorted by name, so
    // related fans (e.g. "CPU 1" and "CPU 2") sit together instead of following header order.
    private IReadOnlyList<FanReading> ApplyFanNames(IReadOnlyList<FanReading> fans)
    {
        return fans
            .Select(fan => settings.FanNames.TryGetValue(fan.Id, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? fan with { Name = name }
                : fan)
            .OrderBy(fan => fan.Id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase))
            .ThenBy(fan => fan.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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

    private void AddHistoryPoint(List<double> history, double? value)
    {
        if (value is null)
        {
            return;
        }

        history.Add(value.Value);
        if (history.Count > MaxHistoryPoints)
        {
            history.RemoveAt(0);
        }
    }

    // A ScrollViewer swallows the mouse wheel even when it has nothing to scroll; pass it on to the
    // dashboard in that case so the page still scrolls while the pointer is over the fan card.
    private void FansScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FansScroll.ScrollableHeight > 0 || FansScroll.Parent is not UIElement parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        });
    }

    private void SparklineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderAllSparklines();
    }

    private void RenderAllSparklines()
    {
        // Colour follows a short rolling average so a one-second spike doesn't flash the card amber.
        var (gpuTempStroke, gpuTempFill) = GetThresholdColors(RecentAverage(gpuTempHistory), GpuTempWarning, GpuTempCritical);
        var (cpuTempStroke, cpuTempFill) = GetThresholdColors(RecentAverage(cpuTempHistory), CpuTempWarning, CpuTempCritical);
        var (cpuLoadStroke, cpuLoadFill) = GetThresholdColors(RecentAverage(cpuLoadHistory), CpuLoadWarning, CpuLoadCritical);
        var (memStroke, memFill) = GetThresholdColors(RecentAverage(memoryHistory), MemoryWarning, MemoryCritical);
        // A fully loaded GPU while gaming is expected, so GPU load never changes colour.
        var (gpuLoadStroke, gpuLoadFill) = GetThresholdColors(0, double.PositiveInfinity, double.PositiveInfinity);

        var (gpuTempMin, gpuTempMax) = GetTemperatureRange(gpuTempHistory);
        var (cpuTempMin, cpuTempMax) = GetTemperatureRange(cpuTempHistory);

        RenderSparkline(GpuTempSparklineCanvas, gpuTempHistory, minVal: gpuTempMin, maxVal: gpuTempMax, strokeColor: gpuTempStroke, fillColor: gpuTempFill, warningThreshold: GpuTempWarning);
        RenderSparkline(GpuLoadSparklineCanvas, gpuLoadHistory, minVal: 0, maxVal: 100, strokeColor: gpuLoadStroke, fillColor: gpuLoadFill);
        RenderSparkline(CpuTempSparklineCanvas, cpuTempHistory, minVal: cpuTempMin, maxVal: cpuTempMax, strokeColor: cpuTempStroke, fillColor: cpuTempFill, warningThreshold: CpuTempWarning);
        RenderSparkline(CpuLoadSparklineCanvas, cpuLoadHistory, minVal: 0, maxVal: 100, strokeColor: cpuLoadStroke, fillColor: cpuLoadFill, warningThreshold: CpuLoadWarning);
        RenderSparkline(MemorySparklineCanvas, memoryHistory, minVal: 0, maxVal: 100, strokeColor: memStroke, fillColor: memFill, warningThreshold: MemoryWarning);
        RenderDualSparkline(NetworkSparklineCanvas, downloadHistory, uploadHistory, downColor: Color.FromRgb(0x73, 0xBF, 0x69), upColor: Color.FromRgb(0xB8, 0x77, 0xD9));
    }

    private static double RecentAverage(List<double> history)
    {
        const int window = 5;
        if (history.Count == 0)
        {
            return 0;
        }

        int start = Math.Max(0, history.Count - window);
        double sum = 0;
        for (int i = start; i < history.Count; i++)
        {
            sum += history[i];
        }

        return sum / (history.Count - start);
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

    // Temperatures rarely leave the 30–90°C band, so a fixed 0–100 scale flattens them. Follow the
    // observed values instead, but keep at least a 20°C span so sensor jitter doesn't fill the graph.
    private static (double min, double max) GetTemperatureRange(List<double> history)
    {
        const double minimumSpan = 20;
        if (history.Count == 0)
        {
            return (20, 100);
        }

        double low = history.Min() - 5;
        double high = history.Max() + 5;
        if (high - low < minimumSpan)
        {
            double center = (low + high) / 2;
            low = center - (minimumSpan / 2);
            high = center + (minimumSpan / 2);
        }

        return (Math.Max(0, Math.Floor(low)), Math.Ceiling(high));
    }

    private static void RenderSparkline(Canvas canvas, List<double> history, double minVal, double maxVal, Color strokeColor, Color fillColor, double? warningThreshold = null)
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

        // 1. Warning threshold line, only when it falls inside the visible range
        if (warningThreshold is double threshold && threshold > minVal && threshold < maxVal)
        {
            double warnNorm = (threshold - minVal) / range;
            double warnY = height - paddingY - (warnNorm * usableHeight);
            var warnLine = new Line
            {
                X1 = 0,
                Y1 = warnY,
                X2 = width,
                Y2 = warnY,
                Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x98, 0x30)),
                StrokeThickness = 0.8,
                StrokeDashArray = new DoubleCollection { 3, 2 }
            };
            canvas.Children.Add(warnLine);
        }

        // 2. Area and trend line
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
        // A 100 KB/s floor keeps idle background traffic from looking like activity.
        double maxVal = Math.Max(peakObserved * 1.25d, 100d);

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

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string FormatMetric(double? value, string suffix) => value is null ? "--" : $"{value:0}{suffix}";

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

        // One decimal for small rates so light traffic doesn't read as "0 KB/s".
        return value < 10 ? $"{value:0.0} {unit}" : $"{value:0} {unit}";
    }
}