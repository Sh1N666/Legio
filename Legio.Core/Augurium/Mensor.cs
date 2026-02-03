using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Mensor (The Surveyor).
/// Static utility responsible for analyzing the hardware terrain once at startup.
/// </summary>
public static class Mensor
{
    /// <summary>
    /// The cached result of the survey.
    /// </summary>
    public static readonly Topologia Result;

    static Mensor()
    {
        Result = SurveyHardware();
    }

    /// <summary>
    /// Performs the hardware survey.
    /// </summary>
    private static Topologia SurveyHardware()
    {
        var topo = new Topologia();
        
        topo.LogicalCoreCount = Environment.ProcessorCount;
        
        // Default assumption: All cores are "Performance" unless detected otherwise.
        // Deep P/E core detection requires OS-specific API calls (GetLogicalProcessorInformationEx on Windows).
        // For the MVP, we assume uniform topology or manual configuration.
        topo.PerformanceCoreCount = topo.LogicalCoreCount; 

        // Detect Cache Line Size (Heuristic default is 64 bytes for x64/ARM64).
        topo.CacheLineSize = 64; 

        // Detect SIMD capabilities
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            topo.HasAvx512 = false;
            topo.HasAvx2 = AdvSimd.IsSupported; // Mapping NEON to AVX2 level for logic simplicity
        }
        else
        {
            topo.HasAvx512 = Avx512F.IsSupported;
            topo.HasAvx2 = Avx2.IsSupported;
        }

        // L3 Cache estimation (Placeholder).
        // Real implementation requires CPUID leaf 0x4 (Intel) or 0x8000001D (AMD).
        topo.L3CacheSize = 0; 

        return topo;
    }

    /// <summary>
    /// Checks if the current thread is running on a Performance Core.
    /// (This is a simplified heuristic; real implementation needs OS thread ID checks).
    /// </summary>
    public static bool IsPerformanceCore(int threadIndex)
    {
        // In hybrid architectures (Intel 12th+ gen), P-Cores usually have lower indices
        // if thread affinity is not manually scrambled.
        return threadIndex < Result.PerformanceCoreCount;
    }
}