namespace Legio.Haruspex

open System.Collections.Concurrent
open Legio.Protocol
open System

module Oracle =

    module Constants =
        // Target duration for a single batch processing (200us).
        // Helps amortize the cost of atomic operations.
        let TargetBatchDurationTicks = 2_000_000.0
        
        // The hard floor for batch size.
        let AbsoluteMinBatch = 16
        
        // Maximum tasks per core to prevent queue flooding.
        let MaxBatchesPerCore = 64
        
        // Fallback size used when no telemetry data is available (Startup).
        let DefaultBatchSize = 1024 

    /// <summary>
    /// The Grimoire (Knowledge Base).
    /// Stores the optimal BatchSize found for a given System ID.
    /// Being static, it persists across Legatus restarts, simulating a "Pre-Trained Model".
    /// Key: CohortId (System ID), Value: Optimal BatchSize
    /// </summary>
    let private _grimoire = new ConcurrentDictionary<int, int>()
    
    /// <summary>
    /// The Divine Function.
    /// Calculates optimal strategy based on telemetry.
    /// Includes protection against cold-start scenarios (0 items).
    /// </summary>
    let divine (topo: Topologia) (omens: ReadOnlySpan<Tessera>) : Strategia =
        
        // 1. DATA GATHERING
        let mutable totalItems = 0L
        let mutable totalTicks = 0L
        
        for i in 0 .. omens.Length - 1 do
            let omen = omens.[i]
            totalItems <- totalItems + int64 omen.ItemCount
            totalTicks <- totalTicks + omen.DurationTicks

        // 2. SAFETY CHECK (CRITICAL FIX)
        // If the system is starting up (Frame 0) or telemetry is missing,
        // totalItems will be 0. Math.Clamp(..., 16, 0) would throw an exception.
        // We return a safe default strategy in this case.
        if totalItems < int64 Constants.AbsoluteMinBatch then
             new Strategia(
                BatchSize = Constants.DefaultBatchSize,
                ThreadCount = topo.LogicalCoreCount,
                Confidence = 0.5f,
                AffinityHint = 0
            )
        else
            // 3. MATHEMATICAL LOGIC (Only runs when we have valid data)
            
            // A. Unit Cost
            let avgCostPerItem = float totalTicks / float totalItems 

            // B. Time-Based Calculation (Targeting 200us)
            let timeBasedBatch = Constants.TargetBatchDurationTicks / (avgCostPerItem + 0.0001)

            // C. Quantity-Based Calculation (Preventing Queue Flood)
            let totalCores = Math.Max(1, topo.LogicalCoreCount)
            let maxTasksLimit = int64 (totalCores * Constants.MaxBatchesPerCore)
            
            // Protection against division by zero if maxTasksLimit is somehow 0
            let quantityBasedMinBatch = 
                if maxTasksLimit > 0L then (int64 totalItems) / maxTasksLimit
                else 1L

            // D. Synthesis
            let optimalBatch = Math.Max(int timeBasedBatch, int quantityBasedMinBatch)

            // E. Clamping
            // This is now safe because we ensured totalItems >= AbsoluteMinBatch in step 2.
            let maxClamp = int totalItems
            let finalBatchSize = 
                Math.Clamp(optimalBatch, Constants.AbsoluteMinBatch, maxClamp)

            new Strategia(
                BatchSize = finalBatchSize,
                ThreadCount = totalCores,
                Confidence = 1.0f,
                AffinityHint = 0
            )