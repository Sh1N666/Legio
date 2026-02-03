using System.Diagnostics;
using Legio.Core.Augurium;
using Legio.Protocol;

namespace Legio.Core.Executio;

/// <summary>
/// Centurio (The Commander of 100).
/// Represents a single worker thread pinned to a specific hardware core.
/// </summary>
public class Centurio
{
    public readonly int Id;
    public readonly Optio Optio; // Local queue
    
    private readonly Thread _thread;
    private readonly Trecenarius _trecenarius; 
    
    // Reference to the squad (all Centurions) for stealing
    private Centurio[]? _legion; 
    
    private volatile bool _isRunning;
    private readonly ManualResetEventSlim _battleSignal; 

    public Centurio(int id, Trecenarius trecenarius)
    {
        Id = id;
        _trecenarius = trecenarius;
        Optio = new Optio();
        _battleSignal = new ManualResetEventSlim(false);
        _isRunning = true;

        _thread = new Thread(MilitiaLoop)
        {
            Name = $"Centurio_{id}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal 
        };
    }

    public void Enlist(Centurio[] legion)
    {
        _legion = legion;
    }

    public void Start() => _thread.Start();

    public void WakeUp() => _battleSignal.Set();

    /// <summary>
    /// Public method called by the Main Thread (Primus) to help processing.
    /// It drains the local queue and tries to steal until no work remains visible.
    /// </summary>
    public void ProcessLocalAndSteal()
    {
        bool foundWork;
        do
        {
            foundWork = false;

            // 1. PRIMARY MISSION: Process local queue until empty
            while (Optio.TryPop(out TaskUnit task))
            {
                ExecuteTask(task);
                foundWork = true;
            }

            // 2. SECONDARY MISSION: Steal from others (Work Stealing)
            if (_legion != null)
            {
                // Random victim start to avoid contention
                int victimId = (Id + 1) % _legion.Length; 
                
                // Try to steal from everyone once
                for (int i = 0; i < _legion.Length; i++)
                {
                    if (victimId != Id)
                    {
                        if (_legion[victimId].Optio.TrySteal(out TaskUnit stolenTask))
                        {
                            ExecuteTask(stolenTask);
                            foundWork = true;
                            // If we stole successfully, go back to checking local queue 
                            // (in case the stolen task generated new local sub-tasks)
                            break; 
                        }
                    }
                    victimId = (victimId + 1) % _legion.Length;
                }
            }
        } while (foundWork); 
        // Loops until both local queue is empty AND one full round of stealing failed.
    }

    /// <summary>
    /// The eternal loop of the background soldier.
    /// </summary>
    private void MilitiaLoop()
    {
        // 1. PINNING (Affinity placeholder)
        // Thread.BeginThreadAffinity()...

        while (_isRunning)
        {
            // Wait for the horn (Signal)
            _battleSignal.Wait(); 

            // Execute the work logic
            ProcessLocalAndSteal();

            // Check once more before sleeping to avoid race condition
            // (If someone pushed work right as we finished)
            if (!Optio.HasWork) 
            {
                _battleSignal.Reset();
                // Double check pattern
                if (Optio.HasWork) _battleSignal.Set();
            }
        }
    }

    /// <summary>
    /// Executes a single unit of work and reports to Trecenarius.
    /// </summary>
    private void ExecuteTask(TaskUnit task)
    {
        long start = Stopwatch.GetTimestamp();

        // CALL THE COMMANDER
        PriorPilusRegistry.ExecuteBatch(task.PriorPilusId, task.Start, task.Length);

        long end = Stopwatch.GetTimestamp();

        // REPORT PERFORMANCE
        var tessera = new Tessera
        {
            DurationTicks = end - start,
            ItemCount = task.Length,
            CenturioId = Id,
            CohortId = task.PriorPilusId,
            CpuPressure = 0 
        };
        
        _trecenarius.Report(tessera);
    }
}

/// <summary>
/// Temporary static registry to link IDs to actual logic.
/// Will be managed by Legatus later.
/// </summary>
public static class PriorPilusRegistry
{
    // Array of delegates or Interfaces
    public static Action<int, int>[] _commanders = new Action<int, int>[64];

    public static void Register(int id, Action<int, int> executeMethod)
    {
        _commanders[id] = executeMethod;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void ExecuteBatch(int id, int start, int length)
    {
        _commanders[id]?.Invoke(start, length);
    }
}