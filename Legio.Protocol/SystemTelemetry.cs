using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Blittable snapshot of OS and hardware PMU telemetry sampled periodically by Speculator
/// on the dedicated Sanctum thread (strictly 0 B heap allocation).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SystemTelemetry
{
    /// <summary>
    /// Normalized CPU load of this process alone [0.0 - 1.0] (CLOCK_PROCESS_CPUTIME_ID / GetProcessTimes).
    /// </summary>
    public float ProcessCpuLoad;

    /// <summary>
    /// Normalized total machine CPU utilization [0.0 - 1.0] (/proc/stat on Linux).
    /// </summary>
    public float SystemCpuLoad;

    /// <summary>
    /// Estimated CPU load caused by external processes [0.0 - 1.0] = max(0, SystemCpuLoad - ProcessCpuLoad).
    /// </summary>
    public float ExternalCpuLoad;

    /// <summary>
    /// Hardware Instructions Per Cycle (IPC = RetiredInstructions / CpuCycles) from Linux perf_event_open PMU.
    /// High IPC (&gt; 1.5) indicates compute-bound ALU execution; low IPC (&lt; 0.9) indicates memory/stall bound.
    /// </summary>
    public float InstructionsPerCycle;

    /// <summary>
    /// Hardware LLC/Cache miss ratio [0.0 - 1.0] = CacheMisses / CacheReferences from Linux perf_event_open PMU.
    /// </summary>
    public float CacheMissRate;

    /// <summary>
    /// 1 if real hardware PMU counters (perf_event_open) are active; 0 if unavailable on this OS/container.
    /// </summary>
    public int HasPmuCounters;

    /// <summary>
    /// 1 if external system CPU load (/proc/stat) is active; 0 otherwise.
    /// </summary>
    public int HasExternalCpu;

    private int _padding;
}
