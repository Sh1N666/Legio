using System.Diagnostics;

namespace Legio.Core.Augurium;

/// <summary>
/// Speculator (The Scout).
/// Monitors the dynamic state of the application environment.
/// Calculates CPU load to inform the Haruspex about thermal/power constraints.
/// </summary>
public class Speculator
{
    private readonly Process _currentProcess;
    private readonly int _logicalCores;
    
    // State for CPU load calculation
    private TimeSpan _lastTotalProcessorTime;
    private long _lastCheckTimestamp;
    
    // Throttling: How often (in ticks) to query the OS kernel.
    private readonly long _minIntervalTicks;

    /// <summary>
    /// Current normalized CPU load of the process [0.0 - 1.0].
    /// </summary>
    public volatile float ProcessCpuLoad;

    public Speculator(int updateIntervalMs = 500)
    {
        _currentProcess = Process.GetCurrentProcess();
        _logicalCores = Environment.ProcessorCount;
        _minIntervalTicks = updateIntervalMs * 10_000; // 10k ticks per ms

        _lastTotalProcessorTime = _currentProcess.TotalProcessorTime;
        _lastCheckTimestamp = Stopwatch.GetTimestamp();
        ProcessCpuLoad = 0.0f;
    }

    /// <summary>
    /// Performs a reconnaissance mission.
    /// Should be called periodically (e.g., by the Sanctum thread).
    /// </summary>
    public void Explorare()
    {
        long currentTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = currentTimestamp - _lastCheckTimestamp;

        if (elapsedTicks < _minIntervalTicks) return;

        // Expensive Kernel Call
        TimeSpan currentTotalProcessorTime = _currentProcess.TotalProcessorTime;
        
        double cpuUsedMs = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
        double wallClockPassedMs = (double)elapsedTicks / Stopwatch.Frequency * 1000.0;

        double load = (cpuUsedMs / wallClockPassedMs) / _logicalCores;

        ProcessCpuLoad = (float)Math.Clamp(load, 0.0, 1.0);

        _lastTotalProcessorTime = currentTotalProcessorTime;
        _lastCheckTimestamp = currentTimestamp;
    }
}