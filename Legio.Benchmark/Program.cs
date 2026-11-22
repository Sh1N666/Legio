using System.Diagnostics;
using BenchmarkDotNet.Running;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

namespace Legio.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--quick", StringComparison.OrdinalIgnoreCase))
        {
            RunSuite(longStabilityMode: false);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--stability", StringComparison.OrdinalIgnoreCase))
        {
            RunSuite(longStabilityMode: true);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    /// <summary>
    /// Standalone frame-by-frame game-loop profiler (--quick or --stability).
    /// Measures individual frame timings to compute true Mean FPS, 1% Low (P99), 0.1% Low (P99.9),
    /// Max spike, StdDev, GC collections, allocations, Optio steal count, and ML strategy convergence.
    /// </summary>
    private static void RunSuite(bool longStabilityMode)
    {
        string modeLabel = longStabilityMode ? "LONG CONTINUOUS FPS STABILITY PROFILER" : "QUICK VERIFICATION";
        Console.WriteLine($"=== LEGIO {modeLabel} ===");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores | Stopwatch: {Stopwatch.Frequency / 1_000_000.0:F1} MHz");
        Console.WriteLine();

        int fastFrames   = longStabilityMode ? 1280 : 80;
        int medFrames    = longStabilityMode ? 640  : 50;
        int heavyFrames  = longStabilityMode ? 400  : 32;
        int serialFast   = longStabilityMode ? 64   : 12;
        int serialHeavy  = longStabilityMode ? 24   : 6;

        RunScenario(
            "1. ECS 3D Particle Physics (65,536 entities, 4 cohorts, 3 DAG phases, 24x cluster skew)",
            fastFrames, serialFast, maxCohorts: 8, initialBatch: 512, stride: 2,
            () => { var s = new EcsParticleSimulation(65_536); return () => s.RunSerial(); },
            () => { var s = new EcsParticleSimulation(65_536); return () => s.RunParallelFor(); },
            leg => { var s = new EcsParticleSimulation(65_536); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunScenario(
            "2. 3D SDF Raymarching + Soft Shadows (320x180 = 57,600 rays, compute-bound skew)",
            heavyFrames, serialHeavy, maxCohorts: 4, initialBatch: 256, stride: 1,
            () => { var s = new RaymarchSdfSimulation(0, 320, 180); return () => s.RunSerial(); },
            () => { var s = new RaymarchSdfSimulation(0, 320, 180); return () => s.RunParallelFor(); },
            leg => { var s = new RaymarchSdfSimulation(0, 320, 180); s.Register(leg); return leg.ExecuteFrame; });

        RunScenario(
            "3. AAA Game-Engine 12-Cohort Pipeline (32k entities + 128/256 micro-cohorts, 6 DAG phases, 10 resources)",
            fastFrames, serialFast, maxCohorts: 16, initialBatch: 512, stride: 2,
            () => { var s = new GameEngineDagSimulation(32_768); return () => s.RunSerial(); },
            () => { var s = new GameEngineDagSimulation(32_768); return () => s.RunParallelFor(); },
            leg => { var s = new GameEngineDagSimulation(32_768); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunScenario(
            "4. Boids Spatial Flocking (32,768 agents, 5 cohorts, 4 DAG phases, lock-free atomic grid contention)",
            medFrames, serialFast, maxCohorts: 8, initialBatch: 512, stride: 2,
            () => { var s = new BoidsSpatialFlockingSimulation(32_768); return () => s.RunSerial(); },
            () => { var s = new BoidsSpatialFlockingSimulation(32_768); return () => s.RunParallelFor(); },
            leg => { var s = new BoidsSpatialFlockingSimulation(32_768); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunScenario(
            "5. Multi-Agent A* Maze Pathfinding (2,048 agents, 64x64 maze, 2 DAG phases, ~100x heavy-tailed variance)",
            heavyFrames, serialHeavy, maxCohorts: 4, initialBatch: 32, stride: 1,
            () => { var s = new AStarNavMeshCrowdSimulation(2_048); return () => s.RunSerial(); },
            () => { var s = new AStarNavMeshCrowdSimulation(2_048); return () => s.RunParallelFor(); },
            leg => { var s = new AStarNavMeshCrowdSimulation(2_048); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunDynamicWaveScenario(longStabilityMode ? 1280 : 256, longStabilityMode ? 128 : 32);
    }

    private static void RunScenario(
        string title,
        int measureFrames,
        int serialFrames,
        int maxCohorts,
        int initialBatch,
        int stride,
        Func<Action> createSerial,
        Func<Action> createParallel,
        Func<Legatus, Action> createLegio)
    {
        Console.WriteLine($"=== {title} ({measureFrames} frames) ===");
        PrintHeader();

        var serialStats = MeasurePerFrame(createSerial(), warmup: 4, frames: serialFrames);
        PrintRow("Serial", serialStats, serialStats.MeanMs, steals: -1, info: "-");

        var parStats = MeasurePerFrame(createParallel(), warmup: 10, frames: measureFrames);
        PrintRow("Parallel.For", parStats, serialStats.MeanMs, steals: -1, info: "-");

        foreach (OraculumModel model in Enum.GetValues<OraculumModel>())
        {
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = maxCohorts,
                OracleModel = model,
                OracleIntervalMs = 80,
                TelemetrySampleStride = stride,
                InitialBatchSize = initialBatch
            });

            Action step = createLegio(legatus);

            // Warmup & let Sanctum execute initial adaptation windows
            for (int w = 0; w < 20; w++) step();
            Thread.Sleep(110);
            for (int w = 0; w < 20; w++) step();
            Thread.Sleep(110);

            long stealsBefore = legatus.GetTotalSteals();
            var stats = MeasurePerFrame(step, warmup: 5, frames: measureFrames);
            long steals = legatus.GetTotalSteals() - stealsBefore;

            var s0 = legatus.Scriba.Legere(0);
            string info = maxCohorts >= 16
                ? $"B0={s0.BatchSize}/T2={legatus.Scriba.Legere(2).ThreadCount}/T9={legatus.Scriba.Legere(9).ThreadCount}"
                : $"B0={s0.BatchSize}/T0={s0.ThreadCount}";

            PrintRow($"Legio.{model}", stats, serialStats.MeanMs, steals, info);
        }

        Console.WriteLine();
    }

    private static void RunDynamicWaveScenario(int measureFrames, int serialFrames)
    {
        Console.WriteLine($"=== 6. Non-Stationary Dynamic Load Wave (49,152 entities, 4 shifting regimes / 128-frame cycle, {measureFrames} frames) ===");
        PrintHeader();

        var sSim = new DynamicLoadWaveSimulation(49_152);
        var serialStats = MeasurePerFrame(() => { sSim.AdvanceFrameTick(); sSim.RunSerial(); }, warmup: 4, frames: serialFrames);
        PrintRow("Serial", serialStats, serialStats.MeanMs, -1, "-");

        var pSim = new DynamicLoadWaveSimulation(49_152);
        var parStats = MeasurePerFrame(() => { pSim.AdvanceFrameTick(); pSim.RunParallelFor(); }, warmup: 16, frames: measureFrames);
        PrintRow("Parallel.For", parStats, serialStats.MeanMs, -1, "-");

        foreach (OraculumModel model in Enum.GetValues<OraculumModel>())
        {
            var sim = new DynamicLoadWaveSimulation(49_152);
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = 8,
                OracleModel = model,
                OracleIntervalMs = 60,
                TelemetrySampleStride = 1,
                InitialBatchSize = 512
            });

            sim.RegisterAll(legatus);

            for (int w = 0; w < 32; w++) { sim.AdvanceFrameTick(); legatus.ExecuteFrame(); }
            Thread.Sleep(100);
            sim.ResetWave();

            long stealsBefore = legatus.GetTotalSteals();
            var stats = MeasurePerFrame(() => { sim.AdvanceFrameTick(); legatus.ExecuteFrame(); }, warmup: 0, frames: measureFrames);
            long steals = legatus.GetTotalSteals() - stealsBefore;

            var s1 = legatus.Scriba.Legere(1);
            PrintRow($"Legio.{model}", stats, serialStats.MeanMs, steals, $"B1={s1.BatchSize}/T1={s1.ThreadCount}");
        }

        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.WriteLine(
            $"{"Runner",-27} | {"Mean ms",8} | {"P95 ms",8} | {"P99 ms",8} | {"Max ms",8} | {"StdDev",7} | {"Speedup",7} | {"1%Low FPS",9} | {"Alloc/f",8} | {"GC0",3} | {"Steals",9} | {"Final Strategy"}");
        Console.WriteLine(new string('-', 146));
    }

    private static void PrintRow(string name, FrameStats st, double serialMeanMs, long steals, string info)
    {
        double speedup = serialMeanMs / Math.Max(1e-6, st.MeanMs);
        double onePercentLowFps = 1000.0 / Math.Max(1e-6, st.P99Ms);
        string stealStr = steals >= 0 ? steals.ToString("N0") : "-";
        Console.WriteLine(
            $"{name,-27} | {st.MeanMs,8:F3} | {st.P95Ms,8:F3} | {st.P99Ms,8:F3} | {st.MaxMs,8:F3} | {st.StdDevMs,7:F3} | {speedup,6:F2}x | {onePercentLowFps,9:F1} | {st.AllocPerFrame,6} B | {st.Gen0Collections,3} | {stealStr,9} | {info}");
    }

    private readonly record struct FrameStats(
        double MeanMs,
        double StdDevMs,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        long AllocPerFrame,
        int Gen0Collections);

    private static FrameStats MeasurePerFrame(Action action, int warmup, int frames)
    {
        for (int i = 0; i < warmup; i++)
            action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        double[] samples = new double[frames];
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        int gen0Before = GC.CollectionCount(0);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < frames; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            long t1 = Stopwatch.GetTimestamp();
            samples[i] = (t1 - t0) * tickToMs;
        }

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        int gen0After = GC.CollectionCount(0);

        double sum = 0;
        for (int i = 0; i < frames; i++) sum += samples[i];
        double mean = sum / frames;

        double varSum = 0;
        for (int i = 0; i < frames; i++)
        {
            double d = samples[i] - mean;
            varSum += d * d;
        }
        double stdDev = Math.Sqrt(varSum / frames);

        Array.Sort(samples);
        double p95 = samples[Math.Min(frames - 1, (int)(frames * 0.95))];
        double p99 = samples[Math.Min(frames - 1, (int)(frames * 0.99))];
        double max = samples[frames - 1];

        return new FrameStats(
            MeanMs: mean,
            StdDevMs: stdDev,
            P95Ms: p95,
            P99Ms: p99,
            MaxMs: max,
            AllocPerFrame: (allocAfter - allocBefore) / frames,
            Gen0Collections: gen0After - gen0Before);
    }
}