# PulseTray

PulseTray is a Windows notification-area monitor for PC health and connected-device battery status.

## Current prototype

- WPF desktop shell targeting .NET 8
- CPU and GPU temperature readings through LibreHardwareMonitor
- Windows-reported battery devices through WMI
- A compact dashboard that refreshes every three seconds

Peripheral battery support depends on what Windows exposes. Bluetooth HID devices may appear through the Windows battery provider; proprietary 2.4 GHz receivers, including some headset dongles, may require a vendor-specific adapter later.

## Build

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet restore
dotnet build
dotnet run --project src/PulseTray/PulseTray.csproj
```