using System.Diagnostics;
using System.Runtime.InteropServices;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Speculator (The Scout).
/// Monitors process CPU load, external system CPU load (via <see cref="LinuxProcStatReader"/>),
/// and hardware PMU counters (IPC &amp; LLC Cache Miss Rate via <see cref="LinuxPmuReader"/>)
/// with strictly zero heap allocations.
/// </summary>
public sealed class Speculator : IDisposable
{
    private const int ClockProcessCputimeId = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long TvSec;
        public long TvNsec;
    }

    private readonly int _logicalCores;
    private readonly long _minIntervalTicks;
    private readonly bool _useLinuxSyscall;
    private readonly bool _useWindowsSyscall;
    private readonly Process? _fallbackProcess;
    private readonly IntPtr _windowsProcessHandle;
    private readonly LinuxProcStatReader _procStatReader;
    private readonly LinuxPmuReader _pmuReader;

    private long _lastCpuTimeNs;
    private long _lastCheckTimestamp;
    private bool _disposed;

    /// <summary>
    /// Current normalized CPU load of the process [0.0 - 1.0].
    /// </summary>
    public volatile float ProcessCpuLoad;

    /// <summary>
    /// Current normalized total machine CPU load [0.0 - 1.0].
    /// </summary>
    public volatile float SystemCpuLoad;

    /// <summary>
    /// Estimated external system CPU load [0.0 - 1.0] = max(0, SystemCpuLoad - ProcessCpuLoad).
    /// </summary>
    public volatile float ExternalCpuLoad;

    /// <summary>
    /// Hardware Instructions Per Cycle (IPC) sampled across Primus and inherited Centurion threads.
    /// </summary>
    public volatile float InstructionsPerCycle;

    /// <summary>
    /// Hardware LLC/Cache miss ratio [0.0 - 1.0] = CacheMisses / CacheReferences.
    /// </summary>
    public volatile float CacheMissRate;

    public bool HasPmuCounters => _pmuReader.IsAvailable;
    public bool HasExternalCpu => _procStatReader.IsAvailable;

    public SystemTelemetry CurrentTelemetry => new()
    {
        ProcessCpuLoad = ProcessCpuLoad,
        SystemCpuLoad = SystemCpuLoad,
        ExternalCpuLoad = ExternalCpuLoad,
        InstructionsPerCycle = InstructionsPerCycle,
        CacheMissRate = CacheMissRate,
        HasPmuCounters = HasPmuCounters ? 1 : 0,
        HasExternalCpu = HasExternalCpu ? 1 : 0
    };

    public Speculator(int updateIntervalMs = 500)
    {
        _logicalCores = Math.Max(1, Environment.ProcessorCount);
        int clampedMs = Math.Max(10, updateIntervalMs);

        // Normalize interval to actual OS Stopwatch.Frequency (1 GHz on Linux, 10 MHz on Windows).
        _minIntervalTicks = Math.Max(1L, (long)(clampedMs * (Stopwatch.Frequency / 1000.0)));

        _useLinuxSyscall = OperatingSystem.IsLinux();
        _useWindowsSyscall = OperatingSystem.IsWindows();

        if (_useWindowsSyscall)
        {
            _windowsProcessHandle = GetCurrentProcess();
        }
        else if (!_useLinuxSyscall)
        {
            _fallbackProcess = Process.GetCurrentProcess();
        }

        _procStatReader = new LinuxProcStatReader();
        _pmuReader = new LinuxPmuReader();

        _lastCpuTimeNs = ReadProcessCpuTimeNs();
        _lastCheckTimestamp = Stopwatch.GetTimestamp();
        ProcessCpuLoad = 0.0f;
        SystemCpuLoad = 0.0f;
        ExternalCpuLoad = 0.0f;
        InstructionsPerCycle = 0.0f;
        CacheMissRate = 0.0f;
    }

    /// <summary>
    /// Performs a throttled zero-allocation reconnaissance check.
    /// Called periodically by the dedicated Sanctum background thread.
    /// </summary>
    public void Explorare()
    {
        if (_disposed) return;

        long currentTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = currentTimestamp - _lastCheckTimestamp;
        if (elapsedTicks < _minIntervalTicks)
            return;

        // 1. Own process CPU load
        long currentCpuTimeNs = ReadProcessCpuTimeNs();
        long cpuDeltaNs = currentCpuTimeNs - _lastCpuTimeNs;

        float procLoad = ProcessCpuLoad;
        double wallDeltaNs = (double)elapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency;
        if (wallDeltaNs > 0.0 && cpuDeltaNs >= 0)
        {
            double load = (cpuDeltaNs / wallDeltaNs) / _logicalCores;
            procLoad = (float)Math.Clamp(load, 0.0, 1.0);
            ProcessCpuLoad = procLoad;
        }

        _lastCpuTimeNs = currentCpuTimeNs;
        _lastCheckTimestamp = currentTimestamp;

        // 2. External system CPU load from /proc/stat (Option A.3, 0 B alloc)
        if (_procStatReader.TrySampleSystemCpuLoad(out float sysLoad))
        {
            SystemCpuLoad = sysLoad;
            ExternalCpuLoad = (float)Math.Clamp(sysLoad - procLoad, 0.0, 1.0);
        }

        // 3. Hardware PMU counters: IPC & LLC Cache Miss Rate (Option A.2, 0 B alloc)
        if (_pmuReader.IsAvailable)
        {
            float ipc = InstructionsPerCycle;
            float missRate = CacheMissRate;
            _pmuReader.Sample(ref ipc, ref missRate);
            InstructionsPerCycle = ipc;
            CacheMissRate = missRate;
        }
    }

    private long ReadProcessCpuTimeNs()
    {
        if (_useLinuxSyscall)
        {
            if (ClockGetTime(ClockProcessCputimeId, out Timespec ts) == 0)
            {
                return ts.TvSec * 1_000_000_000L + ts.TvNsec;
            }
            return _lastCpuTimeNs;
        }

        if (_useWindowsSyscall)
        {
            if (GetProcessTimes(_windowsProcessHandle, out _, out _, out long kernel, out long user))
            {
                // FILETIME unit is 100 nanoseconds.
                return (kernel + user) * 100L;
            }
            return _lastCpuTimeNs;
        }

        return _fallbackProcess != null
            ? (long)(_fallbackProcess.TotalProcessorTime.TotalMilliseconds * 1_000_000.0)
            : 0L;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pmuReader.Dispose();
        _procStatReader.Dispose();
        _fallbackProcess?.Dispose();
    }

    [DllImport("libc", EntryPoint = "clock_gettime", ExactSpelling = true)]
    private static extern int ClockGetTime(int clockId, out Timespec tp);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);
}