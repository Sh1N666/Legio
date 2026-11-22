namespace Legio.Haruspex.Models

open Legio.Protocol
open Legio.Haruspex

/// <summary>
/// Per-cohort state for Model 2: Kosiński's Ordered Fuzzy Numbers (OFN) trend-aware controller.
/// </summary>
[<Struct>]
type internal OfnState =
    val mutable Initialized: bool
    val mutable CostOfn: OrderedFuzzyNumber
    val mutable ThroughputOfn: OrderedFuzzyNumber
    val mutable BarrierWaitOfn: OrderedFuzzyNumber
    val mutable TargetUs: float
    val mutable LastUnitNs: float
    val mutable LastThroughput: float
    val mutable LastBarrierWaitRatio: float
    val mutable StepSign: float
    val mutable Updates: int

/// <summary>
/// MODEL 2: OrderedFuzzyNumbers — Kosiński's OFN controller tracking uncertainty and directional trends
/// across three oriented quantities (Cost, Throughput, BarrierWait) with Option A+B control levers.
/// </summary>
type internal OrderedFuzzyModel(maxCohorts: int) =
    let _states = Array.zeroCreate<OfnState> maxCohorts

    member _.Evaluate(cohortId: int, f: inref<WindowFeatures>, logicalCores: int, physicalCores: int) : Strategia =
        let mutable st = _states.[cohortId]

        if not st.Initialized then
            st.Initialized <- true
            st.CostOfn <- OrderedFuzzyNumber(f.MinUnitNs, f.MeanUnitNs, f.MeanUnitNs, f.MaxUnitNs)
            st.ThroughputOfn <- OrderedFuzzyNumber(f.Throughput * 0.9, f.Throughput, f.Throughput, f.Throughput * 1.1)
            st.BarrierWaitOfn <- OrderedFuzzyNumber(0.0, f.BarrierWaitRatio, f.BarrierWaitRatio, max 0.05 (f.BarrierWaitRatio * 1.2))
            st.TargetUs <- 200.0
            st.LastUnitNs <- f.MeanUnitNs
            st.LastThroughput <- f.Throughput
            st.LastBarrierWaitRatio <- f.BarrierWaitRatio
            st.StepSign <- 1.0
            st.Updates <- 1
        else
            let alpha = 0.30
            let costRising = f.MeanUnitNs >= st.LastUnitNs
            let struct (newF0, newF1, newG1, newG0) =
                if costRising then
                    struct (f.MinUnitNs, st.LastUnitNs, f.MeanUnitNs, f.MaxUnitNs)
                else
                    struct (f.MaxUnitNs, st.LastUnitNs, f.MeanUnitNs, f.MinUnitNs)

            st.CostOfn <- OrderedFuzzyNumber(
                (1.0 - alpha) * st.CostOfn.F0 + alpha * newF0,
                (1.0 - alpha) * st.CostOfn.F1 + alpha * newF1,
                (1.0 - alpha) * st.CostOfn.G1 + alpha * newG1,
                (1.0 - alpha) * st.CostOfn.G0 + alpha * newG0
            )

            let tpImproving = f.Throughput >= st.LastThroughput
            let tpLo = min st.LastThroughput f.Throughput
            let tpHi = max st.LastThroughput f.Throughput
            let struct (tF0, tF1, tG1, tG0) =
                if tpImproving then struct (tpLo * 0.95, st.LastThroughput, f.Throughput, tpHi * 1.05)
                else                struct (tpHi * 1.05, st.LastThroughput, f.Throughput, tpLo * 0.95)

            st.ThroughputOfn <- OrderedFuzzyNumber(
                (1.0 - alpha) * st.ThroughputOfn.F0 + alpha * tF0,
                (1.0 - alpha) * st.ThroughputOfn.F1 + alpha * tF1,
                (1.0 - alpha) * st.ThroughputOfn.G1 + alpha * tG1,
                (1.0 - alpha) * st.ThroughputOfn.G0 + alpha * tG0
            )

            let bwRising = f.BarrierWaitRatio >= st.LastBarrierWaitRatio
            let bwLo = min st.LastBarrierWaitRatio f.BarrierWaitRatio
            let bwHi = max st.LastBarrierWaitRatio f.BarrierWaitRatio
            let struct (bF0, bF1, bG1, bG0) =
                if bwRising then struct (bwLo * 0.9, st.LastBarrierWaitRatio, f.BarrierWaitRatio, bwHi * 1.1)
                else             struct (bwHi * 1.1, st.LastBarrierWaitRatio, f.BarrierWaitRatio, bwLo * 0.9)

            st.BarrierWaitOfn <- OrderedFuzzyNumber(
                (1.0 - alpha) * st.BarrierWaitOfn.F0 + alpha * bF0,
                (1.0 - alpha) * st.BarrierWaitOfn.F1 + alpha * bF1,
                (1.0 - alpha) * st.BarrierWaitOfn.G1 + alpha * bG1,
                (1.0 - alpha) * st.BarrierWaitOfn.G0 + alpha * bG0
            )

            let costTrend = st.CostOfn.Orientation()
            let tpTrend   = st.ThroughputOfn.Orientation()
            let bwTrend   = st.BarrierWaitOfn.Orientation()

            if tpTrend < 0.0 then
                st.StepSign <- -st.StepSign

            let spreadRatio = FuzzyMath.clampFloat (st.CostOfn.Spread() / (abs (st.CostOfn.Defuzzify()) + 1e-6)) 0.05 1.0

            let trendMultiplier =
                if costTrend > 0.0 || f.Imbalance > 1.4 || (bwTrend > 0.0 && f.BarrierWaitRatio > 0.18) then
                    1.0 - 0.14 * spreadRatio
                elif f.ContentionRate > 0.35 then
                    1.0 + 0.18 * spreadRatio
                else
                    1.0 + 0.08 * st.StepSign * spreadRatio

            st.TargetUs <- FuzzyMath.clampFloat (st.TargetUs * trendMultiplier) 30.0 1800.0
            st.LastUnitNs <- f.MeanUnitNs
            st.LastThroughput <- f.Throughput
            st.LastBarrierWaitRatio <- f.BarrierWaitRatio
            st.Updates <- st.Updates + 1

        let defuzzCost = max 0.01 (st.CostOfn.Defuzzify())
        let defuzzWait = FuzzyMath.clampFloat (st.BarrierWaitOfn.Defuzzify()) 0.0 1.0
        let threads =
            FuzzyMath.computeSafeThreads
                f.FrameItems defuzzCost f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu logicalCores physicalCores

        let rawBatch = (st.TargetUs * 1000.0) / defuzzCost
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems threads
        let minQueueGuard = max minB (f.FrameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        // Option B levers guided by OFN directional trends & spatial skew:
        let partMode =
            if f.SpatialSkewAbs > 0.22 || (f.Imbalance > 1.55 && st.BarrierWaitOfn.Orientation() > 0.0 && defuzzWait > 0.15) then
                PartitionMode.Interleaved
            else
                PartitionMode.Contiguous

        let stealMode =
            if defuzzWait > 0.20 || f.Imbalance > 1.50 then StealMode.StealHalf
            else StealMode.StealOne

        let affinity =
            FuzzyMath.computeAffinityHint threads physicalCores f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu

        let spinBudget =
            FuzzyMath.computeSpinBudget f.MeanDurUs f.ExternalCpuLoad defuzzWait

        let relSpread = st.CostOfn.Spread() / (defuzzCost + 1e-6)
        let tpBonus = if st.ThroughputOfn.Orientation() > 0.0 then 0.10 else -0.05
        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.45 * relSpread + tpBonus) 0.15 0.99)
        _states.[cohortId] <- st

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = confidence,
            AffinityHint = affinity,
            SpinIterationsBeforePark = spinBudget,
            PartitionMode = partMode,
            StealMode = stealMode
        )
