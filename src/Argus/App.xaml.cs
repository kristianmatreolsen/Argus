using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Argus.Models;
using Argus.Services;

namespace Argus;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Argus.SingleInstance";
    private const string ShowDashboardEventName = @"Local\Argus.ShowDashboard";
    private const string ReplaceArgument = "--replace";

    private readonly DisplaySettingsService displaySettingsService = new();
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;
    private EventWaitHandle? showDashboardEvent;
    private RegisteredWaitHandle? showDashboardRegistration;
    private HardwareMonitorService? hardwareMonitor;
    private TaskbarIcon? tray;
    private Icon? trayIcon;
    private MainWindow? mainWindow;
    private DisplaySettings? displaySettings;
    private SettingsWindow? settingsWindow;

    // Version from the release tag (set by the build; "0.0.0-dev" for local builds), without the commit hash.
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => AppLog.Error("Unhandled exception on the UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved background task exception", args.Exception);
            args.SetObserved();
        };

        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out ownsSingleInstanceMutex);
        showDashboardEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDashboardEventName);
        if (!ownsSingleInstanceMutex && e.Args.Contains(ReplaceArgument))
        {
            // Started by "Restart as administrator": the previous instance is shutting down, so wait for it.
            ownsSingleInstanceMutex = WaitForPreviousInstance(singleInstanceMutex);
        }

        if (!ownsSingleInstanceMutex)
        {
            // Another Argus is already running in this session; ask it to show its dashboard instead.
            showDashboardEvent.Set();
            Shutdown();
            return;
        }

        showDashboardRegistration = ThreadPool.RegisterWaitForSingleObject(
            showDashboardEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowDashboard)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        AppLog.Info($"Argus {Version} starting (administrator: {SystemCapabilities.IsElevated}, PawnIO installed: {SystemCapabilities.IsPawnIoInstalled}, {Environment.OSVersion.VersionString})");
        hardwareMonitor = new HardwareMonitorService();
        StartupService.UpgradeToElevatedStartup();
        displaySettings = displaySettingsService.Load();
        mainWindow = new MainWindow(hardwareMonitor, new DeviceBatteryService(), new SystemInfoService(), displaySettings, displaySettingsService);
        trayIcon = CreateTrayIcon();
        tray = (TaskbarIcon)Resources["TrayIcon"];
        tray.Icon = trayIcon;
        TaskbarIcon alertTray = tray;
        mainWindow.AlertRaised += (title, message) => alertTray.ShowBalloonTip(title, message, BalloonIcon.Warning);
        var trayToolTip = new TrayToolTipPanel();
        DisplaySettings settings = displaySettings;
        tray.TrayToolTip = trayToolTip;
        mainWindow.SnapshotApplied += (hardware, devices) => trayToolTip.Update(hardware, devices, settings);
        mainWindow.ShowFromTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsSingleInstanceMutex)
        {
            AppLog.Info("Argus exiting");
        }

        showDashboardRegistration?.Unregister(null);
        showDashboardEvent?.Dispose();
        mainWindow?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        hardwareMonitor?.Dispose();
        if (ownsSingleInstanceMutex)
        {
            singleInstanceMutex?.ReleaseMutex();
        }

        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Relaunches Argus elevated (one UAC prompt) and exits this instance. If the prompt is cancelled,
    // this instance keeps running.
    public void RestartAsAdministrator()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, ReplaceArgument)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        mainWindow?.CloseFromApplication();
        Shutdown();
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            // The previous instance exited without releasing the mutex; we own it now.
            return true;
        }
    }

    private void OpenTrayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowDashboard();
    }

    private void ExitTrayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        mainWindow?.CloseFromApplication();
        Shutdown();
    }

    private void SettingsTrayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    public void OpenSettingsWindow()
    {
        if (displaySettings is null)
        {
            return;
        }

        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(displaySettings, mainWindow?.CurrentFans ?? Array.Empty<FanReading>());
            settingsWindow.SettingsChanged += ApplyDisplaySettings;
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }

        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private void ApplyDisplaySettings()
    {
        if (displaySettings is null)
        {
            return;
        }

        bool startupUpdated = displaySettingsService.Save(displaySettings);
        if (!startupUpdated)
        {
            settingsWindow?.SetStartWithWindows(displaySettings.StartWithWindows);
            MessageBox.Show(
                settingsWindow,
                "Windows could not update Argus startup registration. The current startup setting was kept.",
                "Argus Startup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        mainWindow?.ApplyDisplaySettings();
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        mainWindow?.ToggleFromTray();
    }

    private void ShowDashboard()
    {
        mainWindow?.ShowFromTray();
    }

    // Renders the tray icon from the same vector logo as the window header (ArgusLogoSource), at the size
    // Windows uses for small icons on this display, so it stays sharp and always matches the logo.
    private Icon CreateTrayIcon()
    {
        int size = Math.Max(16, GetSystemMetrics(SmallIconWidthMetric));
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage((System.Windows.Media.ImageSource)Resources["ArgusLogoSource"], new Rect(0, 0, size, size));
        }

        var rendered = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var bitmap = new Bitmap(stream);
        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Icon.FromHandle(iconHandle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private const int SmallIconWidthMetric = 49; // SM_CXSMICON

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

