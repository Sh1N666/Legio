using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Blittable hardware SIMD capability flags.
/// </summary>
[Flags]
public enum CpuFeatures : uint
{
    None = 0,
    Avx2 = 1 << 0,
    Avx512F = 1 << 1,
    Neon = 1 << 2
}

/// <summary>
/// Topologia (The Topology).
/// Blittable description of the physical hardware layout and high-resolution timer frequency.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Topologia
{
    /// <summary>
    /// High-resolution Stopwatch ticks per second (OS-dependent: 1 GHz on Linux, 10 MHz on Windows).
    /// </summary>
    public long StopwatchFrequency;

    /// <summary>
    /// Approximate L3 Cache size in bytes (0 if unavailable).
    /// </summary>
    public long L3CacheSize;

    /// <summary>
    /// Total number of logical processing units.
    /// </summary>
    public int LogicalCoreCount;

    /// <summary>
    /// Estimated number of physical cores.
    /// </summary>
    public int PhysicalCoreCount;

    /// <summary>
    /// Number of Performance Cores (P-Cores).
    /// </summary>
    public int PerformanceCoreCount;

    /// <summary>
    /// Size of the L1 Cache Line in bytes (usually 64).
    /// </summary>
    public int CacheLineSize;

    /// <summary>
    /// Hardware SIMD feature flags.
    /// </summary>
    public CpuFeatures Features;

    private uint _padding;

    public readonly bool HasAvx512 => (Features & CpuFeatures.Avx512F) != 0;
    public readonly bool HasAvx2 => (Features & CpuFeatures.Avx2) != 0;
    public readonly bool HasNeon => (Features & CpuFeatures.Neon) != 0;
}