namespace Legio.Haruspex

open Legio.Protocol
open System

module Oracle =

    /// <summary>
    /// The Divine Function.
    /// Pure, stateless calculation of the next strategy.
    /// </summary>
    let divine (topo: Topologia) (omens: ReadOnlySpan<Tessera>) : Strategia =
        
        // 1. Calculate Average Duration per Item from the batch
        let mutable totalItems = 0L
        let mutable totalTicks = 0L
        
        for i in 0 .. omens.Length - 1 do
            let omen = omens.[i]
            totalItems <- totalItems + int64 omen.ItemCount
            totalTicks <- totalTicks + omen.DurationTicks

        let avgCostPerItem = 
            if totalItems > 0L then float totalTicks / float totalItems 
            else 0.0

        // 2. Heuristic Logic (Placeholder for SGD Model)
        // If task is heavy (> 1000 ticks/item), create smaller batches
        let mutable batchSize = 1024
        
        if avgCostPerItem > 1000.0 then
            batchSize <- 64
        elif avgCostPerItem > 100.0 then
            batchSize <- 256
        else
            batchSize <- 4096 // Memory bound -> larger batches

        // 3. Thread Count Logic
        // Use all cores unless cost is extremely low
        let threadCount = topo.LogicalCoreCount

        // POPRAWKA: Jawna inicjalizacja struktury C#
        new Strategia(
            BatchSize = batchSize,
            ThreadCount = threadCount,
            Confidence = 1.0f,
            AffinityHint = 0
        )