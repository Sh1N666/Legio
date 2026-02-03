using BenchmarkDotNet.Attributes;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Core;

/// <summary>
/// Compares Legio vs .NET Parallel across different workloads.
/// </summary>
[SimpleJob]
[InProcess] // Critical for running smoothly with .NET 9/10 Preview
[MemoryDiagnoser] // Tracks GC Allocations
public class LegioVsNet
{
    // 1 Million items
    [Params(1_000_000)]
    public int ItemCount;

    // Complexity: 
    // 1 = Light (Memory bound / Fast Math)
    // 100 = Heavy (CPU bound / Complex Logic)
    [Params(1, 100)] 
    public int Complexity;

    private Legatus _legatus = null!;
    private HeavyMathSystem _system = null!;

    [GlobalSetup]
    public void Setup()
    {
        _system = new HeavyMathSystem(0, ItemCount, Complexity);
        _legatus = new Legatus(maxCohorts: 16);

        var cohors = new Cohors(0, _system);
        cohors.WriteMask.Tollere(0); 

        _legatus.RegisterCohors(cohors);
        _legatus.ConstructConsilium();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _legatus.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Serial_Loop()
    {
        _system.RunSerial();
    }

    [Benchmark]
    public void Parallel_For()
    {
        _system.RunParallelFor();
    }

    [Benchmark]
    public void Legio_Engine()
    {
        _legatus.ExecuteFrame();
    }
}