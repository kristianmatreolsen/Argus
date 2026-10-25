using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private uint lastForegroundProcessId;
    private string? lastForegroundApplication;
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
        foreach (string executable in FindPresentMonCandidates())
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

    // Opening the dashboard (or the taskbar/Start menu) moves the foreground away from the game, so those
    // windows are skipped and the previously focused app stays the one being measured.
    private string? UpdateForegroundApplication()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint processId) == 0 || processId == Environment.ProcessId)
        {
            return lastForegroundApplication;
        }

        if (processId != lastForegroundProcessId)
        {
            try
            {
                using Process foreground = Process.GetProcessById((int)processId);
                string application = foreground.ProcessName + ".exe";
                lastForegroundProcessId = processId;
                if (!ShellProcesses.Contains(application))
                {
                    lastForegroundApplication = application;
                }
            }
            catch (Exception)
            {
                // The process exited between reading the window and opening it.
            }
        }

        return lastForegroundApplication;
    }

    public void Dispose()
    {
        Process? running;
        lock (sync)
        {
            cancellation.Cancel();
            running = process;
            process = null;
        }

        if (running is not null)
        {
            StopProcess(running);
        }
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
