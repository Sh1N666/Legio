using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

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

/// <summary>
/// 1. 4-Cohort ECS 3D Particle Physics Simulation (65,536 entities, 3 DAG phases, 24x dense cluster skew).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class EcsParticleBenchmark
{
    [Params(65_536)]
    public int ParticleCount;

    private EcsParticleSimulation _serialSim = null!;
    private EcsParticleSimulation _parallelSim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new EcsParticleSimulation(ParticleCount);
        _parallelSim = new EcsParticleSimulation(ParticleCount);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var sim = new EcsParticleSimulation(ParticleCount);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        sim.RegisterAll(legatus);

        for (int i = 0; i < 16; i++) legatus.ExecuteFrame();
        Thread.Sleep(120);
        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();

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
    public void Serial() => _serialSim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _parallelSim.RunParallelFor();

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
/// 2. Spatially Skewed 3D SDF Raymarching + Soft Shadows (320x180 = 57,600 rays, compute-bound SMT scaling).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class RaymarchSdfBenchmark
{
    private RaymarchSdfSimulation _serialSim = null!;
    private RaymarchSdfSimulation _parallelSim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new RaymarchSdfSimulation(0, width: 320, height: 180);
        _parallelSim = new RaymarchSdfSimulation(0, width: 320, height: 180);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var sim = new RaymarchSdfSimulation(0, width: 320, height: 180);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 256
        });

        sim.Register(legatus);

        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();
        Thread.Sleep(120);
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
    public void Serial() => _serialSim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _parallelSim.RunParallelFor();

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
/// 3. Complex 12-Cohort AAA Game-Engine Frame Pipeline (6 DAG phases, 10 shared Vexillum resources,
///    RAW/WAR/WAW hazards, mixed 32k heavy cohorts + 128/256 micro-cohorts).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class GameEngineDagBenchmark
{
    private GameEngineDagSimulation _serialSim = null!;
    private GameEngineDagSimulation _parallelSim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new GameEngineDagSimulation(32_768);
        _parallelSim = new GameEngineDagSimulation(32_768);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var sim = new GameEngineDagSimulation(32_768);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 16,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        sim.RegisterAll(legatus);

        for (int i = 0; i < 16; i++) legatus.ExecuteFrame();
        Thread.Sleep(120);
        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();

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
    public void Serial() => _serialSim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _parallelSim.RunParallelFor();

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
/// 4. Classic Craig Reynolds Boids Crowd Flocking (32,768 agents, 5 cohorts, 4 DAG phases,
///    lock-free spatial hash grid with real atomic cache-line contention on 4 dense flocks).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class BoidsSpatialFlockingBenchmark
{
    private BoidsSpatialFlockingSimulation _serialSim = null!;
    private BoidsSpatialFlockingSimulation _parallelSim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new BoidsSpatialFlockingSimulation(32_768);
        _parallelSim = new BoidsSpatialFlockingSimulation(32_768);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var sim = new BoidsSpatialFlockingSimulation(32_768);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        sim.RegisterAll(legatus);

        for (int i = 0; i < 16; i++) legatus.ExecuteFrame();
        Thread.Sleep(120);
        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();

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
    public void Serial() => _serialSim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _parallelSim.RunParallelFor();

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
/// 5. Zero-Allocation Multi-Agent A* Pathfinding (2,048 agents on a 64x64 obstacle labyrinth,
///    2-phase AI DAG, extreme 60x-100x heavy-tailed per-agent execution time variance).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class AStarNavMeshCrowdBenchmark
{
    private AStarNavMeshCrowdSimulation _serialSim = null!;
    private AStarNavMeshCrowdSimulation _parallelSim = null!;
    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new AStarNavMeshCrowdSimulation(2_048);
        _parallelSim = new AStarNavMeshCrowdSimulation(2_048);

        _staticLegatus = CreateAndWarmup(OraculumModel.StaticHeuristic);
        _fuzzySugenoLegatus = CreateAndWarmup(OraculumModel.FuzzySugeno);
        _ofnLegatus = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        _banditLegatus = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private Legatus CreateAndWarmup(OraculumModel model)
    {
        var sim = new AStarNavMeshCrowdSimulation(2_048);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 32
        });

        sim.RegisterAll(legatus);

        for (int i = 0; i < 8; i++) legatus.ExecuteFrame();
        Thread.Sleep(120);
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
    public void Serial() => _serialSim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _parallelSim.RunParallelFor();

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
/// 6. Non-Stationary Dynamic Load-Wave Simulation (49,152 entities, 4 cohorts, 3 DAG phases,
///    4 shifting load regimes per 128-frame cycle: Calm -> Front Explosion -> Storm -> Rear Ambush).
///    Directly tests online ML adaptation and tail stability across abrupt runtime regime transitions.
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class DynamicLoadWaveBenchmark
{
    private DynamicLoadWaveSimulation _serialSim = null!;
    private DynamicLoadWaveSimulation _parallelSim = null!;
    private DynamicLoadWaveSimulation _staticSim = null!;
    private DynamicLoadWaveSimulation _fuzzySim = null!;
    private DynamicLoadWaveSimulation _ofnSim = null!;
    private DynamicLoadWaveSimulation _banditSim = null!;

    private Legatus _staticLegatus = null!;
    private Legatus _fuzzySugenoLegatus = null!;
    private Legatus _ofnLegatus = null!;
    private Legatus _banditLegatus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serialSim = new DynamicLoadWaveSimulation(49_152);
        _parallelSim = new DynamicLoadWaveSimulation(49_152);

        (_staticSim, _staticLegatus) = CreateAndWarmup(OraculumModel.StaticHeuristic);
        (_fuzzySim, _fuzzySugenoLegatus) = CreateAndWarmup(OraculumModel.FuzzySugeno);
        (_ofnSim, _ofnLegatus) = CreateAndWarmup(OraculumModel.OrderedFuzzyNumbers);
        (_banditSim, _banditLegatus) = CreateAndWarmup(OraculumModel.ContextualBandit);
    }

    private static (DynamicLoadWaveSimulation, Legatus) CreateAndWarmup(OraculumModel model)
    {
        var sim = new DynamicLoadWaveSimulation(49_152);
        var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 512
        });

        sim.RegisterAll(legatus);

        for (int i = 0; i < 32; i++) { sim.AdvanceFrameTick(); legatus.ExecuteFrame(); }
        Thread.Sleep(120);
        sim.ResetWave();

        return (sim, legatus);
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
    public void Serial()
    {
        _serialSim.AdvanceFrameTick();
        _serialSim.RunSerial();
    }

    [Benchmark]
    public void Parallel_For()
    {
        _parallelSim.AdvanceFrameTick();
        _parallelSim.RunParallelFor();
    }

    [Benchmark]
    public void Legio_StaticHeuristic()
    {
        _staticSim.AdvanceFrameTick();
        _staticLegatus.ExecuteFrame();
    }

    [Benchmark]
    public void Legio_FuzzySugeno()
    {
        _fuzzySim.AdvanceFrameTick();
        _fuzzySugenoLegatus.ExecuteFrame();
    }

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers()
    {
        _ofnSim.AdvanceFrameTick();
        _ofnLegatus.ExecuteFrame();
    }

    [Benchmark]
    public void Legio_ContextualBandit()
    {
        _banditSim.AdvanceFrameTick();
        _banditLegatus.ExecuteFrame();
    }
}

/// <summary>
/// 7. Brutal CPU-Bound Stochastic Monte Carlo Path Tracing (240x135 x 6 SPP x up to 6 bounces,
///    Fresnel/Snell glass refraction, Russian Roulette variance, 2-phase GI + bilateral denoise DAG).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class MonteCarloPathTracingBenchmark
{
    private MonteCarloPathTracingSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new MonteCarloPathTracingSimulation();

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new MonteCarloPathTracingSimulation();
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 128
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 6; i++) _legatus.ExecuteFrame();
        Thread.Sleep(110);
        for (int i = 0; i < 4; i++) _legatus.ExecuteFrame();
    }

    [GlobalCleanup]
    public void Cleanup() => _legatus?.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial() => _sim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _sim.RunParallelFor();

    [Benchmark]
    public void Legio_StaticHeuristic() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_FuzzySugeno() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_ContextualBandit() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 8. Brutal O(N^2) All-Pairs Astrophysical N-Body Gravity + Soft-Core Collision
///    (3,072 bodies = 9.44 million pairwise force evaluations per frame, 4 cohorts, 3 DAG phases).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class NBodyGravityBenchmark
{
    private NBodyGravitySimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new NBodyGravitySimulation(3_072);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new NBodyGravitySimulation(3_072);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 64
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 6; i++) _legatus.ExecuteFrame();
        Thread.Sleep(110);
        for (int i = 0; i < 4; i++) _legatus.ExecuteFrame();
    }

    [GlobalCleanup]
    public void Cleanup() => _legatus?.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial() => _sim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _sim.RunParallelFor();

    [Benchmark]
    public void Legio_StaticHeuristic() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_FuzzySugeno() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() => _legatus!.ExecuteFrame();

    [Benchmark]
    public void Legio_ContextualBandit() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 9. Extreme 10-Phase Barrier &amp; Memory-Bandwidth Torture Test:
///    256x256 (65,536-vertex) Red-Black Graph-Colored PBD Cloth Physics (10 sequential DAG phases/frame).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class ClothVerletPbdBenchmark
{
    private ClothVerletPbdSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new ClothVerletPbdSimulation();

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new ClothVerletPbdSimulation();
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 16,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 12; i++) { _sim.AdvanceWind(); _legatus.ExecuteFrame(); }
        Thread.Sleep(110);
        for (int i = 0; i < 6; i++) { _sim.AdvanceWind(); _legatus.ExecuteFrame(); }
    }

    [GlobalCleanup]
    public void Cleanup() => _legatus?.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial() => _sim.RunSerial();

    [Benchmark]
    public void Parallel_For() => _sim.RunParallelFor();

    [Benchmark]
    public void Legio_StaticHeuristic() { _sim.AdvanceWind(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_FuzzySugeno() { _sim.AdvanceWind(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() { _sim.AdvanceWind(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_ContextualBandit() { _sim.AdvanceWind(); _legatus!.ExecuteFrame(); }
}
