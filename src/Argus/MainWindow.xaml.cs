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
    private TelemetrySnapshot? latestSnapshot;
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

    // Load and memory thresholds; temperature thresholds are user settings (DisplaySettings).
    private const double CpuLoadWarning = 85;
    private const double CpuLoadCritical = 95;
    private const double MemoryWarning = 85;
    private const double MemoryCritical = 95;

    // One point per second for up to the longest graph span (10 minutes). Graphs average the visible span
    // down to at most MaxPlotPoints so long spans stay smooth and cheap to draw.
    private const int MaxHistoryPoints = 600;
    private const int MaxPlotPoints = 120;
    private readonly List<double> fpsHistory = new();

    // Highest temperatures since Argus started.
    private double? cpuTempPeak;
    private double? gpuTempPeak;

    // A temperature must stay at or above critical (5-second average) this long before a notification,
    // and the same component is not reported again within the cooldown.
    private static readonly TimeSpan AlertDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(10);
    private readonly TemperatureAlertState cpuAlert = new();
    private readonly TemperatureAlertState gpuAlert = new();
    private readonly List<double> gpuTempHistory = new();
    private readonly List<double> gpuLoadHistory = new();
    private readonly List<double> cpuTempHistory = new();
    private readonly List<double> cpuLoadHistory = new();
    private readonly List<double> memoryHistory = new();
    private readonly List<double> downloadHistory = new();
    private readonly List<double> uploadHistory = new();

    // Raised after every refresh with the values shown on the dashboard (used by the tray tooltip panel).
    public event Action<HardwareReading, IReadOnlyList<DeviceBattery>>? SnapshotApplied;

    // Raised with a title and message when a temperature has stayed critical (shown as a notification).
    public event Action<string, string>? AlertRaised;

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
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && latestSnapshot is not null)
            {
                UpdateDashboard(latestSnapshot);
            }
        };
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
        ApplyTopmost();
        RenderAllSparklines();
    }

    // "Keep on top" only applies while pinned; the popup hides as soon as it loses focus anyway.
    private void ApplyTopmost() => Topmost = settings.IsPinned && settings.PinnedOnTop;

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
        ApplyTopmost();
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
        ApplyTopmost();
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
        // DragMove throws when the button is no longer down (fast clicks, pen and touch input).
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        double left = Left;
        double top = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
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
        catch (Exception exception)
        {
            // Keep the tray app responsive if a device disappears during a refresh.
            AppLog.WarnOnce("refresh", "Refreshing the dashboard failed", exception);
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
        catch (Exception exception)
        {
            // Keep the previous device list if enumeration fails.
            AppLog.WarnOnce("battery-refresh", "Reading device batteries failed", exception);
        }
        finally
        {
            Volatile.Write(ref batteryRefreshInProgress, 0);
        }
    }

    // Every refresh updates the history, peaks, alerts and tray tooltip. The dashboard's text and graphs are
    // only updated while it is visible; when it opens, it is drawn from the latest snapshot right away.
    private void ApplySnapshot(TelemetrySnapshot snapshot)
    {
        RecordSnapshot(snapshot);
        latestSnapshot = snapshot;
        if (IsVisible)
        {
            UpdateDashboard(snapshot);
        }

        SnapshotApplied?.Invoke(snapshot.Hardware, snapshot.Devices);
    }

    private void RecordSnapshot(TelemetrySnapshot snapshot)
    {
        HardwareReading hardware = snapshot.Hardware;
        CurrentFans = hardware.Fans;
        gpuTempPeak = Peak(gpuTempPeak, hardware.GpuTemperature);
        cpuTempPeak = Peak(cpuTempPeak, hardware.CpuTemperature);

        // Only record real readings so an unavailable sensor doesn't draw a drop to zero.
        AddHistoryPoint(gpuTempHistory, hardware.GpuTemperature);
        AddHistoryPoint(gpuLoadHistory, hardware.GpuUsage);
        AddHistoryPoint(cpuTempHistory, hardware.CpuTemperature);
        AddHistoryPoint(cpuLoadHistory, hardware.CpuUsage);
        AddHistoryPoint(memoryHistory, hardware.MemoryUsagePercent);
        AddHistoryPoint(downloadHistory, hardware.DownloadKilobytesPerSecond);
        AddHistoryPoint(uploadHistory, hardware.UploadKilobytesPerSecond);
        AddHistoryPoint(fpsHistory, hardware.Fps);

        CheckTemperatureAlert(cpuAlert, "CPU", cpuTempHistory, settings.CpuTempCritical);
        CheckTemperatureAlert(gpuAlert, "GPU", gpuTempHistory, settings.GpuTempCritical);
    }

    private void UpdateDashboard(TelemetrySnapshot snapshot)
    {
        HardwareReading hardware = snapshot.Hardware;
        IReadOnlyList<DeviceBattery> devices = snapshot.Devices;
        SystemInfo system = snapshot.System;

        // GPU TEMP
        GpuTempText.Text = FormatMetric(hardware.GpuTemperature, "°C");
        GpuPowerText.Text = FormatPower(hardware.GpuPower);
        GpuTempPeakText.Text = gpuTempPeak is double gpuPeak ? $"peak {gpuPeak:0}°C" : string.Empty;
        GpuTemperatureCard.ToolTip = hardware.GpuTemperature.HasValue
            ? $"GPU Core Temperature: {hardware.GpuTemperature.Value:0}°C"
            : "GPU Temperature: Sensor Unavailable";

        // GPU LOAD
        GpuLoadText.Text = hardware.GpuUsage.HasValue ? $"{hardware.GpuUsage.Value:0}%" : "--%";
        VramText.Text = hardware.VramUsedGb is double vramUsed && hardware.VramTotalGb is double vramTotal
            ? $"{vramUsed:0.0} / {vramTotal:0} GB"
            : string.Empty;
        string vramInfo = (hardware.VramUsedGb.HasValue && hardware.VramTotalGb.HasValue)
            ? $"{hardware.VramUsedGb.Value:0.0} GB / {hardware.VramTotalGb.Value:0.0} GB Used"
            : (hardware.VramUsagePercent.HasValue ? $"{hardware.VramUsagePercent.Value:0}% Used" : "Unavailable");
        GpuLoadCard.ToolTip = $"GPU Core Load: {(hardware.GpuUsage.HasValue ? $"{hardware.GpuUsage.Value:0}%" : "--%")}\nDedicated VRAM: {vramInfo}";

        // CPU TEMP
        CpuTempText.Text = FormatMetric(hardware.CpuTemperature, "°C");
        CpuPowerText.Text = FormatPower(hardware.CpuPower);
        CpuTempPeakText.Text = cpuTempPeak is double cpuPeak ? $"peak {cpuPeak:0}°C" : string.Empty;
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
        FpsStatusText.Text = hasFps ? hardware.FpsApplication : hardware.FpsAvailable ? "No game detected" : "FPS unavailable";
        FpsLowText.Text = hasFps && hardware.FpsOnePercentLow is double low ? $"1% low {low:0}" : string.Empty;
        FpsCard.ToolTip = hasFps
            ? $"{hardware.FpsApplication}: {hardware.Fps:0} displayed frames per second" +
              (hardware.FpsOnePercentLow is double onePercentLow
                  ? $"\n1% low: {onePercentLow:0} FPS (the slowest 1% of frames over the last 10 seconds)"
                  : string.Empty)
            : hardware.FpsAvailable
                ? "The app in the foreground is not a game, or is not drawing frames."
                : "PresentMon could not be started, so FPS can't be measured. See the log (Settings → Open log folder).";

        // FAN SPEED
        IReadOnlyList<FanReading> fans = ApplyFanNames(hardware.Fans);
        if (fans.Count > 1)
        {
            FanText.Visibility = Visibility.Collapsed;
            SetItems(FansList, fans);
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
        SetItems(DrivesList, system.Drives);
        SetItems(DisksList, hardware.Disks);
        SetItems(DeviceList, devices);
        EmptyDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderAllSparklines();
    }

    // Replacing ItemsSource rebuilds every row, so lists that rarely change (drives, disks, devices) are
    // only replaced when their content differs. The item types are records, so this compares values.
    private static void SetItems<T>(ItemsControl list, IReadOnlyList<T> items)
    {
        if (list.ItemsSource is IReadOnlyList<T> current && current.SequenceEqual(items))
        {
            return;
        }

        list.ItemsSource = items;
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
        var (gpuTempStroke, gpuTempFill) = GetThresholdColors(RecentAverage(gpuTempHistory), settings.GpuTempWarning, settings.GpuTempCritical);
        var (cpuTempStroke, cpuTempFill) = GetThresholdColors(RecentAverage(cpuTempHistory), settings.CpuTempWarning, settings.CpuTempCritical);
        var (cpuLoadStroke, cpuLoadFill) = GetThresholdColors(RecentAverage(cpuLoadHistory), CpuLoadWarning, CpuLoadCritical);
        var (memStroke, memFill) = GetThresholdColors(RecentAverage(memoryHistory), MemoryWarning, MemoryCritical);
        // A fully loaded GPU while gaming is expected, so GPU load never changes colour.
        var (gpuLoadStroke, gpuLoadFill) = GetThresholdColors(0, double.PositiveInfinity, double.PositiveInfinity);

        List<double> gpuTemps = GraphWindow(gpuTempHistory);
        List<double> cpuTemps = GraphWindow(cpuTempHistory);
        List<double> fps = GraphWindow(fpsHistory);
        var (gpuTempMin, gpuTempMax) = GetTemperatureRange(gpuTemps);
        var (cpuTempMin, cpuTempMax) = GetTemperatureRange(cpuTemps);
        var (fpsStroke, fpsFill) = GetThresholdColors(0, double.PositiveInfinity, double.PositiveInfinity);
        double fpsMax = Math.Max(fps.DefaultIfEmpty(0).Max() * 1.15, 60);

        RenderSparkline(GpuTempSparklineCanvas, gpuTemps, minVal: gpuTempMin, maxVal: gpuTempMax, strokeColor: gpuTempStroke, fillColor: gpuTempFill, warningThreshold: settings.GpuTempWarning);
        RenderSparkline(GpuLoadSparklineCanvas, GraphWindow(gpuLoadHistory), minVal: 0, maxVal: 100, strokeColor: gpuLoadStroke, fillColor: gpuLoadFill);
        RenderSparkline(CpuTempSparklineCanvas, cpuTemps, minVal: cpuTempMin, maxVal: cpuTempMax, strokeColor: cpuTempStroke, fillColor: cpuTempFill, warningThreshold: settings.CpuTempWarning);
        RenderSparkline(CpuLoadSparklineCanvas, GraphWindow(cpuLoadHistory), minVal: 0, maxVal: 100, strokeColor: cpuLoadStroke, fillColor: cpuLoadFill, warningThreshold: CpuLoadWarning);
        RenderSparkline(MemorySparklineCanvas, GraphWindow(memoryHistory), minVal: 0, maxVal: 100, strokeColor: memStroke, fillColor: memFill, warningThreshold: MemoryWarning);
        RenderSparkline(FpsSparklineCanvas, fps, minVal: 0, maxVal: fpsMax, strokeColor: fpsStroke, fillColor: fpsFill);
        RenderDualSparkline(NetworkSparklineCanvas, GraphWindow(downloadHistory), GraphWindow(uploadHistory), downColor: Color.FromRgb(0x73, 0xBF, 0x69), upColor: Color.FromRgb(0xB8, 0x77, 0xD9));
    }

    // The last GraphSpanSeconds points, averaged into at most MaxPlotPoints buckets.
    private List<double> GraphWindow(List<double> history)
    {
        int span = Math.Clamp(settings.GraphSpanSeconds, 30, MaxHistoryPoints);
        int start = Math.Max(0, history.Count - span);
        int count = history.Count - start;
        if (count <= MaxPlotPoints)
        {
            return history.GetRange(start, count);
        }

        var points = new List<double>(MaxPlotPoints);
        for (int bucket = 0; bucket < MaxPlotPoints; bucket++)
        {
            int from = start + (int)((long)bucket * count / MaxPlotPoints);
            int to = start + (int)((long)(bucket + 1) * count / MaxPlotPoints);
            double sum = 0;
            for (int i = from; i < to; i++)
            {
                sum += history[i];
            }

            points.Add(sum / Math.Max(1, to - from));
        }

        return points;
    }

    private void CheckTemperatureAlert(TemperatureAlertState state, string component, List<double> history, double critical)
    {
        DateTime now = DateTime.UtcNow;
        if (history.Count == 0 || RecentAverage(history) < critical)
        {
            state.CriticalSinceUtc = null;
            return;
        }

        state.CriticalSinceUtc ??= now;
        if (now - state.CriticalSinceUtc < AlertDelay || now - state.LastAlertUtc < AlertCooldown)
        {
            return;
        }

        state.LastAlertUtc = now;
        string message = $"{component} has been at or above {critical:0}°C for {AlertDelay.TotalSeconds:0} seconds (now {history[^1]:0}°C).";
        AppLog.Warn($"Temperature alert: {message}");
        if (settings.TemperatureAlerts)
        {
            AlertRaised?.Invoke($"{component} temperature is critical", message);
        }
    }

    private sealed class TemperatureAlertState
    {
        public DateTime? CriticalSinceUtc { get; set; }
        public DateTime LastAlertUtc { get; set; } = DateTime.MinValue;
    }

    private static double? Peak(double? peak, double? value) =>
        value is double current ? Math.Max(peak ?? current, current) : peak;

    private static string FormatPower(double? watts) => watts is double value ? $"{value:0} W" : string.Empty;

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
    private static (double min, double max) GetTemperatureRange(IReadOnlyList<double> history)
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

    private static void RenderSparkline(Canvas canvas, IReadOnlyList<double> history, double minVal, double maxVal, Color strokeColor, Color fillColor, double? warningThreshold = null)
    {
        SparklineShapes shapes = GetSparklineShapes(canvas, seriesCount: 1);
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (history.Count < 2 || width <= 0 || height <= 0)
        {
            shapes.WarningLine.Visibility = Visibility.Collapsed;
            shapes.Series[0].Hide();
            return;
        }

        double range = maxVal - minVal;
        if (range <= 0)
        {
            range = 1;
        }

        // Warning threshold line, only when it falls inside the visible range
        if (warningThreshold is double threshold && threshold > minVal && threshold < maxVal)
        {
            double warnY = height - SparklinePaddingY - ((threshold - minVal) / range * (height - (SparklinePaddingY * 2)));
            shapes.WarningLine.X1 = 0;
            shapes.WarningLine.X2 = width;
            shapes.WarningLine.Y1 = warnY;
            shapes.WarningLine.Y2 = warnY;
            shapes.WarningLine.Visibility = Visibility.Visible;
        }
        else
        {
            shapes.WarningLine.Visibility = Visibility.Collapsed;
        }

        shapes.Series[0].Update(history, width, height, value => (value - minVal) / range, strokeColor, fillColor);
    }

    private static void RenderDualSparkline(Canvas canvas, IReadOnlyList<double> history1, IReadOnlyList<double> history2, Color downColor, Color upColor)
    {
        SparklineShapes shapes = GetSparklineShapes(canvas, seriesCount: 2);
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (Math.Max(history1.Count, history2.Count) < 2 || width <= 0 || height <= 0)
        {
            shapes.Series[0].Hide();
            shapes.Series[1].Hide();
            return;
        }

        double max1 = history1.Count > 0 ? history1.Max() : 0;
        double max2 = history2.Count > 0 ? history2.Max() : 0;
        double peakObserved = Math.Max(max1, max2);
        // A 100 KB/s floor keeps idle background traffic from looking like activity.
        double maxVal = Math.Max(peakObserved * 1.25d, 100d);

        UpdateOrHide(shapes.Series[0], history1, width, height, maxVal, downColor, Color.FromArgb(0x30, downColor.R, downColor.G, downColor.B));
        UpdateOrHide(shapes.Series[1], history2, width, height, maxVal, upColor, Color.FromArgb(0x20, upColor.R, upColor.G, upColor.B));
    }

    private static void UpdateOrHide(SparklineSeries series, IReadOnlyList<double> history, double width, double height, double maxVal, Color strokeColor, Color fillColor)
    {
        if (history.Count < 2)
        {
            series.Hide();
            return;
        }

        series.Update(history, width, height, value => value / maxVal, strokeColor, fillColor);
    }

    private const double SparklinePaddingY = 2;

    private static readonly Brush WarningLineBrush = CreateFrozenBrush(Color.FromArgb(0x70, 0xFF, 0x98, 0x30));

    // A graph's shapes are created the first time it is drawn and kept on the canvas (in Tag); later
    // redraws only replace their points and colours.
    private static SparklineShapes GetSparklineShapes(Canvas canvas, int seriesCount)
    {
        if (canvas.Tag is SparklineShapes existing)
        {
            return existing;
        }

        canvas.Children.Clear();
        var warningLine = new Line
        {
            Stroke = WarningLineBrush,
            StrokeThickness = 0.8,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Visibility = Visibility.Collapsed
        };
        canvas.Children.Add(warningLine);

        var series = new SparklineSeries[seriesCount];
        for (int i = 0; i < seriesCount; i++)
        {
            // The second series of the network graph (upload) is dashed.
            series[i] = new SparklineSeries(canvas, dashed: i == 1);
        }

        var shapes = new SparklineShapes(warningLine, series);
        canvas.Tag = shapes;
        return shapes;
    }

    private sealed record SparklineShapes(Line WarningLine, SparklineSeries[] Series);

    // One line with a gradient area underneath.
    private sealed class SparklineSeries
    {
        private readonly Polygon area;
        private readonly Polyline line;
        private readonly SolidColorBrush stroke = new();
        private readonly GradientStop fillTop = new(Colors.Transparent, 0.0);
        private readonly GradientStop fillBottom = new(Colors.Transparent, 1.0);

        public SparklineSeries(Canvas canvas, bool dashed)
        {
            area = new Polygon
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops = new GradientStopCollection { fillTop, fillBottom }
                }
            };
            line = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round
            };
            if (dashed)
            {
                line.StrokeDashArray = new DoubleCollection { 3, 2 };
            }

            canvas.Children.Add(area);
            canvas.Children.Add(line);
        }

        public void Hide()
        {
            area.Visibility = Visibility.Collapsed;
            line.Visibility = Visibility.Collapsed;
        }

        // normalize maps a value to 0..1 of the graph height (clamped).
        public void Update(IReadOnlyList<double> history, double width, double height, Func<double, double> normalize, Color strokeColor, Color fillColor)
        {
            double usableHeight = height - (SparklinePaddingY * 2);
            double stepX = width / (history.Count - 1);
            var linePoints = new PointCollection(history.Count);
            var areaPoints = new PointCollection(history.Count + 2) { new Point(0, height) };
            for (int i = 0; i < history.Count; i++)
            {
                double y = height - SparklinePaddingY - (Math.Clamp(normalize(history[i]), 0, 1) * usableHeight);
                var point = new Point(i * stepX, y);
                linePoints.Add(point);
                areaPoints.Add(point);
            }

            areaPoints.Add(new Point(width, height));
            linePoints.Freeze();
            areaPoints.Freeze();

            line.Points = linePoints;
            area.Points = areaPoints;
            stroke.Color = strokeColor;
            fillTop.Color = fillColor;
            fillBottom.Color = Color.FromArgb(0, fillColor.R, fillColor.G, fillColor.B);
            area.Visibility = Visibility.Visible;
            line.Visibility = Visibility.Visible;
        }
    }

    private sealed record TelemetrySnapshot(
        HardwareReading Hardware,
        IReadOnlyList<DeviceBattery> Devices,
        SystemInfo System);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b) => CreateFrozenBrush(Color.FromRgb(r, g, b));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
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