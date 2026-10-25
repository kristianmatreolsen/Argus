using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Argus.Services;

public sealed class FrameRateService : IDisposable
{
    private const string SessionName = "ArgusTelemetry";
    private static readonly TimeSpan StartupProbeTime = TimeSpan.FromSeconds(1.5);

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "SearchHost.exe",
        "SearchApp.exe"
    };

    private Process? process;
    private FileStream? bundledPresentMonLock;
    private readonly Dictionary<uint, string> processNames = new();
    private readonly object sync = new();
    private readonly Dictionary<string, Queue<DateTime>> frameTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource cancellation = new();

    public FrameRateService()
    {
        _ = Task.Run(() => StartFirstWorkingPresentMonAsync(cancellation.Token));
    }

    // Several vendors bundle PresentMon, but older 1.x builds (e.g. NVIDIA FrameView's) reject the 2.x
    // command line and exit immediately. Try each candidate and keep the first one that stays running.
    private async Task StartFirstWorkingPresentMonAsync(CancellationToken token)
    {
        string? bundled = PrepareBundledPresentMon();
        IEnumerable<string> candidates = bundled is null
            ? FindPresentMonCandidates()
            : FindPresentMonCandidates().Prepend(bundled);

        foreach (string executable in candidates)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            Process? candidate = null;
            try
            {
                candidate = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        // A fixed session name lets --stop_existing_session clean up a session orphaned by a
                        // crashed run; per-process names would leave those running and starving new sessions.
                        Arguments = $"--output_stdout --no_console_stats --exclude_dropped --stop_existing_session --session_name {SessionName}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                if (!candidate.Start())
                {
                    candidate.Dispose();
                    continue;
                }

                _ = ReadOutputAsync(candidate, token);
                _ = DrainErrorsAsync(candidate, token);

                await Task.Delay(StartupProbeTime, token);
                if (candidate.HasExited)
                {
                    candidate.Dispose();
                    continue;
                }

                lock (sync)
                {
                    if (token.IsCancellationRequested)
                    {
                        StopProcess(candidate);
                        return;
                    }

                    process = candidate;
                }

                return;
            }
            catch (OperationCanceledException)
            {
                if (candidate is not null)
                {
                    StopProcess(candidate);
                }

                return;
            }
            catch (Exception)
            {
                candidate?.Dispose();
            }
        }
    }

    // Reports the frame rate of the app the user last had in the foreground. Taking the highest rate of
    // any process would report browsers, chat apps or the desktop as "FPS" while no game is running.
    public FrameRateReading ReadFps()
    {
        string? application = UpdateForegroundApplication();
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-1.25d);
        lock (sync)
        {
            foreach (Queue<DateTime> times in frameTimes.Values)
            {
                while (times.Count > 0 && times.Peek() < cutoff)
                {
                    times.Dequeue();
                }
            }

            if (application is null || !frameTimes.TryGetValue(application, out Queue<DateTime>? frames) || frames.Count == 0)
            {
                return new FrameRateReading(0d, null);
            }

            return new FrameRateReading(frames.Count / 1.25d, Path.GetFileNameWithoutExtension(application));
        }
    }

    // The app being measured is the one in the foreground. When that is Argus itself or the taskbar/Start
    // menu (e.g. while looking at the dashboard), the window directly behind it in the z-order is used
    // instead: the app the user was in before. This also works right after Argus starts.
    private string? UpdateForegroundApplication()
    {
        IntPtr window = GetForegroundWindow();
        string? application = GetApplication(window);
        if (window == IntPtr.Zero || application is null || IsArgusOrShell(window, application))
        {
            application = FindAppBehind(window);
        }

        return application;
    }

    private string? FindAppBehind(IntPtr window)
    {
        IntPtr candidate = window == IntPtr.Zero ? GetTopWindow(IntPtr.Zero) : GetWindow(window, GwHwndNext);
        for (int i = 0; i < 1000 && candidate != IntPtr.Zero; i++, candidate = GetWindow(candidate, GwHwndNext))
        {
            if (!IsUserFacingWindow(candidate))
            {
                continue;
            }

            string? application = GetApplication(candidate);
            if (application is not null && !IsArgusOrShell(candidate, application))
            {
                return application;
            }
        }

        return null;
    }

    private static bool IsArgusOrShell(IntPtr window, string application)
    {
        return (GetWindowThreadProcessId(window, out uint processId) != 0 && processId == Environment.ProcessId)
            || ShellProcesses.Contains(application);
    }

    // Visible, not minimized, not a tool window, and not cloaked (UWP apps keep invisible "cloaked"
    // windows around that would otherwise look like the window behind Argus).
    private static bool IsUserFacingWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window))
        {
            return false;
        }

        if ((GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
        {
            return false;
        }

        return DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private string? GetApplication(IntPtr window)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint processId) == 0)
        {
            return null;
        }

        if (processNames.TryGetValue(processId, out string? cached))
        {
            return cached;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string application = process.ProcessName + ".exe";
            if (processNames.Count > 256)
            {
                processNames.Clear();
            }

            processNames[processId] = application;
            return application;
        }
        catch (Exception)
        {
            // The process exited between reading the window and opening it.
            return null;
        }
    }

    public void Dispose()
    {
        Process? running;
        FileStream? presentMonLock;
        lock (sync)
        {
            cancellation.Cancel();
            running = process;
            process = null;
            presentMonLock = bundledPresentMonLock;
            bundledPresentMonLock = null;
        }

        if (running is not null)
        {
            StopProcess(running);
        }

        presentMonLock?.Dispose();
    }

    private static void StopProcess(Process target)
    {
        try
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        target.Dispose();
    }

    private async Task ReadOutputAsync(Process source, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await source.StandardOutput.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                string? processName = ParseProcessName(line);
                if (processName is null)
                {
                    continue;
                }

                lock (sync)
                {
                    if (!frameTimes.TryGetValue(processName, out Queue<DateTime>? times))
                    {
                        times = new Queue<DateTime>();
                        frameTimes[processName] = times;
                    }

                    times.Enqueue(DateTime.UtcNow);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task DrainErrorsAsync(Process source, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && await source.StandardError.ReadLineAsync(token) is not null)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string? ParseProcessName(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Application", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string firstField = line.Split(',', 2)[0].Trim().Trim('"');
        return firstField.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? firstField : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private const uint GwHwndNext = 2;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    // Extracts the PresentMon embedded at build time to %LOCALAPPDATA%\Argus\PresentMon. Argus often runs
    // elevated while that folder is writable by any process of the user, so the file is checked against
    // the embedded copy's SHA-256 and kept open without write/delete sharing until Argus exits; it can't
    // be swapped for another program between the check and the launch.
    private string? PrepareBundledPresentMon()
    {
        try
        {
            using Stream? resource = typeof(FrameRateService).Assembly.GetManifestResourceStream("PresentMon.exe");
            if (resource is null)
            {
                return null;
            }

            byte[] bytes = new byte[resource.Length];
            resource.ReadExactly(bytes);
            byte[] expectedHash = SHA256.HashData(bytes);

            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus", "PresentMon");
            string path = Path.Combine(directory, $"PresentMon-{Convert.ToHexString(expectedHash, 0, 8)}.exe");
            Directory.CreateDirectory(directory);
            DeleteOtherBundledVersions(directory, path);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);
                }

                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
                {
                    lock (sync)
                    {
                        bundledPresentMonLock = stream;
                    }

                    return path;
                }

                // Partial or modified file: replace it with the embedded copy and check again.
                stream.Dispose();
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static void DeleteOtherBundledVersions(string directory, string currentPath)
    {
        foreach (string file in Directory.GetFiles(directory, "PresentMon-*.exe"))
        {
            if (!string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Still in use by another Argus session; it is removed next time.
                }
            }
        }
    }

    private static IEnumerable<string> FindPresentMonCandidates()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "PresentMon_x64.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"),
            Path.Combine(programFiles, "AMD", "CNext", "CNext", "PresentMon-x64.exe")
        };

        // RivaTuner Statistics Server ships versioned builds, e.g. PresentMon-2.3.1-x64.exe; prefer the newest.
        string rtssPlugins = Path.Combine(programFilesX86, "RivaTuner Statistics Server", "Plugins", "Client", "PresentMonDataProvider");
        try
        {
            if (Directory.Exists(rtssPlugins))
            {
                candidates.AddRange(Directory.GetFiles(rtssPlugins, "PresentMon-*-x64*.exe")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
        }

        // FrameView's bundled PresentMon is a 1.x build; keep it last as a fallback.
        candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "FrameViewSDK", "bin", "PresentMon_x64.exe"));

        return candidates.Where(File.Exists);
    }
}

public sealed record FrameRateReading(double Fps, string? Application);
