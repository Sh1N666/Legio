namespace Legio.Haruspex.Models

open System
open Legio.Protocol
open Legio.Haruspex

/// <summary>
/// Per-cohort metadata for Model 4: Classical Online ML (Discounted LinUCB / Online Ridge Regression RLS).
/// </summary>
[<Struct>]
type internal LinUcbState =
    val mutable Initialized: bool
    val mutable LastArm: int
    val mutable TotalUpdates: int
    val mutable EmaThroughput: float

/// <summary>
/// MODEL 4: OnlineLinUcb — Classical Online Machine Learning (Discounted Disjoint LinUCB, Li et al. WWW 2010).
/// Trains per-arm Online Ridge Regression models via exact Sherman-Morrison Recursive Least Squares (O(d^2))
/// over 10 normalized Option A telemetry features and 6 orthogonal Option B strategy profiles, with 0 B GC.
/// </summary>
type internal OnlineLinUcbModel(maxCohorts: int) =

    static let Dim = 10
    static let ArmCount = 6
    static let TargetUs     = [| 160.0; 360.0;  80.0; 160.0; 240.0; 120.0 |]
    static let ThreadRatio  = [|   1.0;   1.0;   1.0;   1.0;  0.75;   0.5 |]
    static let Partition    = [| PartitionMode.Contiguous; PartitionMode.Contiguous
                                 PartitionMode.Interleaved; PartitionMode.Interleaved
                                 PartitionMode.Contiguous; PartitionMode.Interleaved |]
    static let Steal        = [| StealMode.StealOne; StealMode.StealOne
                                 StealMode.StealHalf; StealMode.StealOne
                                 StealMode.StealOne; StealMode.StealHalf |]
    static let Affinity     = [| 0; 0; 0; 0; 1; 1 |]
    static let Spin         = [| 1536; 512; 2048; 1024; 768; 512 |]

    let _states = Array.zeroCreate<LinUcbState> maxCohorts
    let _aInv = Array.zeroCreate<float> (maxCohorts * ArmCount * Dim * Dim)
    let _b = Array.zeroCreate<float> (maxCohorts * ArmCount * Dim)
    let _curX = Array.zeroCreate<float> Dim
    let _tmpV = Array.zeroCreate<float> Dim
    let _theta = Array.zeroCreate<float> Dim

    let initCohort (c: int) =
        let cohortMatBase = c * ArmCount * Dim * Dim
        let cohortBBase = c * ArmCount * Dim
        for a = 0 to ArmCount - 1 do
            let matOff = cohortMatBase + a * Dim * Dim
            let bOff = cohortBBase + a * Dim
            for i = 0 to Dim - 1 do
                _b.[bOff + i] <- 0.0
                for j = 0 to Dim - 1 do
                    // Initialize A_a^{-1} = (1 / lambda) * I with L2 regularization lambda = 0.5
                    _aInv.[matOff + i * Dim + j] <- if i = j then 2.0 else 0.0

    let fillContextVector (f: inref<WindowFeatures>) (dst: float[]) =
        dst.[0] <- 1.0                                                                        // Bias
        dst.[1] <- FuzzyMath.clampFloat (Math.Log10(f.MeanUnitNs + 1.0) / 5.0) 0.0 1.0        // Log per-item cost
        dst.[2] <- FuzzyMath.clampFloat (Math.Log10(float f.FrameItems + 1.0) / 6.0) 0.0 1.0  // Log workload size
        dst.[3] <- FuzzyMath.clampFloat ((f.Imbalance - 1.0) / 4.0) 0.0 1.0                   // Straggler imbalance
        dst.[4] <- FuzzyMath.clampFloat f.BarrierWaitRatio 0.0 1.0                            // Option A.1: Barrier wait ratio
        dst.[5] <- FuzzyMath.clampFloat f.SpatialSkewAbs 0.0 1.0                              // Option A.4: Spatial centroid skew
        dst.[6] <- FuzzyMath.clampFloat f.ContentionRate 0.0 1.0                              // CAS steal contention rate
        dst.[7] <- FuzzyMath.clampFloat f.ExternalCpuLoad 0.0 1.0                             // Option A.3: External CPU load
        dst.[8] <- FuzzyMath.clampFloat (f.Ipc / 4.0) 0.0 1.0                                 // Option A.2: Hardware PMU IPC
        dst.[9] <- FuzzyMath.clampFloat f.CacheMissRate 0.0 1.0                               // Option A.2: Hardware PMU LLC miss rate

    member _.Evaluate(cohortId: int, f: inref<WindowFeatures>, logicalCores: int, physicalCores: int) : Strategia =
        let mutable st = _states.[cohortId]
        if not st.Initialized then
            initCohort cohortId
            st.Initialized <- true
            st.LastArm <- 0
            st.TotalUpdates <- 0
            st.EmaThroughput <- max 1e-6 f.Throughput

        // 1. Construct current 10-dimensional normalized Option A context vector x_t.
        fillContextVector &f _curX

        let cohortMatBase = cohortId * ArmCount * Dim * Dim
        let cohortBBase = cohortId * ArmCount * Dim

        // 2. Online Ridge Regression RLS update for previously executed arm (Sherman-Morrison formula).
        //    Causal attribution: omens_t were executed by st.LastArm under the exact workload context x_t.
        //    Baseline-centered log-advantage reward is zero-mean and 100% scale-invariant across workloads.
        if st.TotalUpdates > 0 then
            let baseline = max 1e-6 st.EmaThroughput
            let logAdv = FuzzyMath.clampFloat (Math.Log((max 1e-6 f.Throughput) / baseline)) -2.0 2.0
            let reward =
                logAdv
                - 0.35 * f.BarrierWaitRatio
                - 0.15 * f.ContentionRate

            st.EmaThroughput <- 0.85 * baseline + 0.15 * (max 1e-6 f.Throughput)

            let prev = st.LastArm
            let matOff = cohortMatBase + prev * Dim * Dim
            let bOff = cohortBBase + prev * Dim

            // Stabilized exponential forgetting (gamma = 0.97) adapts to non-stationary workload shifts
            // while capping trace(A^{-1}) <= 4*D to guarantee lifelong numerical positive-definiteness.
            let mutable trace = 0.0
            for i = 0 to Dim - 1 do
                trace <- trace + _aInv.[matOff + i * Dim + i]

            let gamma = 0.97
            if trace < 4.0 * float Dim then
                let invGamma = 1.0 / gamma
                for k = 0 to Dim * Dim - 1 do
                    _aInv.[matOff + k] <- _aInv.[matOff + k] * invGamma
                for i = 0 to Dim - 1 do
                    _b.[bOff + i] <- _b.[bOff + i] * gamma

            // Compute v = A^{-1} * x_t and denom = 1 + x_t^T * v.
            let mutable xTv = 0.0
            for i = 0 to Dim - 1 do
                let rowOff = matOff + i * Dim
                let mutable acc = 0.0
                for j = 0 to Dim - 1 do
                    acc <- acc + _aInv.[rowOff + j] * _curX.[j]
                _tmpV.[i] <- acc
                xTv <- xTv + _curX.[i] * acc

            let denom = 1.0 + max 0.0 xTv
            if denom > 1e-9 then
                let invDenom = 1.0 / denom
                for i = 0 to Dim - 1 do
                    let vi = _tmpV.[i]
                    let rowOff = matOff + i * Dim
                    for j = i to Dim - 1 do
                        let updated = _aInv.[rowOff + j] - (vi * _tmpV.[j]) * invDenom
                        let safeVal = if i = j then max 1e-6 updated else updated
                        _aInv.[rowOff + j] <- safeVal
                        _aInv.[matOff + j * Dim + i] <- safeVal

                for i = 0 to Dim - 1 do
                    _b.[bOff + i] <- _b.[bOff + i] + reward * _curX.[i]

        // 3. Action selection via LinUCB: argmax_a (theta_a^T * x_t + alpha * sqrt(x_t^T * A_a^{-1} * x_t)).
        let mutable chosenArm = 0
        let mutable chosenSigma = 1.0

        if st.TotalUpdates < ArmCount then
            // Cold-start exploration: observe every Option B profile once to seed Ridge models.
            chosenArm <- st.TotalUpdates
            chosenSigma <- 0.85
        else
            let alphaUcb = 0.22
            let mutable bestScore = Double.NegativeInfinity

            for a = 0 to ArmCount - 1 do
                let matOff = cohortMatBase + a * Dim * Dim
                let bOff = cohortBBase + a * Dim

                let mutable predMean = 0.0
                let mutable quadVar = 0.0

                for i = 0 to Dim - 1 do
                    let rowOff = matOff + i * Dim
                    let mutable thetaI = 0.0
                    let mutable aInvXI = 0.0
                    for j = 0 to Dim - 1 do
                        let aij = _aInv.[rowOff + j]
                        thetaI <- thetaI + aij * _b.[bOff + j]
                        aInvXI <- aInvXI + aij * _curX.[j]
                    _theta.[i] <- thetaI
                    predMean <- predMean + thetaI * _curX.[i]
                    quadVar <- quadVar + _curX.[i] * aInvXI

                let sigma = Math.Sqrt(max 0.0 quadVar)
                let ucbScore = predMean + alphaUcb * sigma
                if ucbScore > bestScore then
                    bestScore <- ucbScore
                    chosenArm <- a
                    chosenSigma <- sigma

        st.LastArm <- chosenArm
        st.TotalUpdates <- st.TotalUpdates + 1
        _states.[cohortId] <- st

        // 4. Decode chosen Option B strategy profile + continuous context batch projection.
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

        // Scale spin budget down if external CPU contention is active.
        let rawSpin = float Spin.[chosenArm] * FuzzyMath.clampFloat (1.0 - f.ExternalCpuLoad * 0.85) 0.15 1.0
        let spinBudget = FuzzyMath.safeToInt rawSpin 64 4096

        // Confidence directly reflects the Ridge Regression posterior ellipsoid width.
        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.55 * chosenSigma) 0.15 0.99)

        Strategia(
            BatchSize = batch,
            ThreadCount = armThreads,
            Confidence = confidence,
            AffinityHint = Affinity.[chosenArm],
            SpinIterationsBeforePark = spinBudget,
            PartitionMode = Partition.[chosenArm],
            StealMode = Steal.[chosenArm]
        )
