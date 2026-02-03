namespace Legio.Core.Executio;

// Note: We deviate from strict Latin naming for "IJob" because it is an 
// industry-standard term in .NET, critical for understanding JIT optimization contexts.

/// <summary>
/// Mandatum (The Order).
/// The contract for an execution unit. 
/// It MUST be implemented by a struct to allow the JIT compiler to perform de-virtualization.
/// </summary>
/// <typeparam name="T">The type of data to be processed.</typeparam>
public interface IJob<T>
{
    /// <summary>
    /// Executes the logic on a specific item.
    /// The item is passed by reference, allowing in-place modification.
    /// </summary>
    /// <param name="item">Reference to the data element.</param>
    void Execute(ref T item);
}

/// <summary>
/// A delegate wrapper for reference-based actions.
/// Useful for unsafe pointer logic wrappers or quick prototypes.
/// </summary>
/// <typeparam name="T">The type of data to be processed.</typeparam>
public delegate void RefAction<T>(ref T item);