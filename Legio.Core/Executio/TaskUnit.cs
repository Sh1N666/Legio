using System.Runtime.InteropServices;

namespace Legio.Core.Executio;

/// <summary>
/// A raw, 16-byte blittable description of a work chunk (Centuria) inside an Optio deque.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TaskUnit
{
    /// <summary>
    /// Internal slot index of the Cohort/Commander inside Legatus.
    /// </summary>
    public int CohortSlot;

    /// <summary>
    /// Start element index in the target data buffer.
    /// </summary>
    public int Start;

    /// <summary>
    /// Number of elements to process in this chunk.
    /// </summary>
    public int Length;

    /// <summary>
    /// Ring slot (epoch & 7) of the PhaseFrame that owns this chunk.
    /// Ensures completion accounting always credits the exact owning phase.
    /// </summary>
    public int PhaseSlot;
}