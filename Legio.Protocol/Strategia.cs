using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Strategia (The Strategy).
/// Directives issued by the Haruspex (Oracle) based on the analysis of Tesserae.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Strategia
{
    /// <summary>
    /// The optimal size of a data chunk (Centuria) for the current workload.
    /// </summary>
    public int BatchSize;

    /// <summary>
    /// How many Centurions (Threads) should be deployed for this Cohort.
    /// </summary>
    public int ThreadCount;

    /// <summary>
    /// A learning rate parameter or confidence score from the model.
    /// Used for debugging or adaptive tuning.
    /// </summary>
    public float Confidence;

    /// <summary>
    /// Hints if the task should force a specific core type (e.g., 1 for P-Cores only).
    /// </summary>
    public int AffinityHint;
}