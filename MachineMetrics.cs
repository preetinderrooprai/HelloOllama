using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public sealed class MachineMetrics
{
    private static readonly Regex CpuUsagePattern =
        new(@"CPU usage:\s*([\d.]+)%\s*user,\s*([\d.]+)%\s*sys", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageSizePattern =
        new(@"page size of\s+(\d+)\s+bytes", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PagePattern =
        new(@"Pages\s+(free|inactive|speculative):\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<object> ReadAsync(CancellationToken cancellationToken)
    {
        double? cpuPercent = null;
        long? totalBytes = null;
        long? usedBytes = null;
        IReadOnlyList<object> topMemoryProcesses = [];
        string? processMemoryError = null;
        string? processMemoryWarning = null;
        var errors = new List<string>();

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var output = await RunAsync("/usr/bin/top", ["-l", "1", "-n", "0"], cancellationToken);
                var match = CpuUsagePattern.Match(output);
                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var user) &&
                    double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var system))
                {
                    cpuPercent = Math.Clamp(user + system, 0, 100);
                }
                else
                {
                    errors.Add("Could not read CPU usage from macOS top.");
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                cpuPercent = ReadLinuxCpuPercent();
            }
            else
            {
                errors.Add("System CPU metrics are not available on this operating system.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            errors.Add($"CPU metrics unavailable: {exception.Message}");
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var totalText = await RunAsync("/usr/sbin/sysctl", ["-n", "hw.memsize"], cancellationToken);
                var vmText = await RunAsync("/usr/bin/vm_stat", [], cancellationToken);
                if (long.TryParse(totalText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                {
                    var pageSizeMatch = PageSizePattern.Match(vmText);
                    var pageSize = pageSizeMatch.Success &&
                                   long.TryParse(pageSizeMatch.Groups[1].Value, out var parsedPageSize)
                        ? parsedPageSize
                        : 4096;
                    long availablePages = 0;
                    foreach (Match pageMatch in PagePattern.Matches(vmText))
                    {
                        if (long.TryParse(pageMatch.Groups[2].Value, out var pages))
                        {
                            availablePages += pages;
                        }
                    }

                    totalBytes = total;
                    usedBytes = Math.Clamp(total - availablePages * pageSize, 0, total);
                }
                else
                {
                    errors.Add("Could not read total memory from macOS.");
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                (usedBytes, totalBytes) = ReadLinuxMemory();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            errors.Add($"Memory metrics unavailable: {exception.Message}");
        }

        try
        {
            var processMemory = ReadTopMemoryProcesses();
            topMemoryProcesses = processMemory.Processes;
            if (processMemory.UnavailableCount > 0)
            {
                processMemoryWarning =
                    $"Memory usage could not be read for {processMemory.UnavailableCount} process(es) that exited or denied access.";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            processMemoryError = $"Process memory data unavailable: {exception.Message}";
        }

        return new
        {
            cpuPercent,
            memoryUsedBytes = usedBytes,
            memoryTotalBytes = totalBytes,
            topMemoryProcesses,
            processMemoryError,
            processMemoryWarning,
            gpuUtilizationPercent = (double?)null,
            gpuNote = "GPU utilization sensors are not exposed by Ollama on this system. VRAM and processor placement are shown for active Ollama models.",
            collectedAt = DateTimeOffset.UtcNow,
            errors
        };
    }

    private static (IReadOnlyList<object> Processes, int UnavailableCount) ReadTopMemoryProcesses()
    {
        var totals = new Dictionary<string, (long MemoryBytes, int Count)>(StringComparer.OrdinalIgnoreCase);
        var unavailableCount = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    var memoryBytes = process.WorkingSet64;
                    var current = totals.GetValueOrDefault(name);
                    totals[name] = (current.MemoryBytes + memoryBytes, current.Count + 1);
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                   System.ComponentModel.Win32Exception)
                {
                    unavailableCount++;
                }
            }
        }

        var topProcesses = totals
            .OrderByDescending(entry => entry.Value.MemoryBytes)
            .Take(6)
            .Select(entry => (object)new
            {
                name = entry.Key,
                memoryBytes = entry.Value.MemoryBytes,
                processCount = entry.Value.Count
            })
            .ToArray();
        return (topProcesses, unavailableCount);
    }

    private static async Task<string> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{fileName} exited with code {process.ExitCode}." : error.Trim());
        }

        return output;
    }

    private static double? ReadLinuxCpuPercent()
    {
        var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var counters = fields.Skip(1).Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var idle = counters[3] + (counters.Length > 4 ? counters[4] : 0);
        var total = counters.Sum();
        var previous = _previousLinuxCpu;
        _previousLinuxCpu = (total, idle);
        if (previous is null)
        {
            return null;
        }

        var totalDelta = total - previous.Value.Total;
        var idleDelta = idle - previous.Value.Idle;
        return totalDelta <= 0 ? null : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static (long Used, long Total) ReadLinuxMemory()
    {
        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => long.Parse(parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                    CultureInfo.InvariantCulture) * 1024);
        var total = values["MemTotal"];
        var available = values["MemAvailable"];
        return (total - available, total);
    }

    private static (long Total, long Idle)? _previousLinuxCpu;
}
