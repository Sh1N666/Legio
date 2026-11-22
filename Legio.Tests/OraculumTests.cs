using Legio.Haruspex;
using Legio.Protocol;
using Xunit;

namespace Legio.Tests;

public class OraculumTests
{
    private static Topologia MakeTopo() => new()
    {
        StopwatchFrequency = 1_000_000_000L, // 1 GHz (1 ns/tick)
        LogicalCoreCount = 16,
        PhysicalCoreCount = 8,
        PerformanceCoreCount = 8,
        CacheLineSize = 64,
        Features = CpuFeatures.Avx2
    };

    [Fact]
    public void FuzzyNumbers_TfnAndOfn_RespectMathematicalInvariants()
    {
        var tfn = new TriangularFuzzyNumber(10.0, 20.0, 50.0);
        Assert.Equal(10.0, tfn.Low);
        Assert.Equal(20.0, tfn.Mode);
        Assert.Equal(50.0, tfn.High);
        Assert.Equal((10.0 + 20.0 + 50.0) / 3.0, tfn.Defuzzify(), precision: 6);
        Assert.True(tfn.RelativeWidth() > 0.0);

        var risingOfn = new OrderedFuzzyNumber(10.0, 15.0, 25.0, 30.0);
        var fallingOfn = new OrderedFuzzyNumber(30.0, 25.0, 15.0, 10.0);

        Assert.Equal(1.0, risingOfn.Orientation());
        Assert.Equal(-1.0, fallingOfn.Orientation());
        Assert.Equal(20.0, risingOfn.Spread(), precision: 6);
        Assert.Equal(20.0, fallingOfn.Spread(), precision: 6);
        Assert.Equal(20.0, risingOfn.Defuzzify(), precision: 6);
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    [InlineData(OraculumModel.ContextualBandit)]
    public void AllModels_HandleExtremeAndMalformedInputsWithoutThrowing(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        // 1. Empty span
        engine.Evaluate(in topo, 0.2f, ReadOnlySpan<Tessera>.Empty, strategies, updated);
        Assert.All(updated, b => Assert.Equal(0, b));

        // 2. Zero items, zero ticks, negative CohortId, out-of-range CohortId, and huge summed int64 items (> Int32.MaxValue)
        var edgeOmens = new Tessera[]
        {
            new() { CohortId = 0, ItemCount = 0, DurationTicks = 1000 },
            new() { CohortId = 0, ItemCount = 100, DurationTicks = 0 },
            new() { CohortId = -1, ItemCount = 100, DurationTicks = 1000 },
            new() { CohortId = 99, ItemCount = 100, DurationTicks = 1000 },
            // Overflow test: 3 tablets x 1_000_000_000 items > Int32.MaxValue (used to crash old Oraculum!)
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L },
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L },
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L },
            // Tiny duration (1 tick per 10000 items) -> would overflow int(timeBasedBatch) if unclamped
            new() { CohortId = 2, ItemCount = 10_000, TotalCohortItems = 10_000, DurationTicks = 1L }
        };

        engine.Evaluate(in topo, 0.5f, edgeOmens, strategies, updated);

        Assert.Equal(0, updated[0]);
        Assert.Equal(1, updated[1]);
        Assert.Equal(1, updated[2]);

        Assert.True(strategies[1].BatchSize >= 16);
        Assert.InRange(strategies[1].ThreadCount, 1, 16);
        Assert.InRange(strategies[1].Confidence, 0.0f, 1.0f);

        Assert.True(strategies[2].BatchSize >= 16 && strategies[2].BatchSize <= 10_000);
        Assert.InRange(strategies[2].ThreadCount, 1, 16);
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    [InlineData(OraculumModel.ContextualBandit)]
    public void AllModels_IsolateCohorts_AndTailorBatchSizeToWorkloadWeight(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        // Cohort 0: Light workload (2 ns/item, 500k items)
        // Cohort 1: Heavy skewed workload (5000 ns/item, 500k items, high imbalance)
        var omens = new Tessera[]
        {
            new() { CohortId = 0, CenturioId = 0, ItemCount = 50_000, TotalCohortItems = 500_000, DurationTicks = 100_000L },
            new() { CohortId = 0, CenturioId = 1, ItemCount = 50_000, TotalCohortItems = 500_000, DurationTicks = 105_000L },
            new() { CohortId = 1, CenturioId = 0, ItemCount = 1_000,  TotalCohortItems = 500_000, DurationTicks = 5_000_000L, StealCount = 4 },
            new() { CohortId = 1, CenturioId = 1, ItemCount = 1_000,  TotalCohortItems = 500_000, DurationTicks = 18_000_000L, StealCount = 8 }
        };

        // Run several windows so online models converge
        for (int step = 0; step < 15; step++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, 0.4f, omens, strategies, updated);
        }

        Assert.Equal(1, updated[0]);
        Assert.Equal(1, updated[1]);
        Assert.Equal(0, updated[2]);

        Assert.True(
            strategies[0].BatchSize > strategies[1].BatchSize,
            $"Model {model}: expected light Cohort 0 batch ({strategies[0].BatchSize}) > heavy skewed Cohort 1 batch ({strategies[1].BatchSize}).");
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    [InlineData(OraculumModel.ContextualBandit)]
    public void AllModels_EvaluateWithStrictlyZeroHeapAllocations(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        var omens = new Tessera[16];
        for (int i = 0; i < omens.Length; i++)
        {
            omens[i] = new Tessera
            {
                CohortId = i % 2,
                CenturioId = i,
                ItemCount = 4096,
                TotalCohortItems = 262_144,
                DurationTicks = (i % 2 == 0 ? 150_000L : 2_400_000L) + i * 10_000L,
                StealCount = i % 3,
                ContentionCount = i % 2,
                CpuPressure = 0.45f
            };
        }

        // Warmup JIT
        for (int w = 0; w < 20; w++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, 0.45f, omens, strategies, updated);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < 200; r++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, 0.45f, omens, strategies, updated);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void ContextualBandit_ConvergesTowardOptimalBatchRegion()
    {
        var engine = Oracle.createEngine(4, OraculumModel.ContextualBandit, 16, 1024);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[4];
        var updated = new byte[4];

        // Synthetic environment where BatchSize near 2000 achieves lowest wall duration (~200us),
        // while very tiny or very huge batches are much slower.
        int currentBatch = 1024;
        var omens = new Tessera[2];

        for (int step = 0; step < 40; step++)
        {
            double dist = Math.Abs(Math.Log2(Math.Max(16, currentBatch) / 2000.0));
            long simulatedDurationTicks = (long)(200_000.0 * (1.0 + dist * dist * 3.0));

            omens[0] = new Tessera
            {
                CohortId = 0,
                CenturioId = 0,
                ItemCount = 20_000,
                TotalCohortItems = 200_000,
                DurationTicks = simulatedDurationTicks
            };
            omens[1] = new Tessera
            {
                CohortId = 0,
                CenturioId = 1,
                ItemCount = 20_000,
                TotalCohortItems = 200_000,
                DurationTicks = simulatedDurationTicks + 5_000
            };

            Array.Clear(updated);
            engine.Evaluate(in topo, 0.3f, omens, strategies, updated);
            currentBatch = strategies[0].BatchSize;
        }

        Assert.InRange(currentBatch, 400, 6000);
        Assert.True(strategies[0].Confidence > 0.3f);
    }
}
