using System.Diagnostics;
using System.Globalization;
using System.Text;
using Legio.Benchmark.Simulations;
using Legio.Core;
using Legio.Protocol;

namespace Legio.Benchmark.Core;

/// <summary>
/// Standalone frame-by-frame game-loop profiler (--quick, --stability, --heavy, --new, --timeline).
/// Measures individual frame timings to compute true Mean FPS, 1% Low (P99), Max spike, StdDev,
/// time-series stability buckets, spike counts (&gt; 1.5x median), GC collections, allocations,
/// Optio steal counts, Option B strategy decisions, and hardware PMU telemetry.
/// </summary>
internal static class StabilityProfiler
{
    public static void RunSuite(string mode)
    {
        if (mode == "timeline")
        {
            RunTimelineSuite();
            return;
        }

        bool longStabilityMode = mode != "quick";
        string modeLabel = mode switch
        {
            "heavy" => "EXTREME CPU/MEMORY TORTURE STABILITY PROFILER (SCENARIOS 7-11)",
            "new"   => "OPTION A/B + ONLINELINUCB VALIDATION PROFILER (SCENARIOS 6, 10, 11)",
            "quick" => "QUICK VERIFICATION (ALL 11 SCENARIOS)",
            _       => "LONG CONTINUOUS FPS STABILITY PROFILER (ALL 11 SCENARIOS)"
        };

        Console.WriteLine($"=== LEGIO {modeLabel} ===");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores | Stopwatch: {Stopwatch.Frequency / 1_000_000.0:F1} MHz");
        Console.WriteLine();

        int fastFrames   = longStabilityMode ? 1280 : 60;
        int medFrames    = longStabilityMode ? 640  : 40;
        int heavyFrames  = longStabilityMode ? 400  : 24;
        int brutalFrames = longStabilityMode ? 240  : 16;
        int serialFast   = longStabilityMode ? 64   : 10;
        int serialHeavy  = longStabilityMode ? 24   : 5;
        int serialBrutal = longStabilityMode ? 10   : 3;

        if (mode == "quick" || mode == "stability")
        {
            RunScenario(
                "1. ECS 3D Particle Physics (65,536 entities, 4 cohorts, 3 DAG phases, 24x cluster skew)",
                fastFrames, serialFast, maxCohorts: 8, initialBatch: 512, stride: 2, inspectSlot: 0,
                () => { var s = new EcsParticleSimulation(65_536); return () => s.RunSerial(); },
                () => { var s = new EcsParticleSimulation(65_536); return () => s.RunParallelFor(); },
                leg => { var s = new EcsParticleSimulation(65_536); s.RegisterAll(leg); return leg.ExecuteFrame; });

            RunScenario(
                "2. 3D SDF Raymarching + Soft Shadows (320x180 = 57,600 rays, compute-bound skew)",
                heavyFrames, serialHeavy, maxCohorts: 4, initialBatch: 256, stride: 1, inspectSlot: 0,
                () => { var s = new RaymarchSdfSimulation(0, 320, 180); return () => s.RunSerial(); },
                () => { var s = new RaymarchSdfSimulation(0, 320, 180); return () => s.RunParallelFor(); },
                leg => { var s = new RaymarchSdfSimulation(0, 320, 180); s.Register(leg); return leg.ExecuteFrame; });

            RunScenario(
                "3. AAA Game-Engine 12-Cohort Pipeline (32k entities + 128/256 micro-cohorts, 6 DAG phases, 10 resources)",
                fastFrames, serialFast, maxCohorts: 16, initialBatch: 512, stride: 2, inspectSlot: 0,
                () => { var s = new GameEngineDagSimulation(32_768); return () => s.RunSerial(); },
                () => { var s = new GameEngineDagSimulation(32_768); return () => s.RunParallelFor(); },
                leg => { var s = new GameEngineDagSimulation(32_768); s.RegisterAll(leg); return leg.ExecuteFrame; });

            RunScenario(
                "4. Boids Spatial Flocking (32,768 agents, 5 cohorts, 4 DAG phases, lock-free atomic grid contention)",
                medFrames, serialFast, maxCohorts: 8, initialBatch: 512, stride: 2, inspectSlot: 2,
                () => { var s = new BoidsSpatialFlockingSimulation(32_768); return () => s.RunSerial(); },
                () => { var s = new BoidsSpatialFlockingSimulation(32_768); return () => s.RunParallelFor(); },
                leg => { var s = new BoidsSpatialFlockingSimulation(32_768); s.RegisterAll(leg); return leg.ExecuteFrame; });

            RunScenario(
                "5. Multi-Agent A* Maze Pathfinding (2,048 agents, 64x64 maze, 2 DAG phases, ~100x heavy-tailed variance)",
                heavyFrames, serialHeavy, maxCohorts: 4, initialBatch: 32, stride: 1, inspectSlot: 0,
                () => { var s = new AStarNavMeshCrowdSimulation(2_048); return () => s.RunSerial(); },
                () => { var s = new AStarNavMeshCrowdSimulation(2_048); return () => s.RunParallelFor(); },
                leg => { var s = new AStarNavMeshCrowdSimulation(2_048); s.RegisterAll(leg); return leg.ExecuteFrame; });
        }

        if (mode == "quick" || mode == "stability" || mode == "new")
        {
            RunDynamicWaveScenario(longStabilityMode ? 1280 : 192, longStabilityMode ? 128 : 24);
        }

        if (mode == "quick" || mode == "stability" || mode == "heavy")
        {
            RunScenario(
                "7. Monte Carlo Path Tracing GI (240x135 x 6 SPP x 6 bounces, Fresnel glass + Russian Roulette + 2-phase Denoise)",
                brutalFrames, serialBrutal, maxCohorts: 4, initialBatch: 128, stride: 1, inspectSlot: 0,
                () => { var s = new MonteCarloPathTracingSimulation(); return () => s.RunSerial(); },
                () => { var s = new MonteCarloPathTracingSimulation(); return () => s.RunParallelFor(); },
                leg => { var s = new MonteCarloPathTracingSimulation(); s.RegisterAll(leg); return leg.ExecuteFrame; });

            RunScenario(
                "8. Astrophysical O(N^2) N-Body Gravity + Collisions (3,072 bodies = 9.44M pairs/frame, 4 cohorts, 3 DAG phases)",
                brutalFrames, serialBrutal, maxCohorts: 8, initialBatch: 64, stride: 1, inspectSlot: 0,
                () => { var s = new NBodyGravitySimulation(3_072); return () => s.RunSerial(); },
                () => { var s = new NBodyGravitySimulation(3_072); return () => s.RunParallelFor(); },
                leg => { var s = new NBodyGravitySimulation(3_072); s.RegisterAll(leg); return leg.ExecuteFrame; });

            RunScenario(
                "9. Cloth PBD 256x256 Red-Black Solver (65,536 vertices, 260k constraints, 10 SEQUENTIAL DAG PHASES/frame!)",
                medFrames, serialFast, maxCohorts: 16, initialBatch: 512, stride: 2, inspectSlot: 2,
                () => { var s = new ClothVerletPbdSimulation(); return () => s.RunSerial(); },
                () => { var s = new ClothVerletPbdSimulation(); return () => s.RunParallelFor(); },
                leg => { var s = new ClothVerletPbdSimulation(); s.RegisterAll(leg); return () => { s.AdvanceWind(); leg.ExecuteFrame(); }; });
        }

        RunScenario(
            "10. 3D SPH Dam-Break Fluid (12,288 particles, 6 cohorts, 5 DAG phases, 27-cell L3 cache gather + density skew)",
            brutalFrames, serialBrutal, maxCohorts: 8, initialBatch: 128, stride: 1, inspectSlot: 3,
            () => { var s = new FluidSphSimulation(12_288); return () => s.RunSerial(); },
            () => { var s = new FluidSphSimulation(12_288); return () => s.RunParallelFor(); },
            leg => { var s = new FluidSphSimulation(12_288); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunScenario(
            "11. Deep-Zoom Mandelbrot 2048-iter Fractal (384x216 = 82,944 px, ~500x asymmetric spatial skew + 2-phase DAG)",
            brutalFrames, serialBrutal, maxCohorts: 4, initialBatch: 256, stride: 1, inspectSlot: 0,
            () => { var s = new MandelbrotDeepZoomSimulation(); return () => s.RunSerial(); },
            () => { var s = new MandelbrotDeepZoomSimulation(); return () => s.RunParallelFor(); },
            leg => { var s = new MandelbrotDeepZoomSimulation(); s.RegisterAll(leg); return leg.ExecuteFrame; });
    }

    /// <summary>
    /// Time-series stability profiler (--timeline): splits each run into 12 chronological windows
    /// to expose cold-start exploration transients, convergence speed, regime-shift tracking, and frame spikes (>1.5x median).
    /// </summary>
    private static void RunTimelineSuite()
    {
        Console.WriteLine("=== LEGIO TIME-SERIES STABILITY & CONVERGENCE PROFILER (12 CHRONOLOGICAL WINDOWS) ===");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores | Stopwatch: {Stopwatch.Frequency / 1_000_000.0:F1} MHz");
        Console.WriteLine();

        RunTimelineScenario(
            "A. A* Maze Pathfinding (360 frames -> 12 windows x 30 frames)",
            frames: 360, buckets: 12, maxCohorts: 4, initialBatch: 32, stride: 1,
            () => { var s = new AStarNavMeshCrowdSimulation(2_048); return () => s.RunParallelFor(); },
            leg => { var s = new AStarNavMeshCrowdSimulation(2_048); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunTimelineScenario(
            "B. Non-Stationary Dynamic Load Wave (768 frames = 6 full cycles -> 12 windows x 64 frames)",
            frames: 768, buckets: 12, maxCohorts: 8, initialBatch: 512, stride: 1,
            () => { var s = new DynamicLoadWaveSimulation(49_152); return () => { s.AdvanceFrameTick(); s.RunParallelFor(); }; },
            leg => { var s = new DynamicLoadWaveSimulation(49_152); s.RegisterAll(leg); return () => { s.AdvanceFrameTick(); leg.ExecuteFrame(); }; });

        RunTimelineScenario(
            "C. Astrophysical N-Body O(N^2) (240 frames -> 12 windows x 20 frames)",
            frames: 240, buckets: 12, maxCohorts: 8, initialBatch: 64, stride: 1,
            () => { var s = new NBodyGravitySimulation(3_072); return () => s.RunParallelFor(); },
            leg => { var s = new NBodyGravitySimulation(3_072); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunTimelineScenario(
            "D. Cloth PBD 10-Phase DAG (600 frames -> 12 windows x 50 frames)",
            frames: 600, buckets: 12, maxCohorts: 16, initialBatch: 512, stride: 2,
            () => { var s = new ClothVerletPbdSimulation(); return () => s.RunParallelFor(); },
            leg => { var s = new ClothVerletPbdSimulation(); s.RegisterAll(leg); return () => { s.AdvanceWind(); leg.ExecuteFrame(); }; });

        RunTimelineScenario(
            "E. 3D SPH Dam-Break Fluid (240 frames -> 12 windows x 20 frames)",
            frames: 240, buckets: 12, maxCohorts: 8, initialBatch: 128, stride: 1,
            () => { var s = new FluidSphSimulation(12_288); return () => s.RunParallelFor(); },
            leg => { var s = new FluidSphSimulation(12_288); s.RegisterAll(leg); return leg.ExecuteFrame; });

        RunTimelineScenario(
            "F. Deep-Zoom Mandelbrot 2048-iter (240 frames -> 12 windows x 20 frames)",
            frames: 240, buckets: 12, maxCohorts: 4, initialBatch: 256, stride: 1,
            () => { var s = new MandelbrotDeepZoomSimulation(); return () => s.RunParallelFor(); },
            leg => { var s = new MandelbrotDeepZoomSimulation(); s.RegisterAll(leg); return leg.ExecuteFrame; });
    }

    private static void RunTimelineScenario(
        string title,
        int frames,
        int buckets,
        int maxCohorts,
        int initialBatch,
        int stride,
        Func<Action> createParallel,
        Func<Legatus, Action> createLegio)
    {
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine($"{"Runner",-25} | {"Med ms",7} | {"Spk>1.5x",8} | {"Spk>2.0x",8} | {"CV%",6} | 12 Window Means (ms): W1 .. W12");
        Console.WriteLine(new string('-', 152));

        PrintTimelineRow("Parallel.For", CaptureTimeline(createParallel(), warmup: 10, frames, buckets));

        foreach (OraculumModel model in Enum.GetValues<OraculumModel>())
        {
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = maxCohorts,
                OracleModel = model,
                OracleIntervalMs = 80,
                CpuMonitorIntervalMs = 150,
                TelemetrySampleStride = stride,
                InitialBatchSize = initialBatch
            });

            Action step = createLegio(legatus);
            // Minimal 10-frame warmup so cold-start adaptation is directly visible in W1..W3!
            for (int w = 0; w < 10; w++) step();
            Thread.Sleep(90);

            PrintTimelineRow($"Legio.{model}", CaptureTimeline(step, warmup: 0, frames, buckets));
        }

        Console.WriteLine();
    }

    private static TimelineStats CaptureTimeline(Action action, int warmup, int frames, int buckets)
    {
        for (int i = 0; i < warmup; i++) action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        double[] raw = new double[frames];
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        for (int i = 0; i < frames; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            long t1 = Stopwatch.GetTimestamp();
            raw[i] = (t1 - t0) * tickToMs;
        }

        double[] bucketMeans = new double[buckets];
        int perBucket = Math.Max(1, frames / buckets);
        for (int b = 0; b < buckets; b++)
        {
            int start = b * perBucket;
            int end = (b == buckets - 1) ? frames : Math.Min(frames, start + perBucket);
            double s = 0;
            for (int i = start; i < end; i++) s += raw[i];
            bucketMeans[b] = s / Math.Max(1, end - start);
        }

        double mean = 0;
        for (int i = 0; i < frames; i++) mean += raw[i];
        mean /= frames;

        double varSum = 0;
        for (int i = 0; i < frames; i++) { double d = raw[i] - mean; varSum += d * d; }
        double stdDev = Math.Sqrt(varSum / frames);

        double[] sorted = (double[])raw.Clone();
        Array.Sort(sorted);
        double median = sorted[frames / 2];

        int spikes15 = 0;
        int spikes20 = 0;
        for (int i = 0; i < frames; i++)
        {
            if (raw[i] > median * 1.5) spikes15++;
            if (raw[i] > median * 2.0) spikes20++;
        }

        return new TimelineStats(median, mean, stdDev, spikes15, spikes20, bucketMeans);
    }

    private static void PrintTimelineRow(string name, TimelineStats t)
    {
        double cv = t.MeanMs > 1e-6 ? (t.StdDevMs / t.MeanMs) * 100.0 : 0.0;
        var sb = new StringBuilder();
        for (int i = 0; i < t.BucketMeans.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(t.BucketMeans[i].ToString("F2", CultureInfo.InvariantCulture));
        }

        Console.WriteLine(
            $"{name,-25} | {t.MedianMs,7:F3} | {t.Spikes15x,8} | {t.Spikes20x,8} | {cv,5:F1}% | [{sb}]");
    }

    private readonly record struct TimelineStats(
        double MedianMs,
        double MeanMs,
        double StdDevMs,
        int Spikes15x,
        int Spikes20x,
        double[] BucketMeans);

    private static string FormatStrategyAndPmu(Legatus legatus, int slot)
    {
        Strategia s = legatus.Scriba.Legere(slot);
        SystemTelemetry t = legatus.CurrentSystemTelemetry;
        char part = s.PartitionMode == PartitionMode.Interleaved ? 'I' : 'C';
        char steal = s.StealMode == StealMode.StealHalf ? 'H' : '1';
        string pmu = t.HasPmuCounters != 0
            ? $" | IPC={t.InstructionsPerCycle:F2} L3M={t.CacheMissRate * 100:F1}%"
            : "";
        return $"B={s.BatchSize}/T={s.ThreadCount}/{part}/{steal}/A{s.AffinityHint}/Sp{s.SpinIterationsBeforePark}{pmu}";
    }

    private static void RunScenario(
        string title,
        int measureFrames,
        int serialFrames,
        int maxCohorts,
        int initialBatch,
        int stride,
        int inspectSlot,
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
                CpuMonitorIntervalMs = 150,
                TelemetrySampleStride = stride,
                InitialBatchSize = initialBatch
            });

            Action step = createLegio(legatus);

            for (int w = 0; w < 20; w++) step();
            Thread.Sleep(110);
            for (int w = 0; w < 20; w++) step();
            Thread.Sleep(110);

            long stealsBefore = legatus.GetTotalSteals();
            var stats = MeasurePerFrame(step, warmup: 5, frames: measureFrames);
            long steals = legatus.GetTotalSteals() - stealsBefore;

            PrintRow($"Legio.{model}", stats, serialStats.MeanMs, steals, FormatStrategyAndPmu(legatus, inspectSlot));
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
                CpuMonitorIntervalMs = 150,
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

            PrintRow($"Legio.{model}", stats, serialStats.MeanMs, steals, FormatStrategyAndPmu(legatus, 1));
        }

        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.WriteLine(
            $"{"Runner",-27} | {"Mean ms",8} | {"P95 ms",8} | {"P99 ms",8} | {"Max ms",8} | {"StdDev",7} | {"Speedup",7} | {"1%Low FPS",9} | {"Alloc/f",8} | {"GC0",3} | {"Steals",9} | {"Final Strategy (Part/Steal/Aff/Spin | PMU)"}");
        Console.WriteLine(new string('-', 174));
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
