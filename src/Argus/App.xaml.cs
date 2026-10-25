using System.Windows;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        hardwareMonitor = new HardwareMonitorService();
        StartupService.UpgradeToElevatedStartup();
        displaySettings = displaySettingsService.Load();
        mainWindow = new MainWindow(hardwareMonitor, new DeviceBatteryService(), new SystemInfoService(), displaySettings, displaySettingsService);
        trayIcon = CreateTrayIcon();
        mainWindow.Icon = trayIcon.ToImageSource();
        tray = (TaskbarIcon)Resources["TrayIcon"];
        tray.Icon = trayIcon;
        var trayToolTip = new TrayToolTipPanel();
        DisplaySettings settings = displaySettings;
        tray.TrayToolTip = trayToolTip;
        mainWindow.SnapshotApplied += (hardware, devices) => trayToolTip.Update(hardware, devices, settings);
        mainWindow.ShowFromTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        graphics.Clear(Color.Transparent);

        // Dark background circle
        using var bgBrush = new SolidBrush(Color.FromArgb(255, 15, 23, 42)); // #0F172A
        graphics.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var borderPen = new Pen(Color.FromArgb(255, 51, 65, 85), 1.5f); // #334155
        graphics.DrawEllipse(borderPen, 1, 1, 30, 30);

        // Outer Eye Contour (Cyan)
        using var eyePen = new Pen(Color.FromArgb(255, 56, 189, 248), 2.0f); // #38BDF8
        using var eyePath = new GraphicsPath();
        eyePath.AddBezier(new PointF(4, 16), new PointF(10, 7), new PointF(22, 7), new PointF(28, 16));
        eyePath.AddBezier(new PointF(28, 16), new PointF(22, 25), new PointF(10, 25), new PointF(4, 16));
        graphics.DrawPath(eyePen, eyePath);

        // Iris (Cyan circle)
        using var irisBrush = new SolidBrush(Color.FromArgb(255, 56, 189, 248)); // #38BDF8
        graphics.FillEllipse(irisBrush, 11, 11, 10, 10);

        // Pupil (Dark center circle)
        using var pupilBrush = new SolidBrush(Color.FromArgb(255, 15, 23, 42)); // #0F172A
        graphics.FillEllipse(pupilBrush, 13.5f, 13.5f, 5, 5);

        // Glint (White highlight dot)
        using var glintBrush = new SolidBrush(Color.White);
        graphics.FillEllipse(glintBrush, 12.5f, 12.5f, 2.5f, 2.5f);

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Icon.FromHandle(iconHandle);
            return new Icon(temporaryIcon, 32, 32);
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal static class IconExtensions
{
    public static System.Windows.Media.ImageSource ToImageSource(this Icon icon)
    {
        using MemoryStream stream = new();
        icon.Save(stream);
        stream.Position = 0;
        var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }
}