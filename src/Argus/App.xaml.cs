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
    private readonly HardwareMonitorService hardwareMonitor = new();
    private readonly DeviceBatteryService deviceBattery = new();
    private readonly SystemInfoService systemInfo = new();
    private readonly DisplaySettingsService displaySettingsService = new();
    private readonly Icon trayIcon = CreateTrayIcon();
    private MainWindow? mainWindow;
    private readonly Icon appIcon = CreateTrayIcon();
    private DisplaySettings? displaySettings;
    private SettingsWindow? settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        displaySettings = displaySettingsService.Load();
        mainWindow = new MainWindow(hardwareMonitor, deviceBattery, systemInfo, displaySettings);
        mainWindow.Icon = appIcon.ToImageSource();
        TaskbarIcon tray = (TaskbarIcon)Resources["TrayIcon"];
        tray.Icon = trayIcon;
        mainWindow.TelemetryUpdated += UpdateTrayToolTip;
        UpdateTrayToolTip(mainWindow.CurrentTelemetry);
        mainWindow.ShowNearTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        mainWindow?.Dispose();
        ((TaskbarIcon)Resources["TrayIcon"]).Dispose();
        trayIcon.Dispose();
        appIcon.Dispose();
        hardwareMonitor.Dispose();
        base.OnExit(e);
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
            settingsWindow = new SettingsWindow(displaySettings);
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

        displaySettingsService.Save(displaySettings);
        mainWindow?.ApplyDisplaySettings();
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        ShowDashboard();
    }

    private void ShowDashboard()
    {
        mainWindow?.ShowNearTray();
    }

    private void UpdateTrayToolTip(string telemetry)
    {
        if (Resources["TrayIcon"] is TaskbarIcon tray)
        {
            tray.ToolTipText = telemetry;
        }
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