using Legio.Core.Augurium;
using System.Runtime.CompilerServices;

namespace Legio.Core.Executio;

/// <summary>
/// Interface for a Cohort Commander.
/// </summary>
public interface IPriorPilus
{
    void ExecuteBattleOrders(Scriba scriba, Centurio mainCenturion);
}

/// <summary>
/// Base class for all System Commanders.
/// Implements "Atomic Iterator" pattern for maximum throughput.
/// </summary>
public abstract class PriorPilus : IPriorPilus
{
    protected readonly int _id;
    private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;
    // ATOMIC STATE
    // Points to the next available item index. Shared across all threads.
    private int _nextIndex;
    private long _pad7, _pad8, _pad9, _pad10, _pad11, _pad12, _pad13;
    private int _totalItems;
    private int _batchSize;

    protected PriorPilus(int id)
    {
        _id = id;
        // Register the "Help Me" callback.
        // It now matches Func<int, int, int> signature required for telemetry.
        PriorPilusRegistry.Register(id, HelpExecuteBatch);
    }

    public void ExecuteBattleOrders(Scriba scriba, Centurio mainCenturion)
    {
        // 1. Get Strategy
        var strategy = scriba.Legere();

        // 2. Setup the Job
        _totalItems = GetTotalItemCount();
        if (_totalItems == 0) return;

        _batchSize = strategy.BatchSize;
        
        // RESET THE ATOMIC COUNTER
        // This is safe because we are currently single-threaded in the Dispatch Phase.
        _nextIndex = 0;

        // 3. Dispatch "Workers" instead of "Work Chunks"
        // Instead of creating 1000 tasks, we create 1 task per available thread.
        // Each task is a "Job Runner" that will drain the atomic counter.
        
        int workersCount = strategy.ThreadCount; 

        // We push 'workersCount' tasks. Each task simply says: "Go help PriorPilus #ID".
        // The start/length in TaskUnit are ignored here because the thread will ask for them dynamically.
        for (int i = 0; i < workersCount; i++)
        {
            mainCenturion.Optio.Push(new TaskUnit 
            { 
                PriorPilusId = _id, 
                // Start/Length are irrelevant now, the worker will fetch them atomically
                Start = 0, 
                Length = 0 
            });
        }
    }

    /// <summary>
    /// The callback executed by Centurions.
    /// Instead of doing one chunk, it loops until the work is exhausted.
    /// Returns the total number of items processed by this thread for telemetry.
    /// </summary>
    private int HelpExecuteBatch(int ignoredStart, int ignoredLength)
    {
        // LOCAL CACHING
        // Copy volatiles to local stack to avoid accessing 'this' constantly
        int batch = _batchSize;
        int total = _totalItems;
        int totalProcessedByMe = 0; // Local accumulator for telemetry
        
        // ATOMIC LOOP (The Engine Heart)
        while (true)
        {
            // 1. Claim a range atomically
            // Interlocked.Add returns the *incremented* value. So we subtract batch to get start.
            int end = Interlocked.Add(ref _nextIndex, batch);
            int start = end - batch;

            // 2. Validate range
            // If start is beyond total, there is no work left.
            if (start >= total) break;

            // 3. Clamp the last batch (if we overshot total)
            int actualLength = (end > total) ? (total - start) : batch;

            // 4. Execute user logic
            ExecuteBatchLogic(start, actualLength);
            
            // 5. Accumulate telemetry data
            totalProcessedByMe += actualLength;
        }

        // Return the actual amount of work done by this thread.
        // This prevents the Oracle from seeing "0 items" and crashing.
        return totalProcessedByMe;
    }

    /// <summary>
    /// The actual work logic (User Code).
    /// </summary>
    protected abstract void ExecuteBatchLogic(int start, int length);

    protected abstract int GetTotalItemCount();
}