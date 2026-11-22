using Legio.Core.Armarium;

namespace Legio.Core.Executio;

/// <summary>
/// Cohors (The Cohort / System).
/// Represents a registered system with its Read/Write resource masks (Vexillum).
/// </summary>
public sealed class Cohors
{
    public readonly int Id;
    public readonly IPriorPilus Commander;

    // Resource dependency masks (bits 0..255).
    public Vexillum ReadMask;
    public Vexillum WriteMask;

    // Assigned by Legatus during Consilium DAG construction.
    internal int PhaseIndex = -1;

    // Internal dense slot assigned upon registration inside a specific Legatus instance.
    internal int Slot = -1;

    public Cohors(int id, IPriorPilus commander)
    {
        Id = id;
        Commander = commander ?? throw new ArgumentNullException(nameof(commander));
        ReadMask = new Vexillum();
        WriteMask = new Vexillum();
    }
}