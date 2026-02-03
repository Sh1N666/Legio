using Legio.Core.Augurium;

namespace Legio.Core.Executio;

/// <summary>
/// Interface for a Cohort Commander.
/// </summary>
public interface IPriorPilus
{
    /// <summary>
    /// Executes the cohort's mission.
    /// </summary>
    /// <param name="scriba">Access to the current strategy.</param>
    /// <param name="mainCenturion">The Centurion executing the main thread (for dispatch).</param>
    void ExecuteBattleOrders(Scriba scriba, Centurio mainCenturion);
}

/// <summary>
/// Base class for all System Commanders.
/// </summary>
public abstract class PriorPilus : IPriorPilus
{
    protected readonly int _id;

    protected PriorPilus(int id)
    {
        _id = id;
        // Register self in the static registry so Centurions can call back
        PriorPilusRegistry.Register(id, ExecuteBatchLogic);
    }

    public void ExecuteBattleOrders(Scriba scriba, Centurio mainCenturion)
    {
        // 1. Get the Oracle's advice
        var strategy = scriba.Legere();

        // 2. Prepare the work (Abstract method implemented by Arch/User)
        // This calculates how many items we have.
        int totalItems = GetTotalItemCount();
        
        if (totalItems == 0) return;

        // 3. Dispatch Orders (Slicing)
        // If strategy suggests single thread or items are few, run serial.
        if (strategy.ThreadCount <= 1 || totalItems < strategy.BatchSize)
        {
            ExecuteBatchLogic(0, totalItems);
        }
        else
        {
            // Divide and Conquer
            int batchSize = strategy.BatchSize;
            for (int start = 0; start < totalItems; start += batchSize)
            {
                int count = Math.Min(batchSize, totalItems - start);
                
                // Push task to the Main Centurion's queue.
                // Other threads will steal from him.
                mainCenturion.Optio.Push(new TaskUnit 
                { 
                    PriorPilusId = _id, 
                    Start = start, 
                    Length = count 
                });
            }
            
            // Note: The main thread will verify the queue in the Legatus loop
        }
    }

    /// <summary>
    /// The actual work logic (e.g., iterating a query).
    /// Called by Worker Threads (Centurions).
    /// </summary>
    protected abstract void ExecuteBatchLogic(int start, int length);

    protected abstract int GetTotalItemCount();
}