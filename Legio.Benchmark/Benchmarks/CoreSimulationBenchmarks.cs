using BenchmarkDotNet.Attributes;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

namespace Legio.Benchmark.Core;

/// <summary>
/// 1. 4-Cohort ECS 3D Particle Physics Simulation (65,536 entities, 3 DAG phases, 24x dense cluster skew).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class EcsParticleBenchmark
{
    [Params(65_536)]
    public int ParticleCount;

    private EcsParticleSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new EcsParticleSimulation(ParticleCount);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new EcsParticleSimulation(ParticleCount);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 16; i++) _legatus.ExecuteFrame();
        Thread.Sleep(110);
        for (int i = 0; i < 8; i++) _legatus.ExecuteFrame();
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 2. Spatially Skewed 3D SDF Raymarching + Soft Shadows (320x180 = 57,600 rays, compute-bound SMT scaling).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class RaymarchSdfBenchmark
{
    private RaymarchSdfSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new RaymarchSdfSimulation(0, width: 320, height: 180);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new RaymarchSdfSimulation(0, width: 320, height: 180);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 256
        });

        _sim.Register(_legatus);
        for (int i = 0; i < 8; i++) _legatus.ExecuteFrame();
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 3. Complex 12-Cohort AAA Game-Engine Frame Pipeline (6 DAG phases, 10 shared Vexillum resources,
///    RAW/WAR/WAW hazards, mixed 32k heavy cohorts + 128/256 micro-cohorts).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class GameEngineDagBenchmark
{
    private GameEngineDagSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new GameEngineDagSimulation(32_768);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new GameEngineDagSimulation(32_768);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 16,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 16; i++) _legatus.ExecuteFrame();
        Thread.Sleep(110);
        for (int i = 0; i < 8; i++) _legatus.ExecuteFrame();
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 4. Classic Craig Reynolds Boids Crowd Flocking (32,768 agents, 5 cohorts, 4 DAG phases,
///    lock-free spatial hash grid with real atomic cache-line contention on 4 dense flocks).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class BoidsSpatialFlockingBenchmark
{
    private BoidsSpatialFlockingSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new BoidsSpatialFlockingSimulation(32_768);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new BoidsSpatialFlockingSimulation(32_768);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 2,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 16; i++) _legatus.ExecuteFrame();
        Thread.Sleep(110);
        for (int i = 0; i < 8; i++) _legatus.ExecuteFrame();
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 5. Zero-Allocation Multi-Agent A* Pathfinding (2,048 agents on a 64x64 obstacle labyrinth,
///    2-phase AI DAG, extreme 60x-100x heavy-tailed per-agent execution time variance).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class AStarNavMeshCrowdBenchmark
{
    private AStarNavMeshCrowdSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new AStarNavMeshCrowdSimulation(2_048);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new AStarNavMeshCrowdSimulation(2_048);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 32
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 8; i++) _legatus.ExecuteFrame();
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 6. Non-Stationary Dynamic Load-Wave Simulation (49,152 entities, 4 cohorts, 3 DAG phases,
///    4 shifting load regimes per 128-frame cycle: Calm -> Front Explosion -> Storm -> Rear Ambush).
///    Directly tests online ML adaptation and tail stability across abrupt runtime regime transitions.
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class DynamicLoadWaveBenchmark
{
    private DynamicLoadWaveSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new DynamicLoadWaveSimulation(49_152);

    [GlobalSetup(Target = nameof(Legio_StaticHeuristic))]
    public void SetupStatic() => SetupLegio(OraculumModel.StaticHeuristic);

    [GlobalSetup(Target = nameof(Legio_FuzzySugeno))]
    public void SetupFuzzy() => SetupLegio(OraculumModel.FuzzySugeno);

    [GlobalSetup(Target = nameof(Legio_OrderedFuzzyNumbers))]
    public void SetupOfn() => SetupLegio(OraculumModel.OrderedFuzzyNumbers);

    [GlobalSetup(Target = nameof(Legio_ContextualBandit))]
    public void SetupBandit() => SetupLegio(OraculumModel.ContextualBandit);

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

    private void SetupLegio(OraculumModel model)
    {
        _sim = new DynamicLoadWaveSimulation(49_152);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 512
        });

        _sim.RegisterAll(_legatus);
        for (int i = 0; i < 32; i++) { _sim.AdvanceFrameTick(); _legatus.ExecuteFrame(); }
        Thread.Sleep(110);
        _sim.ResetWave();
    }

    [GlobalCleanup]
    public void Cleanup() => _legatus?.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial() { _sim.AdvanceFrameTick(); _sim.RunSerial(); }

    [Benchmark]
    public void Parallel_For() { _sim.AdvanceFrameTick(); _sim.RunParallelFor(); }

    [Benchmark]
    public void Legio_StaticHeuristic() { _sim.AdvanceFrameTick(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_FuzzySugeno() { _sim.AdvanceFrameTick(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_OrderedFuzzyNumbers() { _sim.AdvanceFrameTick(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_ContextualBandit() { _sim.AdvanceFrameTick(); _legatus!.ExecuteFrame(); }

    [Benchmark]
    public void Legio_OnlineLinUcb() { _sim.AdvanceFrameTick(); _legatus!.ExecuteFrame(); }
}
