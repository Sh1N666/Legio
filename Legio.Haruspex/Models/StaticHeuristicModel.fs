namespace Legio.Haruspex.Models

open Legio.Protocol
open Legio.Haruspex

/// <summary>
/// MODEL 0: StaticHeuristic — Deterministic analytical baseline (control group).
/// Uses fixed Option B defaults (Contiguous partitioning, StealOne, AffinityMode.None, fixed Spin=1024)
/// so adaptive ML models can be scientifically benchmarked against a non-learning control baseline.
/// </summary>
module internal StaticHeuristicModel =

    let evaluate (f: inref<WindowFeatures>) (logicalCores: int) (physicalCores: int) : Strategia =
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
