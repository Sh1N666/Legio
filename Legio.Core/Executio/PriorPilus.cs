namespace Legio.Core.Executio;

/// <summary>
/// Interface for a Cohort Commander (System).
/// Executes chunked ranges dispatched by Legatus and stolen via Optio deques.
/// </summary>
public interface IPriorPilus
{
    /// <summary>
    /// Logical identifier of this system/cohort (used for ML telemetry grouping).
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Total number of items to process in the current frame.
    /// </summary>
    int GetTotalItemCount();

    /// <summary>
    /// Executes user logic over the contiguous slice [start, start + length).
    /// Must be thread-safe for disjoint ranges.
    /// </summary>
    void ExecuteBatch(int start, int length);
}

/// <summary>
/// Base class for all System Commanders.
/// Zero global state, zero delegate allocations — directly invoked by Centurions on real TaskUnit ranges.
/// </summary>
public abstract class PriorPilus : IPriorPilus
{
    protected readonly int _id;

    public int Id => _id;

    protected PriorPilus(int id)
    {
        _id = id;
    }

    int IPriorPilus.GetTotalItemCount() => GetTotalItemCount();

    void IPriorPilus.ExecuteBatch(int start, int length) => ExecuteBatchLogic(start, length);

    /// <summary>
    /// Processes items in [start, start + length). Called concurrently across disjoint ranges by Centurions.
    /// </summary>
    protected abstract void ExecuteBatchLogic(int start, int length);

    /// <summary>
    /// Returns the total item count to process for the upcoming frame.
    /// </summary>
    protected abstract int GetTotalItemCount();
}