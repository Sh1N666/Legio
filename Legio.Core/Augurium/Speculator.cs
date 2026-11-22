using System.Diagnostics;
using System.Runtime.InteropServices;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Speculator (The Scout).
/// Monitors process CPU load, external system CPU load (/proc/stat), and hardware PMU counters
/// (IPC &amp; LLC Cache Miss Rate via Linux perf_event_open with inherit=1) with strictly zero heap allocations.
/// </summary>
public sealed unsafe class Speculator : IDisposable
{
    private const int ClockProcessCputimeId = 2;
    private const int OpenReadOnly = 0;

    // Linux perf_event_open constants
    private const uint PerfTypeHardware = 0;
    private const ulong PerfCountHwCpuCycles = 0;
    private const ulong PerfCountHwInstructions = 1;
    private const ulong PerfCountHwCacheReferences = 2;
    private const ulong PerfCountHwCacheMisses = 3;
    // flags bitfield at offset 40 of perf_event_attr: inherit(1<<1) | exclude_kernel(1<<5) | exclude_hv(1<<6)
    private const ulong PerfAttrFlagsInheritUser = (1UL << 1) | (1UL << 5) | (1UL << 6);

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

    // Linux /proc/stat persistent fd + zero-alloc buffer (Option A.3: ExternalCpuLoad)
    private int _procStatFd = -1;
    private readonly byte[] _procStatBuffer;
    private ulong _lastSysTotalJiffies;
    private ulong _lastSysIdleJiffies;

    // Linux perf_event_open PMU file descriptors (Option A.2: IPC & CacheMissRate)
    private int _pmuCyclesFd = -1;
    private int _pmuInstructionsFd = -1;
    private int _pmuCacheRefsFd = -1;
    private int _pmuCacheMissesFd = -1;
    private ulong _lastPmuCycles;
    private ulong _lastPmuInstructions;
    private ulong _lastPmuCacheRefs;
    private ulong _lastPmuCacheMisses;

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

    public bool HasPmuCounters { get; }
    public bool HasExternalCpu { get; }

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
        _procStatBuffer = _useLinuxSyscall ? new byte[256] : Array.Empty<byte>();

        if (_useWindowsSyscall)
        {
            _windowsProcessHandle = GetCurrentProcess();
        }
        else if (!_useLinuxSyscall)
        {
            _fallbackProcess = Process.GetCurrentProcess();
        }

        if (_useLinuxSyscall)
        {
            HasExternalCpu = TryInitProcStat();
            HasPmuCounters = TryInitLinuxPmu();
        }

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
        if (_procStatFd >= 0 && TryReadProcStatJiffies(out ulong totalJiffies, out ulong idleJiffies))
        {
            ulong dTotal = totalJiffies >= _lastSysTotalJiffies ? totalJiffies - _lastSysTotalJiffies : 0;
            ulong dIdle = idleJiffies >= _lastSysIdleJiffies ? idleJiffies - _lastSysIdleJiffies : 0;
            _lastSysTotalJiffies = totalJiffies;
            _lastSysIdleJiffies = idleJiffies;

            if (dTotal > 0)
            {
                double busy = dTotal > dIdle ? (double)(dTotal - dIdle) / dTotal : 0.0;
                float sysLoad = (float)Math.Clamp(busy, 0.0, 1.0);
                SystemCpuLoad = sysLoad;
                ExternalCpuLoad = (float)Math.Clamp(sysLoad - procLoad, 0.0, 1.0);
            }
        }

        // 3. Hardware PMU counters: IPC & LLC Cache Miss Rate (Option A.2, 0 B alloc)
        if (HasPmuCounters)
        {
            ulong cycles = ReadPmuCounter(_pmuCyclesFd, _lastPmuCycles);
            ulong instructions = ReadPmuCounter(_pmuInstructionsFd, _lastPmuInstructions);
            ulong cacheRefs = ReadPmuCounter(_pmuCacheRefsFd, _lastPmuCacheRefs);
            ulong cacheMisses = ReadPmuCounter(_pmuCacheMissesFd, _lastPmuCacheMisses);

            ulong dCycles = cycles >= _lastPmuCycles ? cycles - _lastPmuCycles : 0;
            ulong dInstr = instructions >= _lastPmuInstructions ? instructions - _lastPmuInstructions : 0;
            ulong dRefs = cacheRefs >= _lastPmuCacheRefs ? cacheRefs - _lastPmuCacheRefs : 0;
            ulong dMisses = cacheMisses >= _lastPmuCacheMisses ? cacheMisses - _lastPmuCacheMisses : 0;

            _lastPmuCycles = cycles;
            _lastPmuInstructions = instructions;
            _lastPmuCacheRefs = cacheRefs;
            _lastPmuCacheMisses = cacheMisses;

            if (dCycles > 1000)
            {
                InstructionsPerCycle = (float)Math.Clamp((double)dInstr / dCycles, 0.0, 8.0);
            }

            if (dRefs > 100)
            {
                CacheMissRate = (float)Math.Clamp((double)dMisses / dRefs, 0.0, 1.0);
            }
        }
    }

    private bool TryInitProcStat()
    {
        try
        {
            ReadOnlySpan<byte> path = "/proc/stat\0"u8;
            fixed (byte* pPath = path)
            {
                _procStatFd = LinuxOpen(pPath, OpenReadOnly);
            }

            if (_procStatFd >= 0 && TryReadProcStatJiffies(out ulong total, out ulong idle))
            {
                _lastSysTotalJiffies = total;
                _lastSysIdleJiffies = idle;
                return true;
            }
        }
        catch
        {
            // Ignore in restricted environments
        }

        if (_procStatFd >= 0)
        {
            LinuxClose(_procStatFd);
            _procStatFd = -1;
        }
        return false;
    }

    private bool TryReadProcStatJiffies(out ulong totalJiffies, out ulong idleJiffies)
    {
        totalJiffies = 0;
        idleJiffies = 0;

        nint bytesRead;
        fixed (byte* pBuf = _procStatBuffer)
        {
            bytesRead = LinuxPread(_procStatFd, pBuf, (nuint)_procStatBuffer.Length, 0);
        }

        if (bytesRead <= 4)
            return false;

        // First line format: "cpu  user nice system idle iowait irq softirq steal ..."
        int len = (int)bytesRead;
        int idx = 0;

        // Skip leading "cpu" prefix and spaces
        while (idx < len && _procStatBuffer[idx] != (byte)' ' && _procStatBuffer[idx] != (byte)'\n')
            idx++;

        int field = 0;
        ulong sum = 0;
        ulong idleSum = 0;

        while (idx < len && _procStatBuffer[idx] != (byte)'\n')
        {
            while (idx < len && _procStatBuffer[idx] == (byte)' ')
                idx++;

            if (idx >= len || _procStatBuffer[idx] == (byte)'\n')
                break;

            ulong val = 0;
            bool anyDigit = false;
            while (idx < len)
            {
                byte ch = _procStatBuffer[idx];
                if (ch < (byte)'0' || ch > (byte)'9')
                    break;
                val = val * 10UL + (ulong)(ch - (byte)'0');
                anyDigit = true;
                idx++;
            }

            if (!anyDigit)
                break;

            sum += val;
            // field 3 = idle, field 4 = iowait
            if (field == 3 || field == 4)
                idleSum += val;

            field++;
            if (field >= 10)
                break;
        }

        if (field < 4)
            return false;

        totalJiffies = sum;
        idleJiffies = idleSum;
        return true;
    }

    private bool TryInitLinuxPmu()
    {
        long sysPerfEventOpen = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 298L,
            Architecture.Arm64 => 241L,
            _ => -1L
        };

        if (sysPerfEventOpen < 0)
            return false;

        try
        {
            _pmuCyclesFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCpuCycles);
            _pmuInstructionsFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwInstructions);
            _pmuCacheRefsFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCacheReferences);
            _pmuCacheMissesFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCacheMisses);

            if (_pmuCyclesFd >= 0 && _pmuInstructionsFd >= 0 && _pmuCacheRefsFd >= 0 && _pmuCacheMissesFd >= 0)
            {
                _lastPmuCycles = ReadPmuCounter(_pmuCyclesFd, 0);
                _lastPmuInstructions = ReadPmuCounter(_pmuInstructionsFd, 0);
                _lastPmuCacheRefs = ReadPmuCounter(_pmuCacheRefsFd, 0);
                _lastPmuCacheMisses = ReadPmuCounter(_pmuCacheMissesFd, 0);
                return true;
            }
        }
        catch
        {
            // Fall back gracefully when PMU is blocked by kernel/container policy
        }

        ClosePmuFds();
        return false;
    }

    private static int OpenHardwareCounter(long sysPerfEventOpen, ulong config)
    {
        byte* attr = stackalloc byte[128];
        new Span<byte>(attr, 128).Clear();

        *(uint*)(attr + 0) = PerfTypeHardware;
        *(uint*)(attr + 4) = 128u;
        *(ulong*)(attr + 8) = config;
        *(ulong*)(attr + 40) = PerfAttrFlagsInheritUser;

        long fd = LinuxSyscallPerf(sysPerfEventOpen, attr, 0, -1, -1, 0UL);
        return fd >= 0 ? (int)fd : -1;
    }

    private static ulong ReadPmuCounter(int fd, ulong fallback)
    {
        if (fd < 0) return fallback;
        ulong value = 0;
        nint n = LinuxRead(fd, &value, (nuint)sizeof(ulong));
        return n == sizeof(ulong) ? value : fallback;
    }

    private void ClosePmuFds()
    {
        CloseFd(ref _pmuCyclesFd);
        CloseFd(ref _pmuInstructionsFd);
        CloseFd(ref _pmuCacheRefsFd);
        CloseFd(ref _pmuCacheMissesFd);
    }

    private static void CloseFd(ref int fd)
    {
        if (fd >= 0)
        {
            LinuxClose(fd);
            fd = -1;
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

        ClosePmuFds();
        CloseFd(ref _procStatFd);
        _fallbackProcess?.Dispose();
    }

    [DllImport("libc", EntryPoint = "clock_gettime", ExactSpelling = true)]
    private static extern int ClockGetTime(int clockId, out Timespec tp);

    [DllImport("libc", EntryPoint = "open", ExactSpelling = true)]
    private static extern int LinuxOpen(byte* pathname, int flags);

    [DllImport("libc", EntryPoint = "pread", ExactSpelling = true)]
    private static extern nint LinuxPread(int fd, void* buf, nuint count, long offset);

    [DllImport("libc", EntryPoint = "read", ExactSpelling = true)]
    private static extern nint LinuxRead(int fd, void* buf, nuint count);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int LinuxClose(int fd);

    [DllImport("libc", EntryPoint = "syscall", ExactSpelling = true)]
    private static extern long LinuxSyscallPerf(long number, void* attr, int pid, int cpu, int groupFd, ulong flags);

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