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

    static Mensor()
    {
        Result = SurveyHardware();
    }

    private static Topologia SurveyHardware()
    {
        int logicalCores = Math.Max(1, Environment.ProcessorCount);
        int physicalCores = DetectPhysicalCoreCount(logicalCores);

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

    private static int DetectPhysicalCoreCount(int logicalCores)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Count unique (physical_package_id, core_id) pairs in sysfs (startup-only, once per process).
                var uniqueCores = new HashSet<(int pkg, int core)>();
                for (int cpu = 0; cpu < logicalCores; cpu++)
                {
                    string corePath = $"/sys/devices/system/cpu/cpu{cpu}/topology/core_id";
                    string pkgPath = $"/sys/devices/system/cpu/cpu{cpu}/topology/physical_package_id";
                    if (!File.Exists(corePath)) continue;

                    int coreId = int.Parse(File.ReadAllText(corePath).AsSpan().Trim());
                    int pkgId = File.Exists(pkgPath)
                        ? int.Parse(File.ReadAllText(pkgPath).AsSpan().Trim())
                        : 0;
                    uniqueCores.Add((pkgId, coreId));
                }

                if (uniqueCores.Count > 0)
                    return uniqueCores.Count;
            }
            catch
            {
                // Fall back safely on containerized / restricted environments.
            }
        }

        return logicalCores;
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