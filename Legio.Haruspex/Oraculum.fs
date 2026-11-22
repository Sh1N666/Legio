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
/// of a metric (e.g., rising vs falling per-item cost or throughput).
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
/// </summary>
[<Struct>]
type private CohortWindow =
    val mutable SampleCount: int
    val mutable TotalItems: int64
    val mutable TotalTicks: int64
    val mutable MinDurationTicks: int64
    val mutable MaxDurationTicks: int64
    val mutable MinUnitNs: float
    val mutable MaxUnitNs: float
    val mutable MaxTotalCohortItems: int
    val mutable TotalSteals: int
    val mutable TotalContentions: int
    val mutable AvgCpuPressure: float

/// <summary>
/// Per-cohort state for Model 1: Triangular Fuzzy Numbers + Online-Adaptive Takagi-Sugeno FIS.
/// </summary>
[<Struct>]
type private FuzzySugenoState =
    val mutable Initialized: bool
    val mutable CostTfn: TriangularFuzzyNumber
    val mutable ImbalanceTfn: TriangularFuzzyNumber
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
    val mutable TargetUs: float
    val mutable LastUnitNs: float
    val mutable LastThroughput: float
    val mutable StepSign: float
    val mutable Updates: int

/// <summary>
/// Per-cohort metadata for Model 3: Zero-Allocation Discounted UCB Contextual Bandit.
/// </summary>
[<Struct>]
type private BanditState =
    val mutable LastArm: int
    val mutable TotalUpdates: int
    val mutable BestRewardScale: float

module private FuzzyMath =

    let inline clampFloat (x: float) (lo: float) (hi: float) : float =
        if Double.IsNaN(x) then lo
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

    let computeSafeThreads (frameItems: int) (meanUnitNs: float) (_cpuPressure: float) (maxThreads: int) (physicalCores: int) : int =
        let estSerialUs = (float frameItems * meanUnitNs) / 1000.0
        if frameItems < 32 || estSerialUs < 60.0 then
            1
        elif estSerialUs < 250.0 then
            min maxThreads (max 2 (physicalCores / 2))
        else
            min maxThreads (max 1 (min frameItems maxThreads))

/// <summary>
/// Isolated, zero-allocation Haruspex ML Oracle Engine owned by a single Legatus instance.
/// Supports four selectable inference strategies:
/// StaticHeuristic, FuzzySugeno (TFN + Adaptive Takagi-Sugeno), OrderedFuzzyNumbers (OFN), and ContextualBandit (D-UCB).
/// </summary>
type OraculumEngine(maxCohorts: int, model: OraculumModel, maxThreads: int, initialBatchSize: int) =

    static let BanditArmCount = 8
    // Context-aware arms: target batch duration (us) paired with thread-count scaling factor.
    static let BanditTargetUs    = [| 50.0; 100.0; 200.0; 400.0; 800.0; 120.0; 250.0; 500.0 |]
    static let BanditThreadRatio = [|  1.0;   1.0;   1.0;   1.0;   1.0;   0.5;   0.5;  0.75 |]

    let _maxCohorts = max 1 maxCohorts
    let _maxThreads = max 1 maxThreads
    let _initialBatch = max 16 initialBatchSize
    let _model = model

    // All state is pre-allocated once at startup -> 0 B GC allocation during Evaluate.
    let _windows = Array.zeroCreate<CohortWindow> _maxCohorts
    let _touchedCohorts = Array.zeroCreate<int> _maxCohorts
    let _fuzzyStates = Array.zeroCreate<FuzzySugenoState> _maxCohorts
    let _ofnStates = Array.zeroCreate<OfnState> _maxCohorts
    let _banditStates = Array.zeroCreate<BanditState> _maxCohorts
    let _banditRewards = Array.zeroCreate<float> (_maxCohorts * BanditArmCount)
    let _banditCounts = Array.zeroCreate<float> (_maxCohorts * BanditArmCount)

    // =========================================================================
    // MODEL 0: StaticHeuristic (Baseline analytical control)
    // =========================================================================
    let evaluateStaticHeuristic frameItems meanUnitNs imbalance pressure logicalCores physicalCores =
        let threads = FuzzyMath.computeSafeThreads frameItems meanUnitNs pressure logicalCores physicalCores
        let targetUs = if imbalance > 1.5 then 120.0 else 200.0
        let rawBatch = (targetUs * 1000.0) / max 0.01 meanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds frameItems threads
        let minQueueGuard = max minB (frameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = 0.75f,
            AffinityHint = if pressure > 0.85 then 1 else 0
        )

    // =========================================================================
    // MODEL 1: FuzzySugeno (Triangular Fuzzy Numbers + Adaptive Takagi-Sugeno)
    // =========================================================================
    let evaluateFuzzySugeno c frameItems meanUnitNs minUnitNs maxUnitNs imbalance contentionRate pressure throughput logicalCores physicalCores =
        let mutable st = _fuzzyStates.[c]

        if not st.Initialized then
            st.Initialized <- true
            st.CostTfn <- TriangularFuzzyNumber(minUnitNs, meanUnitNs, maxUnitNs)
            st.ImbalanceTfn <- TriangularFuzzyNumber(1.0, imbalance, max imbalance 1.25)
            st.TargetUsLight <- 140.0
            st.TargetUsMedium <- 240.0
            st.TargetUsHeavy <- 400.0
            st.SkewShrink <- 0.55
            st.EmaThroughput <- throughput
            st.LastTargetUs <- 200.0
            st.StepSign <- 1.0
            st.Updates <- 1
        else
            let alpha = 0.25
            let newMode = (1.0 - alpha) * st.CostTfn.Mode + alpha * meanUnitNs
            let newLow = min newMode ((1.0 - alpha) * st.CostTfn.Low + alpha * minUnitNs)
            let newHigh = max newMode ((1.0 - alpha) * st.CostTfn.High + alpha * maxUnitNs)
            st.CostTfn <- TriangularFuzzyNumber(newLow, newMode, newHigh)

            let imbMode = (1.0 - alpha) * st.ImbalanceTfn.Mode + alpha * imbalance
            let imbLow = min imbMode ((1.0 - alpha) * st.ImbalanceTfn.Low + alpha * 1.0)
            let imbHigh = max imbMode ((1.0 - alpha) * st.ImbalanceTfn.High + alpha * max imbalance imbMode)
            st.ImbalanceTfn <- TriangularFuzzyNumber(imbLow, imbMode, imbHigh)
            st.Updates <- st.Updates + 1

        let crispCost = max 0.01 (st.CostTfn.Defuzzify())
        let logCost = Math.Log10(crispCost + 1.0)
        let muLight  = FuzzyMath.leftShoulder  logCost 1.0 2.0
        let muMedium = FuzzyMath.triMembership logCost 1.0 2.2 3.4
        let muHeavy  = FuzzyMath.rightShoulder logCost 2.4 3.6

        let crispImb = st.ImbalanceTfn.Defuzzify()
        let muSkewed = FuzzyMath.rightShoulder crispImb 1.2 2.2
        let muHighContention = FuzzyMath.rightShoulder contentionRate 0.25 0.65

        if st.Updates > 1 then
            let rewardDelta = (throughput - st.EmaThroughput) / (st.EmaThroughput + 1e-6)
            if rewardDelta < -0.03 then
                st.StepSign <- -st.StepSign

            let uncertainty = FuzzyMath.clampFloat (st.CostTfn.RelativeWidth()) 0.05 1.0
            let lr = 18.0 * uncertainty * st.StepSign

            st.TargetUsLight  <- FuzzyMath.clampFloat (st.TargetUsLight  + lr * muLight)  40.0 600.0
            st.TargetUsMedium <- FuzzyMath.clampFloat (st.TargetUsMedium + lr * muMedium) 60.0 900.0
            st.TargetUsHeavy  <- FuzzyMath.clampFloat (st.TargetUsHeavy  + lr * muHeavy)  80.0 1600.0

            if muSkewed > 0.3 then
                let skewAdj = if rewardDelta >= 0.0 then -0.02 else 0.02
                st.SkewShrink <- FuzzyMath.clampFloat (st.SkewShrink + skewAdj * muSkewed) 0.25 0.85

            st.EmaThroughput <- 0.8 * st.EmaThroughput + 0.2 * throughput

        let wSum = muLight + muMedium + muHeavy + 1e-9
        let baseTargetUs =
            (muLight * st.TargetUsLight + muMedium * st.TargetUsMedium + muHeavy * st.TargetUsHeavy) / wSum

        let skewMultiplier = 1.0 - muSkewed * (1.0 - st.SkewShrink)
        let contentionMultiplier = 1.0 + muHighContention * 0.65
        let targetUs = FuzzyMath.clampFloat (baseTargetUs * skewMultiplier * contentionMultiplier) 25.0 2000.0
        st.LastTargetUs <- targetUs

        let threads = FuzzyMath.computeSafeThreads frameItems crispCost pressure logicalCores physicalCores

        let rawBatch = (targetUs * 1000.0) / crispCost
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds frameItems threads
        let skewCap =
            if muSkewed > 0.4 then max minB (frameItems / max 1 (threads * 6))
            else maxB
        let effMaxB = max minB (min maxB skewCap)
        let minQueueGuard = max minB (frameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB effMaxB

        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.5 * st.CostTfn.RelativeWidth()) 0.15 0.99)
        _fuzzyStates.[c] <- st

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = confidence,
            AffinityHint = if pressure > 0.85 then 1 else 0
        )

    // =========================================================================
    // MODEL 2: OrderedFuzzyNumbers (Kosiński's OFN: uncertainty + trend orientation)
    // =========================================================================
    let evaluateOfn c frameItems meanUnitNs minUnitNs maxUnitNs imbalance contentionRate pressure throughput logicalCores physicalCores =
        let mutable st = _ofnStates.[c]

        if not st.Initialized then
            st.Initialized <- true
            st.CostOfn <- OrderedFuzzyNumber(minUnitNs, meanUnitNs, meanUnitNs, maxUnitNs)
            st.ThroughputOfn <- OrderedFuzzyNumber(throughput * 0.9, throughput, throughput, throughput * 1.1)
            st.TargetUs <- 200.0
            st.LastUnitNs <- meanUnitNs
            st.LastThroughput <- throughput
            st.StepSign <- 1.0
            st.Updates <- 1
        else
            let alpha = 0.30
            let costRising = meanUnitNs >= st.LastUnitNs
            let struct (newF0, newF1, newG1, newG0) =
                if costRising then
                    struct (minUnitNs, st.LastUnitNs, meanUnitNs, maxUnitNs)
                else
                    struct (maxUnitNs, st.LastUnitNs, meanUnitNs, minUnitNs)

            st.CostOfn <- OrderedFuzzyNumber(
                (1.0 - alpha) * st.CostOfn.F0 + alpha * newF0,
                (1.0 - alpha) * st.CostOfn.F1 + alpha * newF1,
                (1.0 - alpha) * st.CostOfn.G1 + alpha * newG1,
                (1.0 - alpha) * st.CostOfn.G0 + alpha * newG0
            )

            let tpImproving = throughput >= st.LastThroughput
            let tpLo = min st.LastThroughput throughput
            let tpHi = max st.LastThroughput throughput
            let struct (tF0, tF1, tG1, tG0) =
                if tpImproving then struct (tpLo * 0.95, st.LastThroughput, throughput, tpHi * 1.05)
                else                struct (tpHi * 1.05, st.LastThroughput, throughput, tpLo * 0.95)

            st.ThroughputOfn <- OrderedFuzzyNumber(
                (1.0 - alpha) * st.ThroughputOfn.F0 + alpha * tF0,
                (1.0 - alpha) * st.ThroughputOfn.F1 + alpha * tF1,
                (1.0 - alpha) * st.ThroughputOfn.G1 + alpha * tG1,
                (1.0 - alpha) * st.ThroughputOfn.G0 + alpha * tG0
            )

            let costTrend = st.CostOfn.Orientation()
            let tpTrend   = st.ThroughputOfn.Orientation()

            if tpTrend < 0.0 then
                st.StepSign <- -st.StepSign

            let spreadRatio = FuzzyMath.clampFloat (st.CostOfn.Spread() / (abs (st.CostOfn.Defuzzify()) + 1e-6)) 0.05 1.0

            let trendMultiplier =
                if costTrend > 0.0 || imbalance > 1.4 then 1.0 - 0.14 * spreadRatio
                elif contentionRate > 0.35 then 1.0 + 0.18 * spreadRatio
                else 1.0 + 0.08 * st.StepSign * spreadRatio

            st.TargetUs <- FuzzyMath.clampFloat (st.TargetUs * trendMultiplier) 30.0 1800.0
            st.LastUnitNs <- meanUnitNs
            st.LastThroughput <- throughput
            st.Updates <- st.Updates + 1

        let defuzzCost = max 0.01 (st.CostOfn.Defuzzify())
        let threads = FuzzyMath.computeSafeThreads frameItems defuzzCost pressure logicalCores physicalCores

        let rawBatch = (st.TargetUs * 1000.0) / defuzzCost
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds frameItems threads
        let minQueueGuard = max minB (frameItems / max 1 (threads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        let relSpread = st.CostOfn.Spread() / (defuzzCost + 1e-6)
        let tpBonus = if st.ThroughputOfn.Orientation() > 0.0 then 0.10 else -0.05
        let confidence = float32 (FuzzyMath.clampFloat (1.0 - 0.45 * relSpread + tpBonus) 0.15 0.99)
        _ofnStates.[c] <- st

        Strategia(
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = confidence,
            AffinityHint = if pressure > 0.85 then 1 else 0
        )

    // =========================================================================
    // MODEL 3: ContextualBandit (Zero-Allocation Discounted UCB1)
    // =========================================================================
    let evaluateBandit c frameItems meanUnitNs imbalance pressure throughput logicalCores physicalCores =
        let mutable st = _banditStates.[c]
        let baseIdx = c * BanditArmCount
        let gamma = 0.96

        let reward = Math.Log(1.0 + max 0.0 throughput)
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
            // Initial round-robin sweep: evaluate each arm once before UCB exploitation begins.
            chosenArm <- st.TotalUpdates
        else
            let logTotal = Math.Log(max 1.0 totalN + 1.0)
            // Log-throughput rewards span ~0.1..2.0 across arms, so constant exploration factor 0.35 balances explore/exploit cleanly.
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

        let baseThreads = FuzzyMath.computeSafeThreads frameItems meanUnitNs pressure logicalCores physicalCores
        let armThreads =
            if baseThreads <= 1 then 1
            else max 1 (min logicalCores (int (Math.Round(float baseThreads * BanditThreadRatio.[chosenArm]))))

        // Context-aware translation: target batch duration scaled down smoothly by observed imbalance.
        let skewScale = 1.0 / max 1.0 (sqrt imbalance)
        let targetUs = BanditTargetUs.[chosenArm] * skewScale
        let rawBatch = (targetUs * 1000.0) / max 0.01 meanUnitNs
        let struct (minB, maxB) = FuzzyMath.computeBatchBounds frameItems armThreads
        let minQueueGuard = max minB (frameItems / max 1 (armThreads * 64))
        let batch = FuzzyMath.safeToInt (max rawBatch (float minQueueGuard)) minB maxB

        let armShare =
            if totalN > 1.0 then _banditCounts.[baseIdx + chosenArm] / totalN
            else 0.25
        let confidence = float32 (FuzzyMath.clampFloat (0.2 + 0.8 * armShare) 0.15 0.99)

        Strategia(
            BatchSize = batch,
            ThreadCount = armThreads,
            Confidence = confidence,
            AffinityHint = if pressure > 0.85 then 1 else 0
        )

    /// Active ML model used by this engine instance.
    member _.Model = _model

    /// Configured initial fallback batch size.
    member _.InitialBatchSize = _initialBatch

    /// <summary>
    /// Evaluates a periodic window of telemetry tablets and writes updated per-Cohort strategies.
    /// Strictly zero heap allocations.
    /// </summary>
    member _.Evaluate
        (topo: inref<Topologia>,
         cpuPressure: float32,
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

            // 1. Aggregate tablets per CohortId without allocating any collections.
            let mutable touchedCount = 0
            for i = 0 to omens.Length - 1 do
                let omen = omens.[i]
                let c = omen.CohortId
                if c >= 0 && c < limit && omen.ItemCount > 0 && omen.DurationTicks > 0L then
                    let unitNs = (float omen.DurationTicks * nsPerTick) / float omen.ItemCount
                    if _windows.[c].SampleCount = 0 then
                        _touchedCohorts.[touchedCount] <- c
                        touchedCount <- touchedCount + 1
                        _windows.[c].MinDurationTicks <- omen.DurationTicks
                        _windows.[c].MaxDurationTicks <- omen.DurationTicks
                        _windows.[c].MinUnitNs <- unitNs
                        _windows.[c].MaxUnitNs <- unitNs
                        _windows.[c].MaxTotalCohortItems <- max omen.ItemCount omen.TotalCohortItems
                        _windows.[c].TotalItems <- int64 omen.ItemCount
                        _windows.[c].TotalTicks <- omen.DurationTicks
                        _windows.[c].TotalSteals <- omen.StealCount
                        _windows.[c].TotalContentions <- omen.ContentionCount
                        _windows.[c].AvgCpuPressure <- float omen.CpuPressure
                        _windows.[c].SampleCount <- 1
                    else
                        _windows.[c].SampleCount <- _windows.[c].SampleCount + 1
                        _windows.[c].TotalItems <- _windows.[c].TotalItems + int64 omen.ItemCount
                        _windows.[c].TotalTicks <- _windows.[c].TotalTicks + omen.DurationTicks
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
                        _windows.[c].TotalSteals <- _windows.[c].TotalSteals + omen.StealCount
                        _windows.[c].TotalContentions <- _windows.[c].TotalContentions + omen.ContentionCount
                        _windows.[c].AvgCpuPressure <- _windows.[c].AvgCpuPressure + float omen.CpuPressure

            // 2. Run selected zero-alloc ML model for each active cohort.
            for idx = 0 to touchedCount - 1 do
                let c = _touchedCohorts.[idx]
                let w = _windows.[c]

                // Reset accumulator slot immediately for next ritual cycle.
                _windows.[c] <- CohortWindow()

                if w.TotalItems > 0L && w.TotalTicks > 0L then
                    let meanUnitNs = max 0.01 ((float w.TotalTicks * nsPerTick) / float w.TotalItems)
                    let meanDurUs = (float w.TotalTicks * nsPerTick) / (1000.0 * float (max 1 w.SampleCount))
                    let maxDurUs = max 0.01 ((float w.MaxDurationTicks * nsPerTick) / 1000.0)
                    let imbalance = FuzzyMath.clampFloat (maxDurUs / max 0.01 meanDurUs) 1.0 10.0
                    let contentionDenom = float (max 1 (w.TotalSteals + w.TotalContentions))
                    let contentionRate = float w.TotalContentions / contentionDenom
                    let pressure = FuzzyMath.clampFloat (max (float cpuPressure) (w.AvgCpuPressure / float (max 1 w.SampleCount))) 0.0 1.0
                    let throughput = float w.TotalItems / max 1.0 maxDurUs
                    let frameItems = max 1 w.MaxTotalCohortItems

                    let strategy =
                        match _model with
                        | OraculumModel.StaticHeuristic ->
                            evaluateStaticHeuristic frameItems meanUnitNs imbalance pressure logicalCores physicalCores
                        | OraculumModel.FuzzySugeno ->
                            evaluateFuzzySugeno c frameItems meanUnitNs w.MinUnitNs w.MaxUnitNs imbalance contentionRate pressure throughput logicalCores physicalCores
                        | OraculumModel.OrderedFuzzyNumbers ->
                            evaluateOfn c frameItems meanUnitNs w.MinUnitNs w.MaxUnitNs imbalance contentionRate pressure throughput logicalCores physicalCores
                        | OraculumModel.ContextualBandit ->
                            evaluateBandit c frameItems meanUnitNs imbalance pressure throughput logicalCores physicalCores
                        | _ ->
                            evaluateStaticHeuristic frameItems meanUnitNs imbalance pressure logicalCores physicalCores

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