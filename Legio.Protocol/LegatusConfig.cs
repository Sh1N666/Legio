namespace Legio.Protocol;

/// <summary>
/// Configuration passed when creating a Legatus scheduler instance.
/// </summary>
public sealed class LegatusConfig
{
    /// <summary>Maximum number of registered Cohorts (systems).</summary>
    public int MaxCohorts { get; init; } = 64;

    /// <summary>Total worker threads including the calling thread (Primus). 0 = auto (LogicalCoreCount).</summary>
    public int ThreadCount { get; init; } = 0;

    /// <summary>ML / Fuzzy model used by the F# Haruspex Oracle.</summary>
    public OraculumModel OracleModel { get; init; } = OraculumModel.FuzzySugeno;

    /// <summary>How often (in ms) the dedicated Sanctum thread drains telemetry and updates strategies.</summary>
    public int OracleIntervalMs { get; init; } = 150;

    /// <summary>Record telemetry every N frames (1 = every frame, higher = lower overhead on ultra-fast frames).</summary>
    public int TelemetrySampleStride { get; init; } = 1;

    /// <summary>Capacity (power of 2) of each Centurion's Optio work-stealing deque.</summary>
    public int OptioCapacity { get; init; } = 4096;

    /// <summary>Capacity (power of 2) of the Circulus telemetry ring buffer.</summary>
    public int CirculusCapacity { get; init; } = 4096;

    /// <summary>Speculator OS CPU-load sampling interval in milliseconds.</summary>
    public int CpuMonitorIntervalMs { get; init; } = 500;

    /// <summary>Initial batch size used before the Oracle finishes its first observation window.</summary>
    public int InitialBatchSize { get; init; } = 1024;

    /// <summary>Optional diagnostic callback invoked on Oracle load/execution warnings.</summary>
    public Action<string>? DiagnosticSink { get; init; }

    public static LegatusConfig Default => new();
}
