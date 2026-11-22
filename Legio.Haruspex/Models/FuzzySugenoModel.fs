namespace Legio.Haruspex.Models

open System
open Legio.Protocol
open Legio.Haruspex

/// <summary>
/// Per-cohort state for Model 1: Triangular Fuzzy Numbers + Online-Adaptive Takagi-Sugeno FIS.
/// </summary>
[<Struct>]
type internal FuzzySugenoState =
    val mutable Initialized: bool
    val mutable CostTfn: TriangularFuzzyNumber
    val mutable ImbalanceTfn: TriangularFuzzyNumber
    val mutable BarrierWaitTfn: TriangularFuzzyNumber
    val mutable TargetUsLight: float
    val mutable TargetUsMedium: float
    val mutable TargetUsHeavy: float
    val mutable SkewShrink: float
    val mutable EmaThroughput: float
    val mutable LastTargetUs: float
    val mutable StepSign: float
    val mutable Updates: int

/// <summary>
/// MODEL 1: FuzzySugeno — Triangular Fuzzy Numbers (TFN) + Online-Adaptive Takagi-Sugeno FIS (Soft Computing).
/// Pre-allocates all per-cohort state once at startup for strictly 0 B GC allocation during evaluation.
/// </summary>
type internal FuzzySugenoModel(maxCohorts: int) =
    let _states = Array.zeroCreate<FuzzySugenoState> maxCohorts

    member _.Evaluate(cohortId: int, f: inref<WindowFeatures>, logicalCores: int, physicalCores: int) : Strategia =
        let mutable st = _states.[cohortId]

        if not st.Initialized then
            st.Initialized <- true
            st.CostTfn <- TriangularFuzzyNumber(f.MinUnitNs, f.MeanUnitNs, f.MaxUnitNs)
            st.ImbalanceTfn <- TriangularFuzzyNumber(1.0, f.Imbalance, max f.Imbalance 1.25)
            st.BarrierWaitTfn <- TriangularFuzzyNumber(0.0, f.BarrierWaitRatio, max f.BarrierWaitRatio 0.15)
            st.TargetUsLight <- 140.0
            st.TargetUsMedium <- 240.0
            st.TargetUsHeavy <- 400.0
            st.SkewShrink <- 0.55
            st.EmaThroughput <- f.Throughput
            st.LastTargetUs <- 200.0
            st.StepSign <- 1.0
            st.Updates <- 1
        else
            let alpha = 0.25
            let newMode = (1.0 - alpha) * st.CostTfn.Mode + alpha * f.MeanUnitNs
            let newLow = min newMode ((1.0 - alpha) * st.CostTfn.Low + alpha * f.MinUnitNs)
            let newHigh = max newMode ((1.0 - alpha) * st.CostTfn.High + alpha * f.MaxUnitNs)
            st.CostTfn <- TriangularFuzzyNumber(newLow, newMode, newHigh)

            let imbMode = (1.0 - alpha) * st.ImbalanceTfn.Mode + alpha * f.Imbalance
            let imbLow = min imbMode ((1.0 - alpha) * st.ImbalanceTfn.Low + alpha * 1.0)
            let imbHigh = max imbMode ((1.0 - alpha) * st.ImbalanceTfn.High + alpha * max f.Imbalance imbMode)
            st.ImbalanceTfn <- TriangularFuzzyNumber(imbLow, imbMode, imbHigh)

            let bwMode = (1.0 - alpha) * st.BarrierWaitTfn.Mode + alpha * f.BarrierWaitRatio
            let bwLow = min bwMode ((1.0 - alpha) * st.BarrierWaitTfn.Low + alpha * (f.BarrierWaitRatio * 0.5))
            let bwHigh = max bwMode ((1.0 - alpha) * st.BarrierWaitTfn.High + alpha * max f.BarrierWaitRatio bwMode)
            st.BarrierWaitTfn <- TriangularFuzzyNumber(bwLow, bwMode, bwHigh)

            st.Updates <- st.Updates + 1

        let crispCost = max 0.01 (st.CostTfn.Defuzzify())
        let logCost = Math.Log10(crispCost + 1.0)
        let muLight  = FuzzyMath.leftShoulder  logCost 1.0 2.0
        let muMedium = FuzzyMath.triMembership logCost 1.0 2.2 3.4
        let muHeavy  = FuzzyMath.rightShoulder logCost 2.4 3.6

        let crispImb = st.ImbalanceTfn.Defuzzify()
        let crispWait = st.BarrierWaitTfn.Defuzzify()
        let muSkewed = FuzzyMath.rightShoulder crispImb 1.2 2.2
        let muBarrierWait = FuzzyMath.rightShoulder crispWait 0.12 0.45
        let muSpatialSkew = FuzzyMath.rightShoulder f.SpatialSkewAbs 0.18 0.50
        let muHighContention = FuzzyMath.rightShoulder f.ContentionRate 0.25 0.65

        if st.Updates > 1 then
            // Reward combines throughput with barrier-wait efficiency (Option A.1 loss signal).
            let effThroughput = f.Throughput * (1.0 - 0.35 * f.BarrierWaitRatio)
            let rewardDelta = (effThroughput - st.EmaThroughput) / (st.EmaThroughput + 1e-6)
            if rewardDelta < -0.03 then
                st.StepSign <- -st.StepSign

            let uncertainty = FuzzyMath.clampFloat (st.CostTfn.RelativeWidth()) 0.05 1.0
            let lr = 18.0 * uncertainty * st.StepSign

            st.TargetUsLight  <- FuzzyMath.clampFloat (st.TargetUsLight  + lr * muLight)  40.0 600.0
            st.TargetUsMedium <- FuzzyMath.clampFloat (st.TargetUsMedium + lr * muMedium) 60.0 900.0
            st.TargetUsHeavy  <- FuzzyMath.clampFloat (st.TargetUsHeavy  + lr * muHeavy)  80.0 1600.0

            if muSkewed > 0.3 || muBarrierWait > 0.3 then
                let skewAdj = if rewardDelta >= 0.0 then -0.02 else 0.02
                st.SkewShrink <- FuzzyMath.clampFloat (st.SkewShrink + skewAdj * max muSkewed muBarrierWait) 0.25 0.85

            st.EmaThroughput <- 0.8 * st.EmaThroughput + 0.2 * effThroughput

        let wSum = muLight + muMedium + muHeavy + 1e-9
        let baseTargetUs =
            (muLight * st.TargetUsLight + muMedium * st.TargetUsMedium + muHeavy * st.TargetUsHeavy) / wSum

        let skewPressure = max muSkewed (0.85 * muBarrierWait)
        let skewMultiplier = 1.0 - skewPressure * (1.0 - st.SkewShrink)
        let contentionMultiplier = 1.0 + muHighContention * 0.65
        let targetUs = FuzzyMath.clampFloat (baseTargetUs * skewMultiplier * contentionMultiplier) 25.0 2000.0
        st.LastTargetUs <- targetUs

        let threads =
            FuzzyMath.computeSafeThreads
                f.FrameItems crispCost f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu logicalCores physicalCores

        let rawBatch = (targetUs * 1000.0) / crispCost
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems threads
        let skewCap =
            if skewPressure > 0.4 then max minB (f.FrameItems / max 1 (threads * 6))
            else maxB
        let effMaxB = max minB (min maxB skewCap)
        let minQueueGuard = max minB (f.FrameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB effMaxB

        // Option B control levers driven by fuzzy memberships:
        let partMode =
            if muSpatialSkew > 0.35 || (muSkewed > 0.55 && muBarrierWait > 0.35) then PartitionMode.Interleaved
            else PartitionMode.Contiguous

        let stealMode =
            if muBarrierWait > 0.40 || muSkewed > 0.50 then StealMode.StealHalf
            else StealMode.StealOne

        let affinity =
            FuzzyMath.computeAffinityHint threads physicalCores f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu

        let spinBudget =
            FuzzyMath.computeSpinBudget f.MeanDurUs f.ExternalCpuLoad crispWait

        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.5 * st.CostTfn.RelativeWidth()) 0.15 0.99)
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
