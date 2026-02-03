using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Topologia (The Topology).
/// Describes the physical layout of the processor.
/// Passed to the Legatus to optimize thread affinity and data locality.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Topologia
{
    /// <summary>
    /// Total number of logical processing units.
    /// </summary>
    public int LogicalCoreCount;

    /// <summary>
    /// Number of Performance Cores (P-Cores).
    /// </summary>
    public int PerformanceCoreCount;

    /// <summary>
    /// Size of the L1 Cache Line in bytes (usually 64).
    /// Critical for avoiding False Sharing.
    /// </summary>
    public int CacheLineSize;

    /// <summary>
    /// Approximate L3 Cache size in bytes.
    /// </summary>
    public long L3CacheSize;

    /// <summary>
    /// Hardware feature flags (AVX2, AVX512, NEON).
    /// </summary>
    public bool HasAvx512;
    public bool HasAvx2;
}