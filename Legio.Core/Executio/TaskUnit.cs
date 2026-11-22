using System.Runtime.InteropServices;

namespace Legio.Core.Executio;

/// <summary>
/// A raw description of a work unit (Centuria) to be processed.
/// Blittable struct used inside the Work Stealing Deque.
/// It doesn't hold the Job logic, only the Data coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TaskUnit
{
    /// <summary>
    /// Index of the Prior Pilus (Cohort Commander) who owns this task.
    /// The Centurion will callback this commander to execute the specific logic.
    /// </summary>
    public int PriorPilusId;

    /// <summary>
    /// Start index in the memory buffer (Manipulus).
    /// </summary>
    public int Start;

    /// <summary>
    /// Number of elements to process.
    /// </summary>
    public int Length;
}