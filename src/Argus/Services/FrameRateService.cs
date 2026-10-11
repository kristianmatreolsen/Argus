using System.Diagnostics;
using System.IO;
using System.Text;

namespace Argus.Services;

public sealed class FrameRateService : IDisposable
{
    private readonly Process? process;
    private readonly object sync = new();
    private readonly Dictionary<string, Queue<DateTime>> frameTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource cancellation = new();

    public FrameRateService()
    {
        string? executable = FindPresentMon();
        if (executable is null)
        {
            return;
        }

        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"--output_stdout --no_csv --no_console_stats --exclude_dropped --stop_existing_session --session_name ArgusTelemetry{Environment.ProcessId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            if (process.Start())
            {
                _ = ReadOutputAsync(process, cancellation.Token);
                _ = DrainErrorsAsync(process, cancellation.Token);
            }
        }
        catch (Exception)
        {
            process?.Dispose();
        }
    }

    public double ReadFps()
    {
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

            return frameTimes.Values
                .Select(times => times.Count / 1.25d)
                .DefaultIfEmpty(0d)
                .Max();
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        process?.Dispose();
        cancellation.Dispose();
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

    private static string? FindPresentMon()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "FrameViewSDK", "bin", "PresentMon_x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "CNext", "CNext", "PresentMon-x64.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon_x64.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
