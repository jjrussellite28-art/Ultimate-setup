using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

public sealed record PerformanceSample(int Cpu, int? Gpu, double RamUsedGb, double RamTotalGb);

/// <summary>
/// CPU, GPU and memory load for the guide menu, like the performance overlay on a
/// handheld. Temperatures need a hardware driver and come with the CaseHUD plugin.
/// </summary>
public sealed class PerformanceMonitor
{
    private readonly object _lock = new();
    private (ulong Idle, ulong Total)? _lastCpu;
    private List<PerformanceCounter>? _gpuCounters;
    private DateTime _gpuCountersAt;

    public Task<PerformanceSample> SampleAsync() => Task.Run(async () =>
    {
        // Measure over this window only, not since the last time the menu was open.
        var first = CpuTimes();
        lock (_lock) _lastCpu = first;
        PrimeGpu();
        await Task.Delay(400);
        var cpu = CpuPercent();
        var gpu = GpuPercent();
        var (used, total) = Memory();
        return new PerformanceSample(cpu, gpu, used, total);
    });

    private int CpuPercent()
    {
        var now = CpuTimes();
        lock (_lock)
        {
            var last = _lastCpu ?? now;
            _lastCpu = now;
            var total = now.Total - last.Total;
            var idle = now.Idle - last.Idle;
            return total == 0 ? 0 : (int)Math.Round(100.0 * (total - idle) / total);
        }
    }

    private static (ulong Idle, ulong Total) CpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return (0, 0);
        // Kernel time includes idle time.
        return (idle, kernel + user);
    }

    /// <summary>The 3D engines' utilization, summed per process like the Task Manager shows it.</summary>
    private void PrimeGpu()
    {
        try
        {
            lock (_lock)
            {
                // Instances come and go with processes; refresh the list now and then.
                if (_gpuCounters is null || DateTime.UtcNow - _gpuCountersAt > TimeSpan.FromSeconds(10))
                {
                    foreach (var c in _gpuCounters ?? new List<PerformanceCounter>()) c.Dispose();
                    var category = new PerformanceCounterCategory("GPU Engine");
                    _gpuCounters = category.GetInstanceNames()
                        .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                        .ToList();
                    _gpuCountersAt = DateTime.UtcNow;
                }
                foreach (var c in _gpuCounters) SafeNext(c);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuCounters = [];
        }
    }

    private int? GpuPercent()
    {
        lock (_lock)
        {
            if (_gpuCounters is not { Count: > 0 }) return null;
            var sum = _gpuCounters.Sum(SafeNext);
            return (int)Math.Clamp(Math.Round(sum), 0, 100);
        }
    }

    private static float SafeNext(PerformanceCounter c)
    {
        try
        {
            return c.NextValue();
        }
        catch (InvalidOperationException)
        {
            // The process behind this instance ended.
            return 0;
        }
    }

    private static (double Used, double Total) Memory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        const double gb = 1024d * 1024 * 1024;
        return (Math.Round((status.ullTotalPhys - status.ullAvailPhys) / gb, 1), Math.Round(status.ullTotalPhys / gb, 1));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
