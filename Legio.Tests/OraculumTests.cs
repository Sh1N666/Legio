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

    private static SystemTelemetry MakeSys(
        float processCpu = 0.4f,
        float externalCpu = 0.05f,
        float ipc = 2.0f,
        float cacheMissRate = 0.08f,
        bool hasPmu = true) => new()
    {
        ProcessCpuLoad = processCpu,
        SystemCpuLoad = Math.Clamp(processCpu + externalCpu, 0.0f, 1.0f),
        ExternalCpuLoad = externalCpu,
        InstructionsPerCycle = ipc,
        CacheMissRate = cacheMissRate,
        HasPmuCounters = hasPmu ? 1 : 0,
        HasExternalCpu = 1
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
    [InlineData(OraculumModel.OnlineLinUcb)]
    public void AllModels_HandleExtremeAndMalformedInputsWithoutThrowing(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        SystemTelemetry sys = MakeSys();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        // 1. Empty span
        engine.Evaluate(in topo, in sys, ReadOnlySpan<Tessera>.Empty, strategies, updated);
        Assert.All(updated, b => Assert.Equal(0, b));

        // 2. Zero items, zero ticks, negative CohortId, out-of-range CohortId, and huge summed int64 items (> Int32.MaxValue)
        var edgeOmens = new Tessera[]
        {
            new() { CohortId = 0, ItemCount = 0, DurationTicks = 1000 },
            new() { CohortId = 0, ItemCount = 100, DurationTicks = 0 },
            new() { CohortId = -1, ItemCount = 100, DurationTicks = 1000 },
            new() { CohortId = 99, ItemCount = 100, DurationTicks = 1000 },
            // Overflow test: 3 tablets x 1_000_000_000 items > Int32.MaxValue (used to crash old Oraculum!)
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L, BarrierWaitTicks = -50L },
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L },
            new() { CohortId = 1, ItemCount = 1_000_000_000, TotalCohortItems = 1_000_000_000, DurationTicks = 500_000_000L },
            // Tiny duration (1 tick per 10000 items) -> would overflow int(timeBasedBatch) if unclamped
            new() { CohortId = 2, ItemCount = 10_000, TotalCohortItems = 10_000, DurationTicks = 1L }
        };

        engine.Evaluate(in topo, in sys, edgeOmens, strategies, updated);

        Assert.Equal(0, updated[0]);
        Assert.Equal(1, updated[1]);
        Assert.Equal(1, updated[2]);

        Assert.True(strategies[1].BatchSize >= 16);
        Assert.InRange(strategies[1].ThreadCount, 1, 16);
        Assert.InRange(strategies[1].Confidence, 0.0f, 1.0f);
        Assert.InRange(strategies[1].SpinIterationsBeforePark, 64, 4096);

        Assert.True(strategies[2].BatchSize >= 16 && strategies[2].BatchSize <= 10_000);
        Assert.InRange(strategies[2].ThreadCount, 1, 16);
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    [InlineData(OraculumModel.ContextualBandit)]
    [InlineData(OraculumModel.OnlineLinUcb)]
    public void AllModels_IsolateCohorts_AndTailorBatchSizeToWorkloadWeight(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        SystemTelemetry sys = MakeSys();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        // Cohort 0: Light uniform workload (2 ns/item, 500k items)
        // Cohort 1: Heavy skewed workload (5000 ns/item, 500k items, high imbalance & spatial skew)
        var omens = new Tessera[]
        {
            new() { CohortId = 0, CenturioId = 0, ItemCount = 50_000, TotalCohortItems = 500_000, DurationTicks = 100_000L, FirstHalfTicks = 50_000L, SecondHalfTicks = 50_000L, ActiveWorkers = 16 },
            new() { CohortId = 0, CenturioId = 1, ItemCount = 50_000, TotalCohortItems = 500_000, DurationTicks = 105_000L, FirstHalfTicks = 52_000L, SecondHalfTicks = 53_000L, ActiveWorkers = 16 },
            new() { CohortId = 1, CenturioId = 0, ItemCount = 1_000,  TotalCohortItems = 500_000, DurationTicks = 5_000_000L, FirstHalfTicks = 4_800_000L, SecondHalfTicks = 200_000L, BarrierWaitTicks = 2_000_000L, StealCount = 4, ActiveWorkers = 16 },
            new() { CohortId = 1, CenturioId = 1, ItemCount = 1_000,  TotalCohortItems = 500_000, DurationTicks = 18_000_000L, FirstHalfTicks = 17_000_000L, SecondHalfTicks = 1_000_000L, BarrierWaitTicks = 6_000_000L, StealCount = 8, ActiveWorkers = 16 }
        };

        // Run several windows so online models converge
        for (int step = 0; step < 20; step++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
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
    [InlineData(OraculumModel.OnlineLinUcb)]
    public void AllModels_EvaluateWithStrictlyZeroHeapAllocations(OraculumModel model)
    {
        var engine = Oracle.createEngine(8, model, 16, 1024);
        Topologia topo = MakeTopo();
        SystemTelemetry sys = MakeSys();
        var strategies = new Strategia[8];
        var updated = new byte[8];

        var omens = new Tessera[16];
        for (int i = 0; i < omens.Length; i++)
        {
            long dur = (i % 2 == 0 ? 150_000L : 2_400_000L) + i * 10_000L;
            omens[i] = new Tessera
            {
                CohortId = i % 2,
                CenturioId = i,
                ItemCount = 4096,
                TotalCohortItems = 262_144,
                DurationTicks = dur,
                BarrierWaitTicks = dur / 4,
                FirstHalfTicks = (i % 2 == 0) ? dur / 2 : (dur * 4) / 5,
                SecondHalfTicks = (i % 2 == 0) ? dur / 2 : dur / 5,
                StealCount = i % 3,
                ContentionCount = i % 2,
                CpuPressure = 0.45f,
                ActiveWorkers = 16
            };
        }

        // Warmup JIT
        for (int w = 0; w < 25; w++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < 200; r++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void ContextualBandit_ConvergesTowardOptimalBatchRegion()
    {
        var engine = Oracle.createEngine(4, OraculumModel.ContextualBandit, 16, 1024);
        Topologia topo = MakeTopo();
        SystemTelemetry sys = MakeSys();
        var strategies = new Strategia[4];
        var updated = new byte[4];

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
                TotalCohortItems = 500_000,
                DurationTicks = simulatedDurationTicks,
                ActiveWorkers = 16
            };
            omens[1] = new Tessera
            {
                CohortId = 0,
                CenturioId = 1,
                ItemCount = 20_000,
                TotalCohortItems = 500_000,
                DurationTicks = simulatedDurationTicks + 5_000,
                ActiveWorkers = 16
            };

            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
            currentBatch = strategies[0].BatchSize;
        }

        Assert.InRange(currentBatch, 400, 6000);
        Assert.True(strategies[0].Confidence > 0.3f);
    }

    [Fact]
    public void OnlineLinUcb_LearnsContextualActionPreferences_AcrossTwoDistinctRegimes()
    {
        var engine = Oracle.createEngine(4, OraculumModel.OnlineLinUcb, 16, 1024);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[4];
        var updated = new byte[4];

        // Synthetic contextual environment:
        // - In Regime A (SpatialSkewAbs > 0.6, BarrierWait high): Interleaved + StealHalf achieves 4x faster duration.
        // - In Regime B (Uniform SpatialSkew = 0, Low BarrierWait): Contiguous + StealOne achieves 3x faster duration.
        Strategia current = new()
        {
            BatchSize = 512,
            ThreadCount = 16,
            PartitionMode = PartitionMode.Contiguous,
            StealMode = StealMode.StealOne
        };

        var omens = new Tessera[2];
        int interleavedChosenInSkewRegime = 0;
        int contiguousChosenInUniformRegime = 0;

        for (int step = 0; step < 80; step++)
        {
            bool skewedRegime = (step % 2) == 0;
            SystemTelemetry sys = MakeSys(processCpu: 0.5f, externalCpu: 0.05f, ipc: skewedRegime ? 1.8f : 2.6f, cacheMissRate: 0.08f);

            long durTicks;
            long barrierWait;
            long firstHalf;
            long secondHalf;

            if (skewedRegime)
            {
                bool optimal = current.PartitionMode == PartitionMode.Interleaved && current.StealMode == StealMode.StealHalf;
                durTicks = optimal ? 250_000L : 1_100_000L;
                barrierWait = optimal ? 15_000L : 450_000L;
                firstHalf = (durTicks * 9) / 10;
                secondHalf = durTicks / 10;
            }
            else
            {
                bool optimal = current.PartitionMode == PartitionMode.Contiguous && current.StealMode == StealMode.StealOne;
                durTicks = optimal ? 220_000L : 850_000L;
                barrierWait = optimal ? 10_000L : 120_000L;
                firstHalf = durTicks / 2;
                secondHalf = durTicks / 2;
            }

            omens[0] = new Tessera
            {
                CohortId = 0,
                CenturioId = 0,
                ItemCount = 10_000,
                TotalCohortItems = 200_000,
                DurationTicks = durTicks,
                BarrierWaitTicks = barrierWait,
                FirstHalfTicks = firstHalf,
                SecondHalfTicks = secondHalf,
                ActiveWorkers = 16
            };
            omens[1] = new Tessera
            {
                CohortId = 0,
                CenturioId = 1,
                ItemCount = 10_000,
                TotalCohortItems = 200_000,
                DurationTicks = durTicks + (skewedRegime ? 80_000L : 5_000L),
                BarrierWaitTicks = barrierWait,
                FirstHalfTicks = firstHalf,
                SecondHalfTicks = secondHalf,
                ActiveWorkers = 16
            };

            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
            current = strategies[0];

            // Measure post-exploration accuracy (last 30 steps)
            if (step >= 50)
            {
                if (skewedRegime && current.PartitionMode == PartitionMode.Interleaved)
                    interleavedChosenInSkewRegime++;
                if (!skewedRegime && current.PartitionMode == PartitionMode.Contiguous)
                    contiguousChosenInUniformRegime++;
            }
        }

        Assert.True(interleavedChosenInSkewRegime >= 10, $"Expected LinUCB to prefer Interleaved in skewed regime, got {interleavedChosenInSkewRegime}/15.");
        Assert.True(contiguousChosenInUniformRegime >= 10, $"Expected LinUCB to prefer Contiguous in uniform regime, got {contiguousChosenInUniformRegime}/15.");
    }

    [Theory]
    [InlineData(OraculumModel.StaticHeuristic)]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    public void DeterministicModels_KeepFullThreadUtilization_UnderOwnHighProcessCpuLoad(OraculumModel model)
    {
        var engine = Oracle.createEngine(4, model, 16, 256);
        Topologia topo = MakeTopo();
        // Own process uses 98% CPU, External processes use 0% -> must NOT throttle self!
        SystemTelemetry sys = MakeSys(processCpu: 0.98f, externalCpu: 0.0f, ipc: 2.2f, cacheMissRate: 0.05f);
        var strategies = new Strategia[4];
        var updated = new byte[4];

        var omens = new Tessera[]
        {
            new() { CohortId = 0, CenturioId = 0, ItemCount = 4_000, TotalCohortItems = 57_600, DurationTicks = 2_000_000L, CpuPressure = 0.98f, ActiveWorkers = 16 },
            new() { CohortId = 0, CenturioId = 1, ItemCount = 4_000, TotalCohortItems = 57_600, DurationTicks = 2_500_000L, CpuPressure = 0.98f, ActiveWorkers = 16 }
        };

        for (int step = 0; step < 10; step++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in sys, omens, strategies, updated);
            Assert.Equal(16, strategies[0].ThreadCount);
        }
    }

    [Theory]
    [InlineData(OraculumModel.FuzzySugeno)]
    [InlineData(OraculumModel.OrderedFuzzyNumbers)]
    public void AdaptiveModels_RespondToOptionATelemetry_WithOptionBControls(OraculumModel model)
    {
        var engine = Oracle.createEngine(4, model, 16, 256);
        Topologia topo = MakeTopo();
        var strategies = new Strategia[4];
        var updated = new byte[4];

        // 1. High spatial skew + high barrier wait -> must activate Interleaved + StealHalf
        SystemTelemetry cleanSys = MakeSys(processCpu: 0.5f, externalCpu: 0.02f, ipc: 2.0f, cacheMissRate: 0.05f);
        var skewedOmens = new Tessera[]
        {
            new() { CohortId = 0, CenturioId = 0, ItemCount = 4_000, TotalCohortItems = 64_000, DurationTicks = 800_000L, BarrierWaitTicks = 700_000L, FirstHalfTicks = 750_000L, SecondHalfTicks = 50_000L, ActiveWorkers = 16 },
            new() { CohortId = 0, CenturioId = 1, ItemCount = 4_000, TotalCohortItems = 64_000, DurationTicks = 2_000_000L, BarrierWaitTicks = 800_000L, FirstHalfTicks = 1_900_000L, SecondHalfTicks = 100_000L, ActiveWorkers = 16 }
        };

        for (int step = 0; step < 8; step++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in cleanSys, skewedOmens, strategies, updated);
        }

        Assert.Equal(PartitionMode.Interleaved, strategies[0].PartitionMode);
        Assert.Equal(StealMode.StealHalf, strategies[0].StealMode);
        int normalSpin = strategies[0].SpinIterationsBeforePark;

        // 2. High ExternalCpuLoad (other apps saturating host) + High PMU cache miss & low IPC -> must yield threads, drop spin, prefer physical cores
        SystemTelemetry contendedSys = MakeSys(processCpu: 0.25f, externalCpu: 0.70f, ipc: 0.60f, cacheMissRate: 0.55f, hasPmu: true);
        for (int step = 0; step < 6; step++)
        {
            Array.Clear(updated);
            engine.Evaluate(in topo, in contendedSys, skewedOmens, strategies, updated);
        }

        Assert.True(strategies[0].ThreadCount < 16, $"Expected thread yield under 70% external CPU load, got {strategies[0].ThreadCount}.");
        Assert.True(strategies[0].SpinIterationsBeforePark < normalSpin, $"Expected lower spin budget under external CPU load ({strategies[0].SpinIterationsBeforePark} vs {normalSpin}).");
        Assert.Equal((int)AffinityMode.PhysicalCoresFirst, strategies[0].AffinityHint);
    }
}
