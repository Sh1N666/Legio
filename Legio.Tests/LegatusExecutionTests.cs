using Legio.Core;
using Legio.Core.Executio;
using Legio.Protocol;
using Xunit;

namespace Legio.Tests;

public class LegatusExecutionTests
{
    private sealed class IncrementSystem(int id, int[] buffer) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => buffer.Length;

        protected override void ExecuteBatchLogic(int start, int length)
        {
            for (int i = start; i < start + length; i++)
            {
                buffer[i]++;
            }
        }
    }

    private sealed class PipelineStageSystem(int id, int[] input, int[] output, Func<int, int> transform) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => output.Length;

        protected override void ExecuteBatchLogic(int start, int length)
        {
            for (int i = start; i < start + length; i++)
            {
                output[i] = transform(input[i]);
            }
        }
    }

    private sealed class SkewedWorkloadSystem(int id, float[] data) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => data.Length;

        protected override void ExecuteBatchLogic(int start, int length)
        {
            int heavyBoundary = data.Length / 8; // First 12.5% is 80x heavier -> forces intense Optio stealing
            for (int i = start; i < start + length; i++)
            {
                float v = data[i];
                int iters = i < heavyBoundary ? 160 : 2;
                for (int j = 0; j < iters; j++)
                {
                    v = MathF.Sin(v) * MathF.Cos(v) + 1.0f;
                }
                data[i] = v;
            }
        }
    }

    [Fact]
    public void ExecuteFrame_CompletesAllItemsBeforeReturning_AcrossManyConsecutiveFrames()
    {
        const int itemCount = 100_000;
        const int frames = 40;
        var buffer = new int[itemCount];

        using var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            ThreadCount = Math.Min(8, Environment.ProcessorCount),
            InitialBatchSize = 256,
            OracleIntervalMs = 50,
            OracleModel = OraculumModel.FuzzySugeno
        });

        var sys = new IncrementSystem(0, buffer);
        var cohors = new Cohors(0, sys);
        cohors.WriteMask.Tollere(0);

        legatus.RegisterCohors(cohors);
        legatus.ConstructConsilium();

        for (int f = 1; f <= frames; f++)
        {
            legatus.ExecuteFrame();

            // Immediately after ExecuteFrame returns, every single element MUST already equal `f`.
            // (If phase barrier were missing, straggling worker threads would cause stale reads here.)
            for (int i = 0; i < itemCount; i++)
            {
                if (buffer[i] != f)
                {
                    Assert.Fail($"Frame {f}: index {i} expected {f}, got {buffer[i]} (barrier failure!).");
                }
            }
        }
    }

    [Fact]
    public void MultiPhasePipeline_EnforcesStrictBarrierBetweenDependentPhases()
    {
        const int n = 65_536;
        const int frames = 30;
        var a = new int[n];
        var b = new int[n];
        var c = new int[n];

        using var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            ThreadCount = Math.Min(8, Environment.ProcessorCount),
            InitialBatchSize = 512,
            OracleModel = OraculumModel.OrderedFuzzyNumbers
        });

        // Phase 0: a[i]++
        var c0 = new Cohors(0, new IncrementSystem(0, a));
        c0.WriteMask.Tollere(0);

        // Phase 1: b[i] = a[i] * 3
        var c1 = new Cohors(1, new PipelineStageSystem(1, a, b, static x => x * 3));
        c1.ReadMask.Tollere(0);
        c1.WriteMask.Tollere(1);

        // Phase 2: c[i] = b[i] + 7
        var c2 = new Cohors(2, new PipelineStageSystem(2, b, c, static x => x + 7));
        c2.ReadMask.Tollere(1);
        c2.WriteMask.Tollere(2);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.ConstructConsilium();

        Assert.Equal(3, legatus.GradusCount);

        for (int f = 1; f <= frames; f++)
        {
            legatus.ExecuteFrame();

            int expectedC = f * 3 + 7;
            for (int i = 0; i < n; i++)
            {
                if (c[i] != expectedC)
                {
                    Assert.Fail($"Frame {f}: c[{i}] expected {expectedC}, got {c[i]}.");
                }
            }
        }
    }

    [Fact]
    public void SkewedWorkload_TriggersRealOptioWorkStealing_AndProducesExactResults()
    {
        const int n = 32_768;
        var data = new float[n];
        var expected = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = (i % 97) * 0.1f;
            data[i] = v;
            expected[i] = v;
        }

        // Compute single-threaded reference
        var referenceSys = new SkewedWorkloadSystem(99, expected);
        ((IPriorPilus)referenceSys).ExecuteBatch(0, n);

        int threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
        using var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 4,
            ThreadCount = threads,
            InitialBatchSize = 128, // Multiple chunks per worker so idle threads can steal from heavy worker 0
            OracleModel = OraculumModel.FuzzySugeno
        });

        var sys = new SkewedWorkloadSystem(0, data);
        var cohors = new Cohors(0, sys);
        cohors.WriteMask.Tollere(0);
        legatus.RegisterCohors(cohors);
        legatus.ConstructConsilium();

        legatus.ExecuteFrame();

        Assert.True(legatus.GetTotalSteals() > 0, "Expected idle Centurions to steal heavy chunks via Optio.");

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expected[i], data[i], precision: 5);
        }
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    [InlineData(OraculumModel.ContextualBandit)]
    public void ExecuteFrame_ZeroAllocationsOnCallingThread_AndHaruspexLoadsCleanly(OraculumModel model)
    {
        const int n = 32_768;
        var buffer = new int[n];
        string? diagError = null;

        using var legatus = new Legatus(new LegatusConfig
        {
            MaxCohorts = 8,
            ThreadCount = Math.Min(4, Environment.ProcessorCount),
            InitialBatchSize = 512,
            OracleModel = model,
            OracleIntervalMs = 25,
            DiagnosticSink = msg => diagError = msg
        });

        Assert.True(legatus.IsHaruspexLoaded, $"Haruspex F# Oracle failed to load: {diagError}");
        Assert.Null(diagError);

        var cohors = new Cohors(0, new IncrementSystem(0, buffer));
        cohors.WriteMask.Tollere(0);
        legatus.RegisterCohors(cohors);
        legatus.ConstructConsilium();

        // Warmup frames (JIT + let Sanctum complete at least one periodic ML ritual)
        for (int i = 0; i < 10; i++)
        {
            legatus.ExecuteFrame();
        }
        Thread.Sleep(70);
        for (int i = 0; i < 5; i++)
        {
            legatus.ExecuteFrame();
        }

        Assert.True(legatus.OracleRitualCount > 0, "Expected periodic Sanctum thread to invoke Haruspex ML Oracle.");

        // Measure strict zero allocation on ExecuteFrame hot path
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 25; i++)
        {
            legatus.ExecuteFrame();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Dispose_CleanlyShutsDownRepeatedInstances_WithoutHanging()
    {
        var buffer = new int[4096];

        for (int round = 0; round < 5; round++)
        {
            using var legatus = new Legatus(new LegatusConfig
            {
                MaxCohorts = 4,
                ThreadCount = Math.Min(4, Environment.ProcessorCount),
                OracleIntervalMs = 20,
                OracleModel = (OraculumModel)(round % 4)
            });

            var cohors = new Cohors(0, new IncrementSystem(0, buffer));
            cohors.WriteMask.Tollere(0);
            legatus.RegisterCohors(cohors);
            legatus.ConstructConsilium();

            legatus.ExecuteFrame();
            legatus.ExecuteFrame();
        }

        Assert.All(buffer, v => Assert.Equal(10, v));
    }

    [Fact]
    public void Speculator_Explorare_IsZeroAllocationOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var speculator = new Legio.Core.Augurium.Speculator(updateIntervalMs: 10);
        // Warmup
        Thread.Sleep(15);
        speculator.Explorare();

        Thread.Sleep(15);
        long before = GC.GetAllocatedBytesForCurrentThread();
        speculator.Explorare();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.InRange(speculator.ProcessCpuLoad, 0.0f, 1.0f);
    }
}
