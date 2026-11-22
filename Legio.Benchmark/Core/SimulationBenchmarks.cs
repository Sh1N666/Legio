using BenchmarkDotNet.Attributes;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

namespace Legio.Benchmark.Core;

/// <summary>
/// Benchmarks the 4-Cohort ECS 3D Particle Simulation across Serial, .NET Parallel.For,
/// and all four selectable Legio Haruspex ML models (0 B GC target).
/// </summary>
[MemoryDiagnoser]
public class EcsParticleBenchmark
{
    [Params(65_536)]
    public int ParticleCount;

    private EcsParticleSimulation _sim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sim = new EcsParticleSimulation(ParticleCount);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 100,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(legatus);

        // Let the periodic Sanctum ML thread observe a couple of windows during warmup
        for (int i = 0; i < 12; i++) legatus.ExecuteFrame();
        Thread.Sleep(150);
        for (int i = 0; i < 6; i++) legatus.ExecuteFrame();

        return legatus;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _staticLegatus.Dispose();
        _fuzzySugenoLegatus.Dispose();
        _ofnLegatus.Dispose();
        _banditLegatus.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Serial() => _sim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _sim.RunParallelFor();

    [Benchmark]
    public void Legio_StaticHeuristic() => _staticLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_FuzzySugeno() => _fuzzySugenoLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() => _ofnLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_ContextualBandit() => _banditLegatus.ExecuteFrame();
}

/// <summary>
/// Benchmarks the spatially skewed 3D SDF Raymarching simulation comparing Serial, Parallel.For,
/// and all four selectable Legio Haruspex ML models.
/// </summary>
[MemoryDiagnoser]
public class RaymarchSdfBenchmark
{
    private RaymarchSdfSimulation _sim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sim = new RaymarchSdfSimulation(0, width: 320, height: 180); // 57,600 skewed rays

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 100,
            TelemetrySampleStride = 1,
            InitialBatchSize = 256
        });

        _sim.Register(legatus);

        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();
        Thread.Sleep(150);
        for (int i = 0; i < 4; i++) legatus.ExecuteFrame();

        return legatus;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _staticLegatus.Dispose();
        _fuzzySugenoLegatus.Dispose();
        _ofnLegatus.Dispose();
        _banditLegatus.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Serial() => _sim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _sim.RunParallelFor();

    [Benchmark]
    public void Legio_StaticHeuristic() => _staticLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_FuzzySugeno() => _fuzzySugenoLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() => _ofnLegatus.ExecuteFrame();

    [Benchmark]
    public void Legio_ContextualBandit() => _banditLegatus.ExecuteFrame();
}
