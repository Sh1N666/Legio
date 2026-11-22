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
/// Shared zero-allocation fuzzy membership functions and hardware-aware scheduling helpers.
/// </summary>
module internal FuzzyMath =

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
