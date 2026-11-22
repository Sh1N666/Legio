namespace Legio.Haruspex

open System
open Legio.Protocol
open Legio.Haruspex.Models

/// <summary>
/// Isolated, zero-allocation Haruspex ML Oracle Engine owned by a single Legatus instance.
/// Aggregates Option A telemetry tablets per Cohort and dispatches inference to the selected model:
///   0. StaticHeuristic     - Analytical baseline (control group: Contiguous / StealOne / fixed spin)
///   1. FuzzySugeno         - TFN + Online-Adaptive Takagi-Sugeno FIS (Soft Computing)
///   2. OrderedFuzzyNumbers - Kosiński's OFN trend-aware controller (Soft Computing)
///   3. ContextualBandit    - Discounted UCB1 Multi-Armed Bandit (Tabular Online RL)
///   4. OnlineLinUcb        - Discounted Linear Contextual Bandit / Online Ridge Regression
///                            trained via Sherman-Morrison Recursive Least Squares (Classical Online ML)
/// </summary>
type OraculumEngine(maxCohorts: int, model: OraculumModel, maxThreads: int, initialBatchSize: int) =

    let _maxCohorts = max 1 maxCohorts
    let _maxThreads = max 1 maxThreads
    let _initialBatch = max 16 initialBatchSize
    let _model = model

    // All state is pre-allocated once at startup -> strictly 0 B GC allocation during Evaluate.
    let _windows = Array.zeroCreate<CohortWindow> _maxCohorts
    let _touchedCohorts = Array.zeroCreate<int> _maxCohorts

    // Allocate state strictly for the active model only (zero wasted RAM for unused models).
    let _fuzzySugeno =
        if model = OraculumModel.FuzzySugeno then FuzzySugenoModel(_maxCohorts)
        else Unchecked.defaultof<FuzzySugenoModel>
    let _orderedFuzzy =
        if model = OraculumModel.OrderedFuzzyNumbers then OrderedFuzzyModel(_maxCohorts)
        else Unchecked.defaultof<OrderedFuzzyModel>
    let _contextualBandit =
        if model = OraculumModel.ContextualBandit then ContextualBanditModel(_maxCohorts)
        else Unchecked.defaultof<ContextualBanditModel>
    let _onlineLinUcb =
        if model = OraculumModel.OnlineLinUcb then OnlineLinUcbModel(_maxCohorts)
        else Unchecked.defaultof<OnlineLinUcbModel>

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
                            StaticHeuristicModel.evaluate &features logicalCores physicalCores
                        | OraculumModel.FuzzySugeno ->
                            _fuzzySugeno.Evaluate(c, &features, logicalCores, physicalCores)
                        | OraculumModel.OrderedFuzzyNumbers ->
                            _orderedFuzzy.Evaluate(c, &features, logicalCores, physicalCores)
                        | OraculumModel.ContextualBandit ->
                            _contextualBandit.Evaluate(c, &features, logicalCores, physicalCores)
                        | OraculumModel.OnlineLinUcb ->
                            _onlineLinUcb.Evaluate(c, &features, logicalCores, physicalCores)
                        | _ ->
                            StaticHeuristicModel.evaluate &features logicalCores physicalCores

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