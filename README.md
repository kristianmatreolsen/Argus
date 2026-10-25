<h1>
  <img src="logo.png" width="48" height="48" alt="Argus Logo" />
  Argus
</h1>

**Argus** is a modern, lightweight Windows system monitoring dashboard that sits quietly in your notification area (system tray). Named after the all-seeing hundred-eyed giant of Greek mythology, Argus provides real-time hardware telemetry, Grafana-style time-series trend graphs, storage health, and peripheral battery status—all packed into a sleek dark UI.

---

## ✨ Features

- **📊 Real-Time Metric Telemetry (1-Second Refresh)**:
  - **GPU**: Core Temperature & GPU Utilization Load (`%`) with Dedicated VRAM usage (`GB used / total`).
  - **CPU**: Core Package Temperature (`°C`) & Total CPU Utilization (`%`).
  - **Memory**: System RAM utilization (`%`) and physical memory breakdown (`GB used / total`).
  - **Network**: Independent Download (`↓`) and Upload (`↑`) rates with automatic peak bandwidth scaling and network adapter link capacity (e.g. `1 Gbps`, `2.5 Gbps`).
  - **FPS**: Live framerate of the app you last had in the foreground (opening the dashboard doesn't reset it), using a locally installed PresentMon (bundled with AMD Software, RivaTuner Statistics Server or NVIDIA FrameView) or one placed next to `Argus.exe`.
  - **Fan Speed**: RPM tracking across all active system, case, and GPU cooling fans, with custom fan names (fans are listed alphabetically by name).

- **📈 Grafana-Style Time-Series Graphs**:
  - Hardware-accelerated vector sparklines with 30-second rolling trend history.
  - Temperature graphs auto-scale to recent readings; a dashed line marks the warning threshold.
  - **Smart Threshold Alerts**: Colors shift from neutral Grafana Blue (`#5794F2`) to Amber (`#FF9830`) on warning thresholds and Red (`#F2495C`) on critical loads, based on a 5-second average so brief spikes don't trigger them.

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

- **🚀 Optional Windows Auto-Start**:
  - Toggle "Start Argus on Windows startup" in the settings menu to automatically launch Argus upon logging into Windows.

- **🔒 100% Local & Privacy-Focused**:
  - Zero external network requests, zero telemetry tracking, and zero cloud dependencies.
  - Runs completely offline on your local machine.

---

## 🛠️ System Requirements

| Requirement | Details |
| :--- | :--- |
| **Operating System** | Windows 10 / Windows 11 (64-bit) |
| **Users (Standalone Executable)** | **No runtime installation needed!** Download and run `Argus.exe`. |
| **SDK (Developers)** | [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Permissions** | Standard user mode. CPU temperature and motherboard fan sensors usually require running Argus as administrator. Vendor utilities that access the same sensor chip (e.g. ASUS Armoury Crate) can make motherboard fans unavailable. |

---

## 🚀 Download & Installation

### Option 1: Standalone Single-File Executable (Recommended for Users)
1. Download `Argus.exe` from the latest [GitHub Releases](https://github.com/kristianmatreolsen/Argus/releases).
2. Double-click `Argus.exe` to run. **No installation or .NET runtime setup required!**

---

### Option 2: Building from Source (Developers)

1. Clone the repository:
   ```powershell
   git clone https://github.com/kristianmatreolsen/Argus.git
   cd Argus
   ```

2. Restore dependencies and run the application:
   ```powershell
   dotnet restore
   dotnet run --project src/Argus/Argus.csproj
   ```

3. Build a standalone `.exe` locally:
   ```powershell
   dotnet publish src/Argus/Argus.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/
   ```
   *The output single-file `Argus.exe` will be located inside the `publish/` directory.*

---

## 📖 Usage Guide

- **Accessing the Dashboard**: Click the Argus eye icon in your Windows taskbar system tray (bottom-right notification area) to pop up the dashboard.
- **Settings Flyout**: Click the **SETTINGS** button in the top-right corner of the dashboard or right-click the system tray icon and select **Settings**.
  - Toggle visibility for metric cards, system information, connected devices, and tray battery details.
  - Give fans custom names (e.g. `CPU 1`, `Front`); each fan's live RPM is shown to help match it to its motherboard header.
  - Enable **Start Argus on Windows startup** to launch the current executable when the current Windows user signs in. If Windows cannot update the startup registration, Argus keeps the existing setting and shows a warning.
- **Reordering Elements**:
  - Click and drag any **section header card** up or down to reorder sections.
  - Click and drag any **metric card** to swap positions in the grid.
- **System Tray Hover**: Hovering over the system tray icon displays a live quick-summary tooltip of your CPU load, Memory, Network traffic, and connected battery statuses. Metrics that the hardware or provider does not expose are shown as unavailable.

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

Distributed under the MIT License.
