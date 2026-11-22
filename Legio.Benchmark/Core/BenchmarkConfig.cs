using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace Legio.Benchmark.Core;

/// <summary>
/// Extended-duration BenchmarkDotNet configuration tailored for real-time game-engine workloads:
/// - 6 warmup + 25 actual iterations @ 500 ms each (~15.5 s per method -> ~180+ periodic Sanctum ML cycles)
/// - Reports Median, P95, and Max alongside Mean/Error/StdDev to expose frame-pacing &amp; tail latency
/// - Includes MemoryDiagnoser to verify strict 0 B GC allocation across all Legio hot paths.
/// </summary>
public sealed class LegioExtendedConfig : ManualConfig
{
    public LegioExtendedConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(6)
            .WithIterationCount(25)
            .WithIterationTime(TimeInterval.FromMilliseconds(500)));

        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.Median);
        AddColumn(StatisticColumn.P95);
        AddColumn(StatisticColumn.Max);
    }
}
