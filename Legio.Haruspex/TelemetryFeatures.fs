namespace Legio.Haruspex

/// <summary>
/// Pre-allocated per-cohort window accumulator (zero allocation during inference).
/// Captures all Option A telemetry features including barrier wait and spatial half-ticks.
/// </summary>
[<Struct>]
type internal CohortWindow =
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
type internal WindowFeatures =
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
