using System.Reflection;
using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Core.Executio;
using Legio.Core.Armarium;
using Legio.Protocol;

namespace Legio.Core;

/// <summary>
/// Legatus (The General).
/// The supreme commander of the ECS architecture.
/// Manages thread lifecycle, analyzes hardware topology via Mensor,
/// resolves system dependencies (Vexillum), and orchestrates frame execution.
/// </summary>
public class Legatus : IDisposable
{
    // --- INTEL (Augurium) ---
    private readonly Scriba _scriba;
    private readonly Trecenarius _trecenarius;
    private readonly Sanctum _sanctum;
    private readonly Topologia _topologia; // Noun: Latin

    // --- ARMY (Exercitus) ---
    private readonly Centurio[] _centurions; 
    
    // The master registry of all cohorts
    private readonly Cohors[] _tabularium; 
    
    // The sorted execution order (flat array)
    private Cohors[] _ordo; 
    
    private int _cohortCount;

    // --- TACTICS (Consilium) ---
    // "Gradus" = Step/Stage/Phase of the battle
    [StructLayout(LayoutKind.Sequential)]
    private struct Gradus 
    { 
        public int StartIndex; 
        public int Count; 
    }
    
    // The Battle Plan consists of sequential steps (phases)
    private readonly Gradus[] _consilium;
    private int _gradusCount;

    /// <summary>
    /// Creates a new Legion.
    /// Automatically attempts to locate the 'Legio.Haruspex' library for intelligence.
    /// </summary>
    /// <param name="maxCohorts">Maximum number of systems supported (default 64).</param>
    public Legatus(int maxCohorts = 64)
    {
        // 1. Survey the Terrain
        _topologia = Mensor.Result;

        // 2. Auto-Wire the Oracle (Dynamic Loading)
        OraculumFunc oracleDelegate = LoadOraculum();

        // 3. Initialize Intelligence
        _scriba = new Scriba();
        _trecenarius = new Trecenarius();
        _sanctum = new Sanctum(_trecenarius, _scriba, oracleDelegate);

        // 4. Initialize Troops
        // Determine army size based on logical cores
        int threadCount = _topologia.LogicalCoreCount;
        _centurions = new Centurio[threadCount];
        
        for (int i = 0; i < threadCount; i++)
        {
            _centurions[i] = new Centurio(i, _trecenarius);
        }

        // Link for work stealing topology
        foreach (var c in _centurions) c.Enlist(_centurions);

        // Start background workers (skip 0, as 0 is the calling thread/Primus)
        for (int i = 1; i < threadCount; i++) _centurions[i].Start();

        // Start Intelligence Loop
        _sanctum.Start();

        // 5. Initialize Logistics
        _tabularium = new Cohors[maxCohorts];
        _ordo = new Cohors[maxCohorts];
        _consilium = new Gradus[32]; // Support up to 32 dependency phases
    }

    /// <summary>
    /// Enlists a new Cohort (System) into the Legion.
    /// </summary>
    public void RegisterCohors(Cohors cohors)
    {
        if (_cohortCount >= _tabularium.Length)
            throw new InvalidOperationException("Tabularium is full. Increase maxCohorts.");

        _tabularium[_cohortCount++] = cohors;
    }

    /// <summary>
    /// Constructs the Battle Plan (Consilium) based on dependencies.
    /// Uses a Greedy Topological Sort to maximize parallelism while respecting Vexillum masks.
    /// </summary>
    public void ConstructConsilium()
    {
        _gradusCount = 0;

        // Temporary tracking
        // Using a pooled array or simple heap allocation (once per startup) is acceptable.
        bool[] assigned = new bool[_cohortCount]; 
        int assignedCount = 0;
        int execIndex = 0;

        // Clear the order buffer
        Array.Clear(_ordo, 0, _cohortCount);

        // --- GREEDY TOPOLOGICAL SORT ---
        while (assignedCount < _cohortCount)
        {
            int stepStartIndex = execIndex;
            
            // Territory masks for the current step (Gradus)
            Vexillum gradusWrites = new Vexillum();
            Vexillum gradusReads = new Vexillum();

            for (int i = 0; i < _cohortCount; i++)
            {
                if (assigned[i]) continue;

                var candidate = _tabularium[i];

                // CONFLICT CHECKS (Confligerea)
                // 1. Write-Write Conflict
                bool writeConflict = gradusWrites.Confligerea(candidate.WriteMask);
                // 2. Read-Write Conflict
                bool readWriteConflict = gradusReads.Confligerea(candidate.WriteMask);
                // 3. Write-Read Conflict
                bool writeReadConflict = gradusWrites.Confligerea(candidate.ReadMask);

                if (!writeConflict && !readWriteConflict && !writeReadConflict)
                {
                    // Recruit to current Gradus
                    _ordo[execIndex++] = candidate;
                    candidate.PhaseIndex = _gradusCount; 
                    assigned[i] = true;
                    assignedCount++;

                    // Expand territory
                    gradusWrites.Iungere(candidate.WriteMask);
                    gradusReads.Iungere(candidate.ReadMask);
                }
            }

            // Record the step boundaries
            _consilium[_gradusCount++] = new Gradus 
            { 
                StartIndex = stepStartIndex, 
                Count = execIndex - stepStartIndex 
            };

            // Deadlock detection
            if (execIndex == stepStartIndex)
            {
                throw new InvalidOperationException("Deadlock detected in Consilium. Cyclic dependency in Vexillum masks.");
            }
        }
    }

    /// <summary>
    /// Commands the Legion to fight one frame.
    /// </summary>
    public void ExecuteFrame()
    {
        var primus = _centurions[0];

        // 1. Wake up the army (Background threads)
        for (int i = 1; i < _centurions.Length; i++) _centurions[i].WakeUp();

        // 2. Iterate through the Plan (Consilium)
        for (int p = 0; p < _gradusCount; p++)
        {
            Gradus gradus = _consilium[p];

            // A. DISPATCH ORDERS
            // Push tasks to the Main Thread's queue (Primus).
            // Other threads will steal from here.
            for (int i = 0; i < gradus.Count; i++)
            {
                var cohors = _ordo[gradus.StartIndex + i];
                cohors.Commander.ExecuteBattleOrders(_scriba, primus);
            }

            // B. FIGHT (Work Stealing Loop)
            // The Main Thread participates in the battle.
            // This method blocks until the local queue is empty AND stealing fails.
            primus.ProcessLocalAndSteal();

            // C. SYNCHRONIZATION
            // Implicit barrier: Since tasks in Gradus N+1 require data from Gradus N,
            // and ProcessLocalAndSteal drains all available work, we naturally wait here.
        }
    }

    public void Dispose()
    {
        // TODO: Signal shutdown to threads
    }

    // ==========================================
    // AUTO-WIRING LOGIC (REFLECTION)
    // ==========================================

    /// <summary>
    /// Attempts to load the F# Oracle from 'Legio.Haruspex.dll'.
    /// Creates a high-performance open delegate if found.
    /// Falls back to a dummy C# implementation if missing.
    /// </summary>
    private OraculumFunc LoadOraculum()
    {
        try
        {
            // Try to load the assembly (it should be alongside Legio.Core.dll in the NuGet package)
            var assemblyName = new AssemblyName("Legio.Haruspex");
            Assembly assembly;
            
            try 
            {
                assembly = Assembly.Load(assemblyName);
            }
            catch 
            {
                // If not found in GAC/Context, try local file (helpful for debug)
                return FallbackOraculum;
            }

            if (assembly != null)
            {
                // F# modules compile to static classes.
                // We look for the "Oracle" module and "divine" function.
                var oracleType = assembly.GetType("Legio.Haruspex.Oracle");
                if (oracleType != null)
                {
                    var divineMethod = oracleType.GetMethod("divine", BindingFlags.Public | BindingFlags.Static);
                    if (divineMethod != null)
                    {
                        // Create a Delegate. 
                        // This removes Reflection overhead for subsequent calls.
                        // It binds the method info to the delegate type strictly.
                        return (OraculumFunc)Delegate.CreateDelegate(typeof(OraculumFunc), divineMethod);
                    }
                }
            }
        }
        catch 
        {
            // TODO: Loging or error handling here.
        }

        // If anything fails, use the safe fallback
        return FallbackOraculum;
    }

    /// <summary>
    /// A dummy strategy used if the F# library is missing or fails to load.
    /// Ensures the engine still runs, just without ML optimization.
    /// </summary>
    private static Strategia FallbackOraculum(Topologia topo, ReadOnlySpan<Tessera> omens)
    {
        // Conservative default strategy
        return new Strategia 
        { 
            BatchSize = 1024, 
            ThreadCount = topo.LogicalCoreCount,
            Confidence = 0.5f,
            AffinityHint = 0
        };
    }
}