using BenchmarkDotNet.Attributes;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

namespace Legio.Benchmark.Core;

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

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
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

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
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

    [GlobalSetup(Target = nameof(Legio_OnlineLinUcb))]
    public void SetupLinUcb() => SetupLegio(OraculumModel.OnlineLinUcb);

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

    [Benchmark]
    public void Legio_OnlineLinUcb() { _sim.AdvanceWind(); _legatus!.ExecuteFrame(); }
}

/// <summary>
/// 10. Mega-Brutal 3D SPH Dam-Break Fluid Simulation (12,288 particles, 6 cohorts, 5 DAG phases,
///     27-cell linked-list L3 cache gather thrashing + extreme corner density spatial clustering).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class FluidSphBenchmark
{
    private FluidSphSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new FluidSphSimulation(12_288);

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
        _sim = new FluidSphSimulation(12_288);
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}

/// <summary>
/// 11. Mega-Brutal Double-Precision Deep-Zoom Mandelbrot Fractal (384x216 = 82,944 pixels, up to 2,048 iter/px,
///     ~500x asymmetric spatial skew on the cardioid/Seahorse boundary + 2-phase Sobel/Exposure DAG).
/// </summary>
[Config(typeof(LegioExtendedConfig))]
public class MandelbrotDeepZoomBenchmark
{
    private MandelbrotDeepZoomSimulation _sim = null!;
    private Legatus? _legatus;

    [GlobalSetup(Targets = [nameof(Serial), nameof(Parallel_For)])]
    public void SetupBaseline() => _sim = new MandelbrotDeepZoomSimulation();

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
        _sim = new MandelbrotDeepZoomSimulation();
        _legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            OracleModel = model,
            OracleIntervalMs = 80,
            TelemetrySampleStride = 1,
            InitialBatchSize = 256
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

    [Benchmark]
    public void Legio_OnlineLinUcb() => _legatus!.ExecuteFrame();
}
