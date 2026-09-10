using System.Windows;
using PulseTray.Services;

namespace PulseTray;

public partial class App : Application
{
    private readonly HardwareMonitorService hardwareMonitor = new();
    private readonly DeviceBatteryService deviceBattery = new();
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mainWindow = new MainWindow(hardwareMonitor, deviceBattery);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        mainWindow?.Dispose();
        ((Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)Resources["TrayIcon"]).Dispose();
        hardwareMonitor.Dispose();
        base.OnExit(e);
    }

    private void OpenTrayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        mainWindow?.Show();
        mainWindow?.Activate();
    }

    private void ExitTrayMenuItem_Click(object sender, RoutedEventArgs e) => Shutdown();

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        mainWindow?.Show();
        mainWindow?.Activate();
    }
}