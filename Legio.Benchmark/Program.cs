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
            RunQuickComparison();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    /// <summary>
    /// Fast standalone comparison runner (--quick) that prints mean frame time, speedup,
    /// managed allocations, Optio steal count, and Oracle ritual count for every model.
    /// </summary>
    private static void RunQuickComparison()
    {
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores | Stopwatch: {Stopwatch.Frequency / 1_000_000.0:F1} MHz");
        Console.WriteLine();

        RunEcsQuick();
        Console.WriteLine();
        RunRaymarchQuick();
    }

    private static void RunEcsQuick()
    {
        const int particles = 65_536;
        const int measureFrames = 60;
        var sim = new EcsParticleSimulation(particles);

        Console.WriteLine($"=== 4-Cohort ECS Particle Simulation ({particles:N0} entities, 3 DAG phases, skewed clusters) ===");
        Console.WriteLine($"{"Runner",-28} | {"Mean (ms)",10} | {"Speedup",8} | {"Alloc/frame",12} | {"Steals",8} | {"Batch0/1",11}");
        Console.WriteLine(new string('-', 92));

        double serialMs = Measure("Serial", () => sim.RunSerial(), 5, 15, out long serialAlloc);
        Console.WriteLine($"{"Serial",-28} | {serialMs,10:F3} | {"1.00x",8} | {serialAlloc,10} B | {"-",8} | {"-",11}");

        double parMs = Measure("Parallel.For", () => sim.RunParallelFor(), 5, measureFrames, out long parAlloc);
        Console.WriteLine($"{"Parallel.For",-28} | {parMs,10:F3} | {serialMs / parMs,7:F2}x | {parAlloc,10} B | {"-",8} | {"-",11}");

        foreach (OraculumModel model in Enum.GetValues<OraculumModel>())
        {
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = 8,
                OracleModel = model,
                OracleIntervalMs = 80,
                TelemetrySampleStride = 1,
                InitialBatchSize = 512
            });

            sim.RegisterAll(legatus);

            // Warmup + let Sanctum adapt across several periodic ML windows
            for (int w = 0; w < 25; w++) legatus.ExecuteFrame();
            Thread.Sleep(120);
            for (int w = 0; w < 25; w++) legatus.ExecuteFrame();
            Thread.Sleep(120);

            long stealsBefore = legatus.GetTotalSteals();
            double ms = Measure($"Legio ({model})", () => legatus.ExecuteFrame(), 5, measureFrames, out long alloc);
            long steals = legatus.GetTotalSteals() - stealsBefore;

            int b0 = legatus.Scriba.Legere(0).BatchSize;
            int b1 = legatus.Scriba.Legere(1).BatchSize;

            Console.WriteLine($"{"Legio." + model,-28} | {ms,10:F3} | {serialMs / ms,7:F2}x | {alloc,10} B | {steals,8} | {b0,5}/{b1,-5}");
        }
    }

    private static void RunRaymarchQuick()
    {
        const int measureFrames = 40;
        var sim = new RaymarchSdfSimulation(0, 320, 180);

        Console.WriteLine($"=== Skewed 3D SDF Raymarching ({sim.Width}x{sim.Height} = {sim.PixelCount:N0} rays, orbiting camera) ===");
        Console.WriteLine($"{"Runner",-28} | {"Mean (ms)",10} | {"Speedup",8} | {"Alloc/frame",12} | {"Steals",8} | {"Batch/Thr",11}");
        Console.WriteLine(new string('-', 92));

        double serialMs = Measure("Serial", () => { sim.AdvanceCamera(); sim.RunSerial(); }, 2, 6, out long serialAlloc);
        Console.WriteLine($"{"Serial",-28} | {serialMs,10:F3} | {"1.00x",8} | {serialAlloc,10} B | {"-",8} | {"-",11}");

        double parMs = Measure("Parallel.For", () => { sim.AdvanceCamera(); sim.RunParallelFor(); }, 5, measureFrames, out long parAlloc);
        Console.WriteLine($"{"Parallel.For",-28} | {parMs,10:F3} | {serialMs / parMs,7:F2}x | {parAlloc,10} B | {"-",8} | {"-",11}");

        foreach (OraculumModel model in Enum.GetValues<OraculumModel>())
        {
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = 4,
                OracleModel = model,
                OracleIntervalMs = 80,
                TelemetrySampleStride = 1,
                InitialBatchSize = 256
            });

            sim.Register(legatus);

            for (int w = 0; w < 20; w++) { sim.AdvanceCamera(); legatus.ExecuteFrame(); }
            Thread.Sleep(120);
            for (int w = 0; w < 20; w++) { sim.AdvanceCamera(); legatus.ExecuteFrame(); }
            Thread.Sleep(120);

            long stealsBefore = legatus.GetTotalSteals();
            double ms = Measure($"Legio ({model})", () => { sim.AdvanceCamera(); legatus.ExecuteFrame(); }, 5, measureFrames, out long alloc);
            long steals = legatus.GetTotalSteals() - stealsBefore;

            var s = legatus.Scriba.Legere(0);
            Console.WriteLine($"{"Legio." + model,-28} | {ms,10:F3} | {serialMs / ms,7:F2}x | {alloc,10} B | {steals,8} | {s.BatchSize,5}/{s.ThreadCount,-3}");
        }
    }

    private static double Measure(string _, Action action, int warmup, int iterations, out long allocPerFrame)
    {
        for (int i = 0; i < warmup; i++) action();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();

        for (int i = 0; i < iterations; i++) action();

        long elapsed = Stopwatch.GetTimestamp() - t0;
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        allocPerFrame = (allocAfter - allocBefore) / iterations;
        return (elapsed * 1000.0 / Stopwatch.Frequency) / iterations;
    }
}