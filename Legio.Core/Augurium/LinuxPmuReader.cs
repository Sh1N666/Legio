using System.Runtime.InteropServices;

namespace Legio.Core.Augurium;

/// <summary>
/// Zero-allocation Linux hardware PMU reader using perf_event_open(2) with inherit=1 | exclude_kernel=1 | exclude_hv=1.
/// Samples hardware Instructions Per Cycle (IPC) and LLC Cache Miss Rate across Primus and inherited Centurions (Option A.2).
/// Falls back gracefully (IsAvailable = false) when blocked by kernel perf_event_paranoid / container seccomp policy.
/// </summary>
internal sealed unsafe class LinuxPmuReader : IDisposable
{
    private const uint PerfTypeHardware = 0;
    private const ulong PerfCountHwCpuCycles = 0;
    private const ulong PerfCountHwInstructions = 1;
    private const ulong PerfCountHwCacheReferences = 2;
    private const ulong PerfCountHwCacheMisses = 3;
    // flags bitfield at offset 40 of perf_event_attr: inherit(1<<1) | exclude_kernel(1<<5) | exclude_hv(1<<6)
    private const ulong PerfAttrFlagsInheritUser = (1UL << 1) | (1UL << 5) | (1UL << 6);

    private int _cyclesFd = -1;
    private int _instructionsFd = -1;
    private int _cacheRefsFd = -1;
    private int _cacheMissesFd = -1;

    private ulong _lastCycles;
    private ulong _lastInstructions;
    private ulong _lastCacheRefs;
    private ulong _lastCacheMisses;
    private bool _disposed;

    public bool IsAvailable { get; }

    public LinuxPmuReader()
    {
        IsAvailable = OperatingSystem.IsLinux() && TryInit();
    }

    /// <summary>
    /// Samples incremental IPC and LLC Cache Miss Rate since the previous call (0 B GC allocation).
    /// </summary>
    public void Sample(ref float ipc, ref float cacheMissRate)
    {
        if (!IsAvailable || _disposed)
            return;

        ulong cycles = ReadCounter(_cyclesFd, _lastCycles);
        ulong instructions = ReadCounter(_instructionsFd, _lastInstructions);
        ulong cacheRefs = ReadCounter(_cacheRefsFd, _lastCacheRefs);
        ulong cacheMisses = ReadCounter(_cacheMissesFd, _lastCacheMisses);

        ulong dCycles = cycles >= _lastCycles ? cycles - _lastCycles : 0;
        ulong dInstr = instructions >= _lastInstructions ? instructions - _lastInstructions : 0;
        ulong dRefs = cacheRefs >= _lastCacheRefs ? cacheRefs - _lastCacheRefs : 0;
        ulong dMisses = cacheMisses >= _lastCacheMisses ? cacheMisses - _lastCacheMisses : 0;

        _lastCycles = cycles;
        _lastInstructions = instructions;
        _lastCacheRefs = cacheRefs;
        _lastCacheMisses = cacheMisses;

        if (dCycles > 1000)
        {
            ipc = (float)Math.Clamp((double)dInstr / dCycles, 0.0, 8.0);
        }

        if (dRefs > 100)
        {
            cacheMissRate = (float)Math.Clamp((double)dMisses / dRefs, 0.0, 1.0);
        }
    }

    private bool TryInit()
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
            _cyclesFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCpuCycles);
            _instructionsFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwInstructions);
            _cacheRefsFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCacheReferences);
            _cacheMissesFd = OpenHardwareCounter(sysPerfEventOpen, PerfCountHwCacheMisses);

            if (_cyclesFd >= 0 && _instructionsFd >= 0 && _cacheRefsFd >= 0 && _cacheMissesFd >= 0)
            {
                _lastCycles = ReadCounter(_cyclesFd, 0);
                _lastInstructions = ReadCounter(_instructionsFd, 0);
                _lastCacheRefs = ReadCounter(_cacheRefsFd, 0);
                _lastCacheMisses = ReadCounter(_cacheMissesFd, 0);
                return true;
            }
        }
        catch
        {
            // Fall back gracefully when PMU is blocked by kernel/container policy
        }

        CloseAllFds();
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

    private static ulong ReadCounter(int fd, ulong fallback)
    {
        if (fd < 0) return fallback;
        ulong value = 0;
        nint n = LinuxRead(fd, &value, (nuint)sizeof(ulong));
        return n == sizeof(ulong) ? value : fallback;
    }

    private void CloseAllFds()
    {
        CloseFd(ref _cyclesFd);
        CloseFd(ref _instructionsFd);
        CloseFd(ref _cacheRefsFd);
        CloseFd(ref _cacheMissesFd);
    }

    private static void CloseFd(ref int fd)
    {
        if (fd >= 0)
        {
            LinuxClose(fd);
            fd = -1;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAllFds();
    }

    [DllImport("libc", EntryPoint = "read", ExactSpelling = true)]
    private static extern nint LinuxRead(int fd, void* buf, nuint count);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int LinuxClose(int fd);

    [DllImport("libc", EntryPoint = "syscall", ExactSpelling = true)]
    private static extern long LinuxSyscallPerf(long number, void* attr, int pid, int cpu, int groupFd, ulong flags);
}
