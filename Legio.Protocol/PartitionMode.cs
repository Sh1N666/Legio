namespace Legio.Protocol;

/// <summary>
/// Controls how initial work chunks are distributed across active Centurions before Optio work stealing.
/// </summary>
public enum PartitionMode : byte
{
    /// <summary>
    /// Each worker receives one contiguous slice of chunks (maximizes spatial L1/L2 cache locality for uniform loads).
    /// </summary>
    Contiguous = 0,

    /// <summary>
    /// Chunks are assigned round-robin/strided across workers (breaks spatial hotspots before Optio stealing is needed).
    /// </summary>
    Interleaved = 1
}

/// <summary>
/// Controls how many chunks a thief Centurion steals per victim interaction.
/// </summary>
public enum StealMode : byte
{
    /// <summary>
    /// Steals a single chunk per attempt (minimal contention when queues are shallow).
    /// </summary>
    StealOne = 0,

    /// <summary>
    /// Steals up to half of a victim's remaining chunks (bounded batch) into the thief's local Optio deque
    /// to amortize CAS contention and dissolve heavy straggler tails faster.
    /// </summary>
    StealHalf = 1
}

/// <summary>
/// Hardware thread-pinning directive issued by the Haruspex Oracle.
/// </summary>
public enum AffinityMode : int
{
    /// <summary>OS scheduler decides core placement (no forced affinity pin).</summary>
    None = 0,

    /// <summary>Pins workers 0..PhysicalCores-1 to distinct physical cores first before using SMT siblings.</summary>
    PhysicalCoresFirst = 1,

    /// <summary>Pins each active worker 1:1 to a distinct logical core.</summary>
    PinnedAllLogical = 2
}
