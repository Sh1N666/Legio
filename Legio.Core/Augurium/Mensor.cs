using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Mensor (The Surveyor).
/// Static utility responsible for analyzing the hardware terrain once at startup.
/// </summary>
public static class Mensor
{
    /// <summary>
    /// The cached result of the hardware survey.
    /// </summary>
    public static readonly Topologia Result;

    private static readonly int[] _physicalFirstCpuMap;

    static Mensor()
    {
        int logicalCores = Math.Max(1, Environment.ProcessorCount);
        _physicalFirstCpuMap = BuildPhysicalFirstCpuMap(logicalCores, out int physicalCores);
        Result = SurveyHardware(logicalCores, physicalCores);
    }

    /// <summary>
    /// Maps a worker index [0..N-1] to a logical CPU ID such that indices [0..PhysicalCoreCount-1]
    /// always occupy distinct physical cores before assigning SMT siblings. Zero allocation.
    /// </summary>
    public static int GetPreferredCpuId(int workerIndex)
    {
        if (_physicalFirstCpuMap.Length == 0)
            return 0;

        int idx = workerIndex < 0 ? 0 : workerIndex % _physicalFirstCpuMap.Length;
        return _physicalFirstCpuMap[idx];
    }

    private static Topologia SurveyHardware(int logicalCores, int physicalCores)
    {
        CpuFeatures features = CpuFeatures.None;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (AdvSimd.IsSupported) features |= CpuFeatures.Neon;
        }
        else
        {
            if (Avx512F.IsSupported) features |= CpuFeatures.Avx512F;
            if (Avx2.IsSupported) features |= CpuFeatures.Avx2;
        }

        return new Topologia
        {
            StopwatchFrequency = Stopwatch.Frequency,
            L3CacheSize = DetectL3CacheSize(),
            LogicalCoreCount = logicalCores,
            PhysicalCoreCount = physicalCores,
            PerformanceCoreCount = physicalCores,
            CacheLineSize = 64,
            Features = features
        };
    }

    private static int[] BuildPhysicalFirstCpuMap(int logicalCores, out int physicalCores)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Partition logical CPUs into primary physical-core leaders first, then SMT siblings.
                var seenCores = new HashSet<(int pkg, int core)>();
                var primaryCpus = new List<int>(logicalCores);
                var siblingCpus = new List<int>(logicalCores);

                for (int cpu = 0; cpu < logicalCores; cpu++)
                {
                    string corePath = $"/sys/devices/system/cpu/cpu{cpu}/topology/core_id";
                    string pkgPath = $"/sys/devices/system/cpu/cpu{cpu}/topology/physical_package_id";
                    if (!File.Exists(corePath)) continue;

                    int coreId = int.Parse(File.ReadAllText(corePath).AsSpan().Trim());
                    int pkgId = File.Exists(pkgPath)
                        ? int.Parse(File.ReadAllText(pkgPath).AsSpan().Trim())
                        : 0;

                    if (seenCores.Add((pkgId, coreId)))
                        primaryCpus.Add(cpu);
                    else
                        siblingCpus.Add(cpu);
                }

                if (primaryCpus.Count > 0)
                {
                    physicalCores = primaryCpus.Count;
                    var ordered = new int[primaryCpus.Count + siblingCpus.Count];
                    int pos = 0;
                    for (int i = 0; i < primaryCpus.Count; i++) ordered[pos++] = primaryCpus[i];
                    for (int i = 0; i < siblingCpus.Count; i++) ordered[pos++] = siblingCpus[i];
                    return ordered;
                }
            }
            catch
            {
                // Fall back safely on containerized / restricted environments.
            }
        }

        physicalCores = logicalCores;
        var identity = new int[logicalCores];
        for (int i = 0; i < logicalCores; i++)
            identity[i] = i;
        return identity;
    }

    private static long DetectL3CacheSize()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string path = "/sys/devices/system/cpu/cpu0/cache/index3/size";
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path).Trim();
                    if (raw.EndsWith('K') && long.TryParse(raw.AsSpan(0, raw.Length - 1), out long kb))
                        return kb * 1024;
                    if (raw.EndsWith('M') && long.TryParse(raw.AsSpan(0, raw.Length - 1), out long mb))
                        return mb * 1024 * 1024;
                }
            }
            catch
            {
                // Ignore if sysfs cache info is unavailable.
            }
        }

        return 0;
    }

    /// <summary>
    /// Checks if the given thread index maps to a performance core heuristic.
    /// </summary>
    public static bool IsPerformanceCore(int threadIndex)
    {
        return threadIndex >= 0 && threadIndex < Result.PerformanceCoreCount;
    }
}