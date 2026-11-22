using Legio.Core.Armarium;

namespace Legio.Core.Executio;

/// <summary>
/// Cohors (The Cohort / System).
/// Represents a registered ECS System with its memory dependencies.
/// </summary>
public class Cohors
{
    public readonly int Id;
    public readonly IPriorPilus Commander;
    
    // Dependency Flags (The Territory)
    public Vexillum ReadMask;
    public Vexillum WriteMask;

    // Assigned by Legatus during Battle Plan construction
    internal int PhaseIndex = -1;

    public Cohors(int id, IPriorPilus commander)
    {
        Id = id;
        Commander = commander;
        ReadMask = new Vexillum(); // Starts empty, filled by Arch integration
        WriteMask = new Vexillum();
    }
}