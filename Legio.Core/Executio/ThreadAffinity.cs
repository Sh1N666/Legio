using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Protocol;

namespace Legio.Core.Executio;

/// <summary>
/// Cross-platform OS thread affinity pinning helper (Option B.3).
/// Uses sched_setaffinity(2) on Linux and SetThreadAffinityMask on Windows, mapping workers
/// to distinct physical cores first via Mensor's hardware topology map. Strictly 0 B GC allocation.
/// </summary>
internal static unsafe class ThreadAffinity
{
    public static void Apply(int workerId, int affinityHint)
    {
        try
        {
            int logicalCores = Math.Max(1, Mensor.Result.LogicalCoreCount);

            if (OperatingSystem.IsLinux())
            {
                byte* mask = stackalloc byte[128];
                new Span<byte>(mask, 128).Clear();

                if (affinityHint == (int)AffinityMode.PhysicalCoresFirst)
                {
                    int cpu = Mensor.GetPreferredCpuId(workerId);
                    if ((uint)cpu < 1024u)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }
                else if (affinityHint == (int)AffinityMode.PinnedAllLogical)
                {
                    int cpu = workerId % logicalCores;
                    if ((uint)cpu < 1024u)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }
                else
                {
                    int count = Math.Min(logicalCores, 1024);
                    for (int cpu = 0; cpu < count; cpu++)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }

                LinuxSchedSetAffinity(0, 128, mask);
            }
            else if (OperatingSystem.IsWindows())
            {
                int limit = Math.Min(logicalCores, 64);
                ulong maskBits;
                if (affinityHint == (int)AffinityMode.PhysicalCoresFirst)
                {
                    int cpu = Mensor.GetPreferredCpuId(workerId) % limit;
                    maskBits = 1UL << cpu;
                }
                else if (affinityHint == (int)AffinityMode.PinnedAllLogical)
                {
                    int cpu = workerId % limit;
                    maskBits = 1UL << cpu;
                }
                else
                {
                    maskBits = limit == 64 ? ulong.MaxValue : ((1UL << limit) - 1UL);
                }

                SetThreadAffinityMask(GetCurrentThread(), (nuint)maskBits);
            }
        }
        catch
        {
            // Ignore affinity failures in restricted containers / cgroups
        }
    }

    [DllImport("libc", EntryPoint = "sched_setaffinity", ExactSpelling = true)]
    private static extern int LinuxSchedSetAffinity(int pid, nuint cpusetsize, void* mask);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);
}
