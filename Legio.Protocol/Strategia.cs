using System.Runtime.InteropServices;

namespace Legio.Protocol;

/// <summary>
/// Strategia (The Strategy).
/// Directives issued by the Haruspex (Oracle) per Cohort based on periodic analysis of Tesserae.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
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
    /// Model confidence score [0.0 - 1.0] (e.g., inversely proportional to fuzzy width / uncertainty).
    /// </summary>
    public float Confidence;

    /// <summary>
    /// Hints if the task should prefer a specific core class (0 = All, 1 = P-Cores preferred).
    /// </summary>
    public int AffinityHint;
}