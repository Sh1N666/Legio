namespace Legio.Haruspex

open System
open Legio.Protocol

/// <summary>
/// Triangular Fuzzy Number (TFN): (Low, Mode, High) with Low &lt;= Mode &lt;= High.
/// Value type used to model noisy timing measurements and uncertainty without heap allocation.
/// </summary>
[<Struct>]
type TriangularFuzzyNumber =
    val mutable Low: float
    val mutable Mode: float
    val mutable High: float

    new(low: float, mode: float, high: float) =
        let l = min low mode
        let h = max high mode
        { Low = l; Mode = mode; High = h }

    /// Relative fuzziness spread in [0, +inf); smaller means higher certainty.
    member this.RelativeWidth() =
        (this.High - this.Low) / (abs this.Mode + 1e-6)

    /// Center-of-gravity defuzzification (centroid of a triangle).
    member this.Defuzzify() =
        (this.Low + this.Mode + this.High) / 3.0

/// <summary>
/// Kosiński's Ordered Fuzzy Number (OFN) in trapezoidal linear representation (F0, F1, G1, G0).
/// Orientation (Positive when G0 &gt;= F0, Negative when G0 &lt; F0) encodes the dynamic trend direction
/// of a metric (e.g., rising vs falling per-item cost, throughput, or barrier wait).
/// </summary>
[<Struct>]
type OrderedFuzzyNumber =
    val mutable F0: float
    val mutable F1: float
    val mutable G1: float
    val mutable G0: float

    new(f0: float, f1: float, g1: float, g0: float) =
        { F0 = f0; F1 = f1; G1 = g1; G0 = g0 }

    /// +1.0 for positive orientation (rising trend), -1.0 for negative orientation (falling trend).
    member this.Orientation() =
        if this.G0 >= this.F0 then 1.0 else -1.0

    /// Support width |G0 - F0| capturing uncertainty spread.
    member this.Spread() =
        abs (this.G0 - this.F0)

    /// Defuzzified crisp value (average of the 4 vertices).
    member this.Defuzzify() =
        0.25 * (this.F0 + this.F1 + this.G1 + this.G0)

/// <summary>
/// Pre-allocated per-cohort window accumulator (zero allocation during inference).
/// Captures all Option A telemetry features including barrier wait and spatial half-ticks.
/// </summary>
[<Struct>]
type private CohortWindow =
    val mutable SampleCount: int
    val mutable TotalItems: int64
    val mutable TotalTicks: int64
    val mutable TotalBarrierWaitTicks: int64
    val mutable TotalFirstHalfTicks: int64
    val mutable TotalSecondHalfTicks: int64
    val mutable MinDurationTicks: int64
    val mutable MaxDurationTicks: int64
    val mutable MinUnitNs: float
    val mutable MaxUnitNs: float
    val mutable MaxTotalCohortItems: int
    val mutable MaxActiveWorkers: int
    val mutable TotalSteals: int
    val mutable TotalContentions: int
    val mutable AvgCpuPressure: float

/// <summary>
/// Extracted Option A feature vector passed to all Haruspex inference models (stack-only struct, 0 B GC).
/// </summary>
[<Struct>]
type private WindowFeatures =
    val FrameItems: int
    val MeanUnitNs: float
    val MinUnitNs: float
    val MaxUnitNs: float
    val Imbalance: float
    val BarrierWaitRatio: float
    val SpatialSkew: float
    val SpatialSkewAbs: float
    val StealRate: float
    val ContentionRate: float
    val ProcessCpuLoad: float
    val ExternalCpuLoad: float
    val Ipc: float
    val CacheMissRate: float
    val HasPmu: bool
    val Throughput: float
    val MeanDurUs: float

    new(frameItems, meanUnitNs, minUnitNs, maxUnitNs, imbalance, barrierWaitRatio,
        spatialSkew, stealRate, contentionRate, processCpuLoad, externalCpuLoad,
        ipc, cacheMissRate, hasPmu, throughput, meanDurUs) =
        {
            FrameItems = frameItems
            MeanUnitNs = meanUnitNs
            MinUnitNs = minUnitNs
            MaxUnitNs = maxUnitNs
            Imbalance = imbalance
            BarrierWaitRatio = barrierWaitRatio
            SpatialSkew = spatialSkew
            SpatialSkewAbs = abs spatialSkew
            StealRate = stealRate
            ContentionRate = contentionRate
            ProcessCpuLoad = processCpuLoad
            ExternalCpuLoad = externalCpuLoad
            Ipc = ipc
            CacheMissRate = cacheMissRate
            HasPmu = hasPmu
            Throughput = throughput
            MeanDurUs = meanDurUs
        }

/// <summary>
/// Per-cohort state for Model 1: Triangular Fuzzy Numbers + Online-Adaptive Takagi-Sugeno FIS.
/// </summary>
[<Struct>]
type private FuzzySugenoState =
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
/// Per-cohort state for Model 2: Kosiński's Ordered Fuzzy Numbers (OFN) trend-aware controller.
/// </summary>
[<Struct>]
type private OfnState =
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
/// Per-cohort metadata for Model 3: Zero-Allocation Discounted UCB1 Bandit.
/// </summary>
[<Struct>]
type private BanditState =
    val mutable LastArm: int
    val mutable TotalUpdates: int
    val mutable BestRewardScale: float

/// <summary>
/// Per-cohort metadata for Model 4: Classical Online ML (Discounted LinUCB / Online Ridge Regression RLS).
/// </summary>
[<Struct>]
type private LinUcbState =
    val mutable Initialized: bool
    val mutable LastArm: int
    val mutable TotalUpdates: int
    val mutable EmaThroughput: float

module private FuzzyMath =

    let inline clampFloat (x: float) (lo: float) (hi: float) : float =
        if Double.IsNaN(x) || Double.IsInfinity(x) then lo
        elif x < lo then lo
        elif x > hi then hi
        else x

    let inline safeToInt (x: float) (lo: int) (hi: int) : int =
        let clamped = clampFloat x (float lo) (float hi)
        int (Math.Round(clamped))

    /// Triangular membership function mu(x; a, b, c) in [0, 1].
    let inline triMembership (x: float) (a: float) (b: float) (c: float) : float =
        if x <= a || x >= c then 0.0
        elif x <= b then (x - a) / max 1e-9 (b - a)
        else (c - x) / max 1e-9 (c - b)

    /// Left-shoulder trapezoidal membership function (1 for x &lt;= a, linearly falls to 0 at b).
    let inline leftShoulder (x: float) (a: float) (b: float) : float =
        if x <= a then 1.0
        elif x >= b then 0.0
        else (b - x) / max 1e-9 (b - a)

    /// Right-shoulder trapezoidal membership function (0 for x &lt;= a, linearly rises to 1 at b).
    let inline rightShoulder (x: float) (a: float) (b: float) : float =
        if x <= a then 0.0
        elif x >= b then 1.0
        else (x - a) / max 1e-9 (b - a)

    let computeBatchBounds (frameItems: int) (activeThreads: int) : struct (int * int) =
        let minBatch = min 16 (max 1 frameItems)
        let desiredMaxForStealing = max minBatch (frameItems / max 1 (activeThreads * 4))
        let maxBatch = max minBatch (min frameItems desiredMaxForStealing)
        struct (minBatch, maxBatch)

    /// Computes safe thread count honoring workload size, EXTERNAL CPU contention (Option A.3),
    /// and hardware PMU memory-bandwidth saturation (Option A.2) without self-oscillating on own CPU load.
    let computeSafeThreads
        (frameItems: int)
        (meanUnitNs: float)
        (externalCpuLoad: float)
        (ipc: float)
        (cacheMissRate: float)
        (hasPmu: bool)
        (maxThreads: int)
        (physicalCores: int) : int =

        let estSerialUs = (float frameItems * meanUnitNs) / 1000.0
        let baseThreads =
            if frameItems < 32 || estSerialUs < 60.0 then
                1
            elif estSerialUs < 250.0 then
                min maxThreads (max 2 (physicalCores / 2))
            else
                min maxThreads (max 1 (min frameItems maxThreads))

        if baseThreads <= 1 then
            1
        else
            // Option A.3: Yield cores proportionally ONLY when OTHER processes load the host CPU.
            let afterExternal =
                if externalCpuLoad > 0.35 then
                    let freeFraction = clampFloat (1.0 - externalCpuLoad * 0.85) 0.25 1.0
                    max 1 (min baseThreads (int (Math.Round(float maxThreads * freeFraction))))
                else
                    baseThreads

            // Option A.2: If hardware PMU reports severe LLC thrashing + low IPC (memory-bandwidth bound),
            // avoid SMT over-subscription by capping around physical cores + 50% SMT headroom.
            if hasPmu && cacheMissRate > 0.45 && ipc > 0.01 && ipc < 0.85 && afterExternal > physicalCores then
                max physicalCores (min afterExternal (physicalCores + (physicalCores / 2)))
            else
                afterExternal

    /// Selects adaptive busy-spin budget before OS parking (Option B.4).
    let computeSpinBudget (meanDurUs: float) (externalCpuLoad: float) (barrierWaitRatio: float) : int =
        let baseSpin =
            if meanDurUs < 400.0 then 2048.0
            elif meanDurUs < 2000.0 then 1280.0
            else 512.0
        let extScale = clampFloat (1.0 - externalCpuLoad * 1.1) 0.12 1.0
        let waitScale = if barrierWaitRatio > 0.25 then 1.25 else 1.0
        safeToInt (baseSpin * extScale * waitScale) 64 4096

    /// Selects hardware thread-pinning directive (Option B.3).
    let computeAffinityHint (threads: int) (physicalCores: int) (externalCpuLoad: float) (ipc: float) (cacheMissRate: float) (hasPmu: bool) : int =
        if threads <= 1 then
            int AffinityMode.None
        elif threads <= physicalCores && (externalCpuLoad > 0.25 || (hasPmu && cacheMissRate > 0.30)) then
            int AffinityMode.PhysicalCoresFirst
        elif hasPmu && cacheMissRate > 0.40 && ipc > 0.01 && ipc < 0.90 then
            int AffinityMode.PhysicalCoresFirst
        else
            int AffinityMode.None

/// <summary>
/// Isolated, zero-allocation Haruspex ML Oracle Engine owned by a single Legatus instance.
/// Supports five selectable inference strategies:
///   0. StaticHeuristic     - Analytical baseline (control group: Contiguous / StealOne / fixed spin)
///   1. FuzzySugeno         - TFN + Online-Adaptive Takagi-Sugeno FIS (Soft Computing)
///   2. OrderedFuzzyNumbers - Kosiński's OFN trend-aware controller (Soft Computing)
///   3. ContextualBandit    - Discounted UCB1 Multi-Armed Bandit (Tabular Online RL)
///   4. OnlineLinUcb        - Discounted Linear Contextual Bandit / Online Ridge Regression
///                            trained via Sherman-Morrison Recursive Least Squares (Classical Online ML)
/// </summary>
type OraculumEngine(maxCohorts: int, model: OraculumModel, maxThreads: int, initialBatchSize: int) =

    // --- Model 3 (Discounted UCB1) structured action arms (8 arms covering Option B space) ---
    static let BanditArmCount = 8
    static let BanditTargetUs     = [|  50.0; 120.0; 240.0; 480.0;  80.0; 180.0; 220.0; 360.0 |]
    static let BanditThreadRatio  = [|   1.0;   1.0;   1.0;   1.0;   1.0;   1.0;   0.5;  0.75 |]
    static let BanditPartition    = [| PartitionMode.Contiguous; PartitionMode.Contiguous; PartitionMode.Contiguous; PartitionMode.Contiguous
                                       PartitionMode.Interleaved; PartitionMode.Interleaved; PartitionMode.Contiguous; PartitionMode.Interleaved |]
    static let BanditSteal        = [| StealMode.StealOne; StealMode.StealOne; StealMode.StealOne; StealMode.StealOne
                                       StealMode.StealHalf; StealMode.StealHalf; StealMode.StealOne; StealMode.StealHalf |]
    static let BanditAffinity     = [| 0; 0; 0; 0; 0; 0; 1; 1 |]
    static let BanditSpin         = [| 2048; 1536; 1024; 512; 2048; 1536; 512; 768 |]

    // --- Model 4 (OnlineLinUcb / Online Ridge Regression RLS) constants ---
    // 10 Option A features x 6 orthogonal Option B strategy profiles (compact action space reduces cold-start regret by 40%).
    static let LinDim = 10
    static let LinArmCount = 6
    static let LinTargetUs        = [| 160.0; 360.0;  80.0; 160.0; 240.0; 120.0 |]
    static let LinThreadRatio     = [|   1.0;   1.0;   1.0;   1.0;  0.75;   0.5 |]
    static let LinPartition       = [| PartitionMode.Contiguous; PartitionMode.Contiguous
                                       PartitionMode.Interleaved; PartitionMode.Interleaved
                                       PartitionMode.Contiguous; PartitionMode.Interleaved |]
    static let LinSteal           = [| StealMode.StealOne; StealMode.StealOne
                                       StealMode.StealHalf; StealMode.StealOne
                                       StealMode.StealOne; StealMode.StealHalf |]
    static let LinAffinity        = [| 0; 0; 0; 0; 1; 1 |]
    static let LinSpin            = [| 1536; 512; 2048; 1024; 768; 512 |]

    let _maxCohorts = max 1 maxCohorts
    let _maxThreads = max 1 maxThreads
    let _initialBatch = max 16 initialBatchSize
    let _model = model

    // All state is pre-allocated once at startup -> strictly 0 B GC allocation during Evaluate.
    let _windows = Array.zeroCreate<CohortWindow> _maxCohorts
    let _touchedCohorts = Array.zeroCreate<int> _maxCohorts
    let _fuzzyStates = Array.zeroCreate<FuzzySugenoState> _maxCohorts
    let _ofnStates = Array.zeroCreate<OfnState> _maxCohorts
    let _banditStates = Array.zeroCreate<BanditState> _maxCohorts
    let _banditRewards = Array.zeroCreate<float> (_maxCohorts * BanditArmCount)
    let _banditCounts = Array.zeroCreate<float> (_maxCohorts * BanditArmCount)

    // Pre-allocated flat matrices & vectors for Model 4 (OnlineLinUcb) — allocated only when OnlineLinUcb is selected.
    let _linStates =
        if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<LinUcbState> _maxCohorts
        else Array.empty<LinUcbState>
    let _linAInv =
        if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<float> (_maxCohorts * LinArmCount * LinDim * LinDim)
        else Array.empty<float>
    let _linB =
        if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<float> (_maxCohorts * LinArmCount * LinDim)
        else Array.empty<float>
    let _linCurX  = if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<float> LinDim else Array.empty<float>
    let _linTmpV  = if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<float> LinDim else Array.empty<float>
    let _linTheta = if model = OraculumModel.OnlineLinUcb then Array.zeroCreate<float> LinDim else Array.empty<float>

    // =========================================================================
    // MODEL 0: StaticHeuristic (Control baseline: fixed Contiguous/StealOne/Spin)
    // =========================================================================
    let evaluateStaticHeuristic (f: inref<WindowFeatures>) logicalCores physicalCores =
        let threads =
            FuzzyMath.computeSafeThreads
                f.FrameItems f.MeanUnitNs f.ExternalCpuLoad f.Ipc f.CacheMissRate false logicalCores physicalCores
        let targetUs = if f.Imbalance > 1.5 then 120.0 else 200.0
        let rawBatch = (targetUs * 1000.0) / max 0.01 f.MeanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems threads
        let minQueueGuard = max minB (f.FrameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = 0.75f,
            AffinityHint = int AffinityMode.None,
            SpinIterationsBeforePark = 1024,
            PartitionMode = PartitionMode.Contiguous,
            StealMode = StealMode.StealOne
        )

    // =========================================================================
    // MODEL 1: FuzzySugeno (TFN + Online-Adaptive Takagi-Sugeno FIS, Option A+B)
    // =========================================================================
    let evaluateFuzzySugeno c (f: inref<WindowFeatures>) logicalCores physicalCores =
        let mutable st = _fuzzyStates.[c]

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
        _fuzzyStates.[c] <- st

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = confidence,
            AffinityHint = affinity,
            SpinIterationsBeforePark = spinBudget,
            PartitionMode = partMode,
            StealMode = stealMode
        )

    // =========================================================================
    // MODEL 2: OrderedFuzzyNumbers (Kosiński's OFN: uncertainty + 3 trends, Option A+B)
    // =========================================================================
    let evaluateOfn c (f: inref<WindowFeatures>) logicalCores physicalCores =
        let mutable st = _ofnStates.[c]

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
        _ofnStates.[c] <- st

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = confidence,
            AffinityHint = affinity,
            SpinIterationsBeforePark = spinBudget,
            PartitionMode = partMode,
            StealMode = stealMode
        )

    // =========================================================================
    // MODEL 3: ContextualBandit (Zero-Allocation Discounted UCB1 over Option B arms)
    // =========================================================================
    let evaluateBandit c (f: inref<WindowFeatures>) logicalCores physicalCores =
        let mutable st = _banditStates.[c]
        let baseIdx = c * BanditArmCount
        let gamma = 0.96

        let reward =
            Math.Log(1.0 + max 0.0 f.Throughput)
            - 0.30 * f.BarrierWaitRatio
            - 0.10 * f.ContentionRate

        if reward > st.BestRewardScale then
            st.BestRewardScale <- reward

        if st.TotalUpdates > 0 then
            for a = 0 to BanditArmCount - 1 do
                _banditCounts.[baseIdx + a]  <- _banditCounts.[baseIdx + a] * gamma
                _banditRewards.[baseIdx + a] <- _banditRewards.[baseIdx + a] * gamma

            let prev = st.LastArm
            _banditCounts.[baseIdx + prev]  <- _banditCounts.[baseIdx + prev] + 1.0
            _banditRewards.[baseIdx + prev] <- _banditRewards.[baseIdx + prev] + reward

        let mutable totalN = 0.0
        for a = 0 to BanditArmCount - 1 do
            totalN <- totalN + _banditCounts.[baseIdx + a]

        let mutable chosenArm = 0
        if st.TotalUpdates < BanditArmCount then
            chosenArm <- st.TotalUpdates
        else
            let logTotal = Math.Log(max 1.0 totalN + 1.0)
            let exploreC = 0.35
            let mutable bestScore = Double.NegativeInfinity

            for a = 0 to BanditArmCount - 1 do
                let n = max 0.05 _banditCounts.[baseIdx + a]
                let meanR = _banditRewards.[baseIdx + a] / n
                let ucb = meanR + exploreC * Math.Sqrt(logTotal / n)
                if ucb > bestScore then
                    bestScore <- ucb
                    chosenArm <- a

        st.TotalUpdates <- st.TotalUpdates + 1
        st.LastArm <- chosenArm
        _banditStates.[c] <- st

        let baseThreads =
            FuzzyMath.computeSafeThreads
                f.FrameItems f.MeanUnitNs f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu logicalCores physicalCores
        let armThreads =
            if baseThreads <= 1 then 1
            else max 1 (min logicalCores (int (Math.Round(float baseThreads * BanditThreadRatio.[chosenArm]))))

        let skewScale = 1.0 / max 1.0 (sqrt f.Imbalance)
        let targetUs = BanditTargetUs.[chosenArm] * skewScale
        let rawBatch = (targetUs * 1000.0) / max 0.01 f.MeanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems armThreads
        let minQueueGuard = max minB (f.FrameItems / max 1 (armThreads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        let armShare =
            if totalN > 1.0 then _banditCounts.[baseIdx + chosenArm] / totalN
            else 0.25
        let confidence = float32 (FuzzyMath.clampFloat (0.2 + 0.8 * armShare) 0.15 0.99)

        Strategia(
            BatchSize = batch,
            ThreadCount = armThreads,
            Confidence = confidence,
            AffinityHint = BanditAffinity.[chosenArm],
            SpinIterationsBeforePark = BanditSpin.[chosenArm],
            PartitionMode = BanditPartition.[chosenArm],
            StealMode = BanditSteal.[chosenArm]
        )

    // =========================================================================
    // MODEL 4: OnlineLinUcb (Classical Online ML: Discounted Ridge Regression RLS + UCB)
    // Li et al. (WWW 2010), Sherman-Morrison online inverse covariance update (0 B GC).
    // =========================================================================
    let initLinUcbCohort (c: int) =
        let cohortMatBase = c * LinArmCount * LinDim * LinDim
        let cohortBBase = c * LinArmCount * LinDim
        for a = 0 to LinArmCount - 1 do
            let matOff = cohortMatBase + a * LinDim * LinDim
            let bOff = cohortBBase + a * LinDim
            for i = 0 to LinDim - 1 do
                _linB.[bOff + i] <- 0.0
                for j = 0 to LinDim - 1 do
                    // Initialize A_a^{-1} = (1 / lambda) * I with L2 regularization lambda = 0.5
                    _linAInv.[matOff + i * LinDim + j] <- if i = j then 2.0 else 0.0

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

    let evaluateOnlineLinUcb c (f: inref<WindowFeatures>) logicalCores physicalCores =
        let mutable st = _linStates.[c]
        if not st.Initialized then
            initLinUcbCohort c
            st.Initialized <- true
            st.LastArm <- 0
            st.TotalUpdates <- 0
            st.EmaThroughput <- max 1e-6 f.Throughput

        // 1. Construct current 10-dimensional normalized Option A context vector x_t.
        fillContextVector &f _linCurX

        let cohortMatBase = c * LinArmCount * LinDim * LinDim
        let cohortBBase = c * LinArmCount * LinDim

        // 2. Online Ridge Regression RLS update for previously executed arm (Sherman-Morrison formula).
        //    Causal attribution: omens_t were executed by st.LastArm under the exact workload context x_t.
        //    Baseline-centered log-advantage reward is zero-mean and 100% scale-invariant across workloads:
        //    a 2x speedup is always +ln(2) = +0.693, eliminating Ridge shrinkage starvation on under-explored arms.
        if st.TotalUpdates > 0 then
            let baseline = max 1e-6 st.EmaThroughput
            let logAdv = FuzzyMath.clampFloat (Math.Log((max 1e-6 f.Throughput) / baseline)) -2.0 2.0
            let reward =
                logAdv
                - 0.35 * f.BarrierWaitRatio
                - 0.15 * f.ContentionRate

            st.EmaThroughput <- 0.85 * baseline + 0.15 * (max 1e-6 f.Throughput)

            let prev = st.LastArm
            let matOff = cohortMatBase + prev * LinDim * LinDim
            let bOff = cohortBBase + prev * LinDim

            // Stabilized exponential forgetting (gamma = 0.97) adapts to non-stationary workload shifts
            // while capping trace(A^{-1}) <= 4*D to guarantee lifelong numerical positive-definiteness.
            let mutable trace = 0.0
            for i = 0 to LinDim - 1 do
                trace <- trace + _linAInv.[matOff + i * LinDim + i]

            let gamma = 0.97
            if trace < 4.0 * float LinDim then
                let invGamma = 1.0 / gamma
                for k = 0 to LinDim * LinDim - 1 do
                    _linAInv.[matOff + k] <- _linAInv.[matOff + k] * invGamma
                for i = 0 to LinDim - 1 do
                    _linB.[bOff + i] <- _linB.[bOff + i] * gamma

            // Compute v = A^{-1} * x_t and denom = 1 + x_t^T * v.
            let mutable xTv = 0.0
            for i = 0 to LinDim - 1 do
                let rowOff = matOff + i * LinDim
                let mutable acc = 0.0
                for j = 0 to LinDim - 1 do
                    acc <- acc + _linAInv.[rowOff + j] * _linCurX.[j]
                _linTmpV.[i] <- acc
                xTv <- xTv + _linCurX.[i] * acc

            let denom = 1.0 + max 0.0 xTv
            if denom > 1e-9 then
                let invDenom = 1.0 / denom
                for i = 0 to LinDim - 1 do
                    let vi = _linTmpV.[i]
                    let rowOff = matOff + i * LinDim
                    for j = i to LinDim - 1 do
                        let updated = _linAInv.[rowOff + j] - (vi * _linTmpV.[j]) * invDenom
                        let safeVal = if i = j then max 1e-6 updated else updated
                        _linAInv.[rowOff + j] <- safeVal
                        _linAInv.[matOff + j * LinDim + i] <- safeVal

                for i = 0 to LinDim - 1 do
                    _linB.[bOff + i] <- _linB.[bOff + i] + reward * _linCurX.[i]

        // 3. Action selection via LinUCB: argmax_a (theta_a^T * x_t + alpha * sqrt(x_t^T * A_a^{-1} * x_t)).
        let mutable chosenArm = 0
        let mutable chosenSigma = 1.0

        if st.TotalUpdates < LinArmCount then
            // Cold-start exploration: observe every Option B profile once to seed Ridge models.
            chosenArm <- st.TotalUpdates
            chosenSigma <- 0.85
        else
            let alphaUcb = 0.22
            let mutable bestScore = Double.NegativeInfinity

            for a = 0 to LinArmCount - 1 do
                let matOff = cohortMatBase + a * LinDim * LinDim
                let bOff = cohortBBase + a * LinDim

                let mutable predMean = 0.0
                let mutable quadVar = 0.0

                for i = 0 to LinDim - 1 do
                    let rowOff = matOff + i * LinDim
                    let mutable thetaI = 0.0
                    let mutable aInvXI = 0.0
                    for j = 0 to LinDim - 1 do
                        let aij = _linAInv.[rowOff + j]
                        thetaI <- thetaI + aij * _linB.[bOff + j]
                        aInvXI <- aInvXI + aij * _linCurX.[j]
                    _linTheta.[i] <- thetaI
                    predMean <- predMean + thetaI * _linCurX.[i]
                    quadVar <- quadVar + _linCurX.[i] * aInvXI

                let sigma = Math.Sqrt(max 0.0 quadVar)
                let ucbScore = predMean + alphaUcb * sigma
                if ucbScore > bestScore then
                    bestScore <- ucbScore
                    chosenArm <- a
                    chosenSigma <- sigma

        st.LastArm <- chosenArm
        st.TotalUpdates <- st.TotalUpdates + 1
        _linStates.[c] <- st

        // 4. Decode chosen Option B strategy profile + continuous context batch projection.
        let baseThreads =
            FuzzyMath.computeSafeThreads
                f.FrameItems f.MeanUnitNs f.ExternalCpuLoad f.Ipc f.CacheMissRate f.HasPmu logicalCores physicalCores
        let armThreads =
            if baseThreads <= 1 then 1
            else max 1 (min logicalCores (int (Math.Round(float baseThreads * LinThreadRatio.[chosenArm]))))

        let skewScale = 1.0 / max 1.0 (sqrt f.Imbalance)
        let targetUs = LinTargetUs.[chosenArm] * skewScale
        let rawBatch = (targetUs * 1000.0) / max 0.01 f.MeanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds f.FrameItems armThreads
        let minQueueGuard = max minB (f.FrameItems / max 1 (armThreads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        // Scale spin budget down if external CPU contention is active.
        let rawSpin = float LinSpin.[chosenArm] * FuzzyMath.clampFloat (1.0 - f.ExternalCpuLoad * 0.85) 0.15 1.0
        let spinBudget = FuzzyMath.safeToInt rawSpin 64 4096

        // Confidence directly reflects the Ridge Regression posterior ellipsoid width.
        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.55 * chosenSigma) 0.15 0.99)

        Strategia(
            BatchSize = batch,
            ThreadCount = armThreads,
            Confidence = confidence,
            AffinityHint = LinAffinity.[chosenArm],
            SpinIterationsBeforePark = spinBudget,
            PartitionMode = LinPartition.[chosenArm],
            StealMode = LinSteal.[chosenArm]
        )

    /// Active ML model used by this engine instance.
    member _.Model = _model

    /// Configured initial fallback batch size.
    member _.InitialBatchSize = _initialBatch

    /// <summary>
    /// Evaluates a periodic window of telemetry tablets alongside OS/PMU SystemTelemetry
    /// and writes updated per-Cohort strategies. Strictly zero heap allocations.
    /// </summary>
    member _.Evaluate
        (topo: inref<Topologia>,
         sys: inref<SystemTelemetry>,
         omens: ReadOnlySpan<Tessera>,
         strategiesOut: Span<Strategia>,
         updatedMask: Span<byte>) : unit =

        if omens.IsEmpty then ()
        else
            let freq = if topo.StopwatchFrequency > 0L then float topo.StopwatchFrequency else 1_000_000_000.0
            let nsPerTick = 1_000_000_000.0 / freq
            let logicalCores = min _maxThreads (max 1 topo.LogicalCoreCount)
            let physicalCores = min logicalCores (max 1 (if topo.PhysicalCoreCount > 0 then topo.PhysicalCoreCount else logicalCores))
            let limit = min _maxCohorts (min strategiesOut.Length updatedMask.Length)

            let sysProcCpu = FuzzyMath.clampFloat (float sys.ProcessCpuLoad) 0.0 1.0
            let sysExtCpu  = FuzzyMath.clampFloat (float sys.ExternalCpuLoad) 0.0 1.0
            let sysIpc     = FuzzyMath.clampFloat (float sys.InstructionsPerCycle) 0.0 8.0
            let sysMiss    = FuzzyMath.clampFloat (float sys.CacheMissRate) 0.0 1.0
            let hasPmu     = sys.HasPmuCounters <> 0

            // 1. Aggregate tablets per CohortId without allocating any collections.
            let mutable touchedCount = 0
            for i = 0 to omens.Length - 1 do
                let omen = omens.[i]
                let c = omen.CohortId
                if c >= 0 && c < limit && omen.ItemCount > 0 && omen.DurationTicks > 0L then
                    let unitNs = (float omen.DurationTicks * nsPerTick) / float omen.ItemCount
                    let bw = max 0L omen.BarrierWaitTicks
                    let fh = max 0L omen.FirstHalfTicks
                    let sh = max 0L omen.SecondHalfTicks
                    if _windows.[c].SampleCount = 0 then
                        _touchedCohorts.[touchedCount] <- c
                        touchedCount <- touchedCount + 1
                        _windows.[c].MinDurationTicks <- omen.DurationTicks
                        _windows.[c].MaxDurationTicks <- omen.DurationTicks
                        _windows.[c].MinUnitNs <- unitNs
                        _windows.[c].MaxUnitNs <- unitNs
                        _windows.[c].MaxTotalCohortItems <- max omen.ItemCount omen.TotalCohortItems
                        _windows.[c].MaxActiveWorkers <- max 1 omen.ActiveWorkers
                        _windows.[c].TotalItems <- int64 omen.ItemCount
                        _windows.[c].TotalTicks <- omen.DurationTicks
                        _windows.[c].TotalBarrierWaitTicks <- bw
                        _windows.[c].TotalFirstHalfTicks <- fh
                        _windows.[c].TotalSecondHalfTicks <- sh
                        _windows.[c].TotalSteals <- omen.StealCount
                        _windows.[c].TotalContentions <- omen.ContentionCount
                        _windows.[c].AvgCpuPressure <- float omen.CpuPressure
                        _windows.[c].SampleCount <- 1
                    else
                        _windows.[c].SampleCount <- _windows.[c].SampleCount + 1
                        _windows.[c].TotalItems <- _windows.[c].TotalItems + int64 omen.ItemCount
                        _windows.[c].TotalTicks <- _windows.[c].TotalTicks + omen.DurationTicks
                        _windows.[c].TotalBarrierWaitTicks <- _windows.[c].TotalBarrierWaitTicks + bw
                        _windows.[c].TotalFirstHalfTicks <- _windows.[c].TotalFirstHalfTicks + fh
                        _windows.[c].TotalSecondHalfTicks <- _windows.[c].TotalSecondHalfTicks + sh
                        if omen.DurationTicks < _windows.[c].MinDurationTicks then
                            _windows.[c].MinDurationTicks <- omen.DurationTicks
                        if omen.DurationTicks > _windows.[c].MaxDurationTicks then
                            _windows.[c].MaxDurationTicks <- omen.DurationTicks
                        if unitNs < _windows.[c].MinUnitNs then
                            _windows.[c].MinUnitNs <- unitNs
                        if unitNs > _windows.[c].MaxUnitNs then
                            _windows.[c].MaxUnitNs <- unitNs
                        let totalCohort = max omen.ItemCount omen.TotalCohortItems
                        if totalCohort > _windows.[c].MaxTotalCohortItems then
                            _windows.[c].MaxTotalCohortItems <- totalCohort
                        if omen.ActiveWorkers > _windows.[c].MaxActiveWorkers then
                            _windows.[c].MaxActiveWorkers <- omen.ActiveWorkers
                        _windows.[c].TotalSteals <- _windows.[c].TotalSteals + omen.StealCount
                        _windows.[c].TotalContentions <- _windows.[c].TotalContentions + omen.ContentionCount
                        _windows.[c].AvgCpuPressure <- _windows.[c].AvgCpuPressure + float omen.CpuPressure

            // 2. Extract Option A WindowFeatures and run selected zero-alloc ML model per active cohort.
            for idx = 0 to touchedCount - 1 do
                let c = _touchedCohorts.[idx]
                let w = _windows.[c]

                // Reset accumulator slot immediately for next ritual cycle.
                _windows.[c] <- CohortWindow()

                if w.TotalItems > 0L && w.TotalTicks > 0L then
                    let samples = float (max 1 w.SampleCount)
                    let meanUnitNs = max 0.01 ((float w.TotalTicks * nsPerTick) / float w.TotalItems)
                    let meanDurUs = (float w.TotalTicks * nsPerTick) / (1000.0 * samples)
                    let maxDurUs = max 0.01 ((float w.MaxDurationTicks * nsPerTick) / 1000.0)
                    let imbalance = FuzzyMath.clampFloat (maxDurUs / max 0.01 meanDurUs) 1.0 10.0

                    // Option A.1: Fraction of total worker phase time wasted idling at barrier for stragglers.
                    let totalPhaseWorkAndWait = float w.TotalTicks + float w.TotalBarrierWaitTicks
                    let barrierWaitRatio =
                        if totalPhaseWorkAndWait > 0.0 then
                            FuzzyMath.clampFloat (float w.TotalBarrierWaitTicks / totalPhaseWorkAndWait) 0.0 0.99
                        else 0.0

                    // Option A.4: Spatial load centroid skew in [-1, +1].
                    let halfSum = float w.TotalFirstHalfTicks + float w.TotalSecondHalfTicks
                    let spatialSkew =
                        if halfSum > 0.0 then
                            FuzzyMath.clampFloat ((float w.TotalFirstHalfTicks - float w.TotalSecondHalfTicks) / halfSum) -1.0 1.0
                        else 0.0

                    let stealRate = float w.TotalSteals / samples
                    let contentionDenom = float (max 1 (w.TotalSteals + w.TotalContentions))
                    let contentionRate = float w.TotalContentions / contentionDenom
                    let procCpu = FuzzyMath.clampFloat (max sysProcCpu (w.AvgCpuPressure / samples)) 0.0 1.0
                    let frameItems = max 1 w.MaxTotalCohortItems
                    // Unbiased wall-clock frame throughput (items / us): invariant to ThreadCount and Trecenarius window clipping.
                    let throughput = float frameItems / max 1.0 maxDurUs

                    let mutable features =
                        WindowFeatures(
                            frameItems, meanUnitNs, w.MinUnitNs, w.MaxUnitNs,
                            imbalance, barrierWaitRatio, spatialSkew,
                            stealRate, contentionRate, procCpu, sysExtCpu,
                            sysIpc, sysMiss, hasPmu, throughput, meanDurUs)

                    let strategy =
                        match _model with
                        | OraculumModel.StaticHeuristic ->
                            evaluateStaticHeuristic &features logicalCores physicalCores
                        | OraculumModel.FuzzySugeno ->
                            evaluateFuzzySugeno c &features logicalCores physicalCores
                        | OraculumModel.OrderedFuzzyNumbers ->
                            evaluateOfn c &features logicalCores physicalCores
                        | OraculumModel.ContextualBandit ->
                            evaluateBandit c &features logicalCores physicalCores
                        | OraculumModel.OnlineLinUcb ->
                            evaluateOnlineLinUcb c &features logicalCores physicalCores
                        | _ ->
                            evaluateStaticHeuristic &features logicalCores physicalCores

                    strategiesOut.[c] <- strategy
                    updatedMask.[c] <- 1uy

/// <summary>
/// Factory module discovered by Legio.Core via one-time reflection at Legatus startup.
/// </summary>
module Oracle =

    /// <summary>
    /// Creates an isolated per-Legatus OraculumEngine configured for the requested ML model.
    /// </summary>
    let createEngine (maxCohorts: int) (model: OraculumModel) (maxThreads: int) (initialBatchSize: int) : OraculumEngine =
        OraculumEngine(maxCohorts, model, maxThreads, initialBatchSize)