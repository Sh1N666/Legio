namespace Legio.Haruspex.Models

open System
open Legio.Protocol
open Legio.Haruspex

/// <summary>
/// Per-cohort metadata for Model 3: Zero-Allocation Discounted UCB1 Bandit.
/// </summary>
[<Struct>]
type internal BanditState =
    val mutable LastArm: int
    val mutable TotalUpdates: int
    val mutable BestRewardScale: float

/// <summary>
/// MODEL 3: ContextualBandit — Zero-Allocation Discounted UCB1 Multi-Armed Bandit (Tabular Online RL)
/// over 8 structured Option B strategy profiles.
/// </summary>
type internal ContextualBanditModel(maxCohorts: int) =

    static let ArmCount = 8
    static let TargetUs     = [|  50.0; 120.0; 240.0; 480.0;  80.0; 180.0; 220.0; 360.0 |]
    static let ThreadRatio  = [|   1.0;   1.0;   1.0;   1.0;   1.0;   1.0;   0.5;  0.75 |]
    static let Partition    = [| PartitionMode.Contiguous; PartitionMode.Contiguous; PartitionMode.Contiguous; PartitionMode.Contiguous
                                 PartitionMode.Interleaved; PartitionMode.Interleaved; PartitionMode.Contiguous; PartitionMode.Interleaved |]
    static let Steal        = [| StealMode.StealOne; StealMode.StealOne; StealMode.StealOne; StealMode.StealOne
                                 StealMode.StealHalf; StealMode.StealHalf; StealMode.StealOne; StealMode.StealHalf |]
    static let Affinity     = [| 0; 0; 0; 0; 0; 0; 1; 1 |]
    static let Spin         = [| 2048; 1536; 1024; 512; 2048; 1536; 512; 768 |]

    let _states = Array.zeroCreate<BanditState> maxCohorts
    let _rewards = Array.zeroCreate<float> (maxCohorts * ArmCount)
    let _counts = Array.zeroCreate<float> (maxCohorts * ArmCount)

    member _.Evaluate(cohortId: int, f: inref<WindowFeatures>, logicalCores: int, physicalCores: int) : Strategia =
        let mutable st = _states.[cohortId]
        let baseIdx = cohortId * ArmCount
        let gamma = 0.96

        let reward =
            Math.Log(1.0 + max 0.0 f.Throughput)
            - 0.30 * f.BarrierWaitRatio
            - 0.10 * f.ContentionRate

        if reward > st.BestRewardScale then
            st.BestRewardScale <- reward

        if st.TotalUpdates > 0 then
            for a = 0 to ArmCount - 1 do
                _counts.[baseIdx + a]  <- _counts.[baseIdx + a] * gamma
                _rewards.[baseIdx + a] <- _rewards.[baseIdx + a] * gamma

            let prev = st.LastArm
            _counts.[baseIdx + prev]  <- _counts.[baseIdx + prev] + 1.0
            _rewards.[baseIdx + prev] <- _rewards.[baseIdx + prev] + reward

        let mutable totalN = 0.0
        for a = 0 to ArmCount - 1 do
            totalN <- totalN + _counts.[baseIdx + a]

        let mutable chosenArm = 0
        if st.TotalUpdates < ArmCount then
            chosenArm <- st.TotalUpdates
        else
            let logTotal = Math.Log(max 1.0 totalN + 1.0)
            let exploreC = 0.35
            let mutable bestScore = Double.NegativeInfinity

            for a = 0 to ArmCount - 1 do
                let n = max 0.05 _counts.[baseIdx + a]
                let meanR = _rewards.[baseIdx + a] / n
                let ucb = meanR + exploreC * Math.Sqrt(logTotal / n)
                if ucb > bestScore then
                    bestScore <- ucb
                    chosenArm <- a

        st.TotalUpdates <- st.TotalUpdates + 1
        st.LastArm <- chosenArm
        _states.[cohortId] <- st

        let baseThreads =
            FuzzyMath.computeSafeThreads
                f.FrameItems f.MeanUnitNs f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu logicalCores physicalCores
        let armThreads =
            if baseThreads <= 1 then 1
            else max 1 (min logicalCores (int (Math.Round(float baseThreads * ThreadRatio.[chosenArm]))))

        let skewScale = 1.0 / max 1.0 (sqrt f.Imbalance)
        let targetUs = TargetUs.[chosenArm] * skewScale
        let rawBatch = (targetUs * 1000.0) / max 0.01 f.MeanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems armThreads
        let minQueueGuard = max minB (f.FrameItems / max 1 (armThreads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        let armShare =
            if totalN > 1.0 then _counts.[baseIdx + chosenArm] / totalN
            else 0.25
        let confidence = float32 (FuzzyMath.clampFloat (0.2 + 0.8 * armShare) 0.15 0.99)

        Strategia(
            BatchSize = batch,
            ThreadCount = armThreads,
            Confidence = confidence,
            AffinityHint = Affinity.[chosenArm],
            SpinIterationsBeforePark = Spin.[chosenArm],
            PartitionMode = Partition.[chosenArm],
            StealMode = Steal.[chosenArm]
        )
