using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Strategia (The Strategy).
/// Directives issued by the Haruspex (Oracle) per Cohort based on periodic analysis of Tesserae.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Strategia
{
    /// <summary>
    /// The optimal size of a data chunk (Centuria) for the current workload.
    /// </summary>
    public int BatchSize;

    /// <summary>
    /// How many Centurions (Threads) should be deployed for this Cohort.
    /// </summary>
    public int ThreadCount;

    /// <summary>
    /// Model confidence score [0.0 - 1.0] (e.g., inversely proportional to fuzzy width / uncertainty).
    /// </summary>
    public float Confidence;

    /// <summary>
    /// Thread pinning mode (0 = Unpinned/OS default, 1 = PhysicalCoresFirst, 2 = PinnedAllLogical).
    /// </summary>
    public int AffinityHint;

    /// <summary>
    /// Adaptive busy-spin budget before parking idle workers in the OS kernel (clamped 64..4096).
    /// </summary>
    public int SpinIterationsBeforePark;

    /// <summary>
    /// Initial chunk partitioning policy (Contiguous for cache locality vs Interleaved to break spatial hotspots).
    /// </summary>
    public PartitionMode PartitionMode;

    /// <summary>
    /// Optio work-stealing granularity (StealOne vs StealHalf batch stealing).
    /// </summary>
    public StealMode StealMode;

    private byte _pad0;
    private byte _pad1;
}