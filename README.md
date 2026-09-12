# 👁️ Argus

**Argus** is a modern, lightweight Windows system monitoring dashboard that sits quietly in your notification area (system tray). Named after the all-seeing hundred-eyed giant of Greek mythology, Argus provides real-time hardware telemetry, Grafana-style time-series trend graphs, storage health, and peripheral battery status—all packed into a sleek dark UI.

---

## ✨ Features

- **📊 Real-Time Metric Telemetry (1-Second Refresh)**:
  - **GPU**: Core Temperature & GPU Utilization Load (`%`) with Dedicated VRAM usage (`GB used / total`).
  - **CPU**: Core Package Temperature (`°C`) & Total CPU Utilization (`%`).
  - **Memory**: System RAM utilization (`%`) and physical memory breakdown (`GB used / total`).
  - **Network**: Independent Download (`↓`) and Upload (`↑`) rates with automatic peak bandwidth scaling and network adapter link capacity (e.g. `1 Gbps`, `2.5 Gbps`).
  - **FPS**: Live 3D renderer framerate tracking via DXGI / Direct3D performance counters.
  - **Fan Speed**: RPM tracking across all active system, case, and GPU cooling fans.

- **📈 Grafana-Style Time-Series Graphs**:
  - Hardware-accelerated vector sparklines with 30-second rolling trend history.
  - Dynamic Y-axis auto-scaling and reference guide lines.
  - **Smart Threshold Alerts**: Colors dynamically shift from neutral Grafana Blue (`#5794F2`) to Amber (`#FF9830`) on warning thresholds and Red (`#F2495C`) on critical loads.

- **🖐️ Full Drag & Drop Customization**:
  - Drag and drop **section blocks** (`METRICS`, `SYSTEM`, `DEVICES`) to reorder the main view.
  - Drag and drop **individual metric cards** across the 2-column grid to create your preferred layout.
  - Click section headers to collapse or expand blocks on the fly.
  - Custom section and card orders are automatically saved to `%LOCALAPPDATA%\Argus\settings.json`.

- **💾 System & Storage Monitoring**:
  - Live PC uptime and machine info.
  - Multi-drive health cards with free space badges (`% free`) and capacity stats.

- **🔋 Peripheral Battery Status**:
  - Real-time battery levels for connected Bluetooth HID devices and supported wireless peripherals.
  - Color-coded battery badges (Green `> 50%`, Amber `20–50%`, Red `< 20%`).

- **🛡️ 100% Local & Privacy-Focused**:
  - Zero external network requests, zero telemetry tracking, and zero cloud dependencies.
  - Runs completely offline on your local machine.

---

## 🛠️ System Requirements

| Requirement | Details |
| :--- | :--- |
| **Operating System** | Windows 10 / Windows 11 (64-bit) |
| **Runtime (Users)** | [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **SDK (Developers)** | [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Permissions** | Standard user mode (*Administrator mode recommended for direct CPU MSR hardware temperature sensors*). |

---

## 🚀 Installation & Running

### Building from Source

1. Clone the repository:
   ```powershell
   git clone https://github.com/kristianmatreolsen/Argus.git
   cd Argus
   ```

2. Restore dependencies and build the solution:
   ```powershell
   dotnet restore
   dotnet build Argus.sln -c Release
   ```

3. Run the application:
   ```powershell
   dotnet run --project src/Argus/Argus.csproj
   ```

---

## 📖 Usage Guide

- **Accessing the Dashboard**: Click the Argus eye icon in your Windows taskbar system tray (bottom-right notification area) to pop up the dashboard.
- **Settings Flyout**: Click the **SETTINGS** button in the top-right corner of the dashboard or right-click the system tray icon and select **Settings**.
  - Toggle visibility for any of the 10 individual metric cards, system info, or device batteries.
- **Reordering Elements**:
  - Click and drag any **section header card** up or down to reorder sections.
  - Click and drag any **metric card** to swap positions in the grid.
- **System Tray Hover**: Hovering over the system tray icon displays a live quick-summary tooltip of your CPU load, Memory, Network traffic, and connected battery statuses.

---

## 🔒 Security & Privacy Statement

Argus was built with security and transparency in mind:

1. **No External Network Calls**: Argus does not communicate with external servers, telemetry services, or remote APIs.
2. **Local Settings Storage**: User preferences are stored exclusively in your local Windows user profile directory (`%LOCALAPPDATA%\Argus\settings.json`).
3. **Open Source Dependencies**:
   - `LibreHardwareMonitorLib`: Open-source hardware sensor library.
   - `Hardcodet.NotifyIcon.Wpf`: WPF system tray wrapper.
   - `HidSharp`: Open-source HID device communication library.

---

## 📄 License

Distributed under the MIT License. See `LICENSE` for more information.