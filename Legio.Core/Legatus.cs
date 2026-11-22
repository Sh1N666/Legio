using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Core.Executio;
using Legio.Protocol;

namespace Legio.Core;

/// <summary>
/// Legatus (The General).
/// Supreme commander of the scheduler. Manages worker Centurions, DAG phase planning (Consilium),
/// hybrid initial-slice + Optio work stealing, exact phase barriers, and lifecycle cleanup.
/// </summary>
public sealed class Legatus : IDisposable
{
    private const int EpochRingSize = 8;
    private const int EpochRingMask = EpochRingSize - 1;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)]
        public long Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gradus
    {
        public int StartIndex;
        public int Count;
    }

    // --- CONFIG & INTEL (Augurium) ---
    private readonly LegatusConfig _config;
    private readonly Scriba _scriba;
    private readonly Trecenarius _trecenarius;
    private readonly Sanctum _sanctum;
    private readonly Topologia _topologia;

    // --- ARMY (Exercitus) ---
    private readonly Centurio[] _centurions;
    private readonly PhaseFrame[] _phaseRing;
    private PaddedLong _phaseEpoch;

    // --- REGISTRY & CONSILIUM ---
    private readonly Cohors[] _tabularium;
    private readonly IPriorPilus[] _commanders;
    private readonly Cohors[] _ordo;
    private readonly Gradus[] _consilium;
    private readonly int[] _phaseCounts;
    private readonly int[] _phaseOffsets;
    private int _cohortCount;
    private int _gradusCount;

    private long _frameCounter;
    private bool _disposed;

    public int ThreadCount => _centurions.Length;
    public int GradusCount => _gradusCount;
    public Topologia Topologia => _topologia;
    public Scriba Scriba => _scriba;
    public bool IsHaruspexLoaded { get; }
    public OraculumModel ActiveOracleModel => _config.OracleModel;
    public long OracleRitualCount => _sanctum.RitualCount;

    internal long CurrentEpoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _phaseEpoch.Value);
    }

    /// <summary>
    /// Creates a new Legion with default configuration and the specified max cohort count.
    /// </summary>
    public Legatus(int maxCohorts = 64)
        : this(new LegatusConfig { MaxCohorts = maxCohorts })
    {
    }

    /// <summary>
    /// Creates a new Legion configured by <paramref name="config"/>.
    /// </summary>
    public Legatus(LegatusConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.MaxCohorts <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "MaxCohorts must be positive.");

        _topologia = Mensor.Result;

        int threadCount = _config.ThreadCount > 0
            ? _config.ThreadCount
            : Math.Max(1, _topologia.LogicalCoreCount);

        OraculumBatchFunc oracleDelegate = LoadOraculum(threadCount, out bool loadedHaruspex);
        IsHaruspexLoaded = loadedHaruspex;

        _scriba = new Scriba(_config.MaxCohorts, _config.InitialBatchSize, threadCount);
        _trecenarius = new Trecenarius(_config.CirculusCapacity);
        _sanctum = new Sanctum(
            trecenarius: _trecenarius,
            scriba: _scriba,
            oracleLogic: oracleDelegate,
            maxCohorts: _config.MaxCohorts,
            oracleIntervalMs: _config.OracleIntervalMs,
            cpuMonitorIntervalMs: _config.CpuMonitorIntervalMs,
            diagnosticSink: _config.DiagnosticSink);

        _phaseRing = new PhaseFrame[EpochRingSize];
        for (int i = 0; i < EpochRingSize; i++)
        {
            _phaseRing[i] = new PhaseFrame(i, _config.MaxCohorts, threadCount);
        }

        _centurions = new Centurio[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            _centurions[i] = new Centurio(
                id: i,
                legatus: this,
                trecenarius: _trecenarius,
                maxCohorts: _config.MaxCohorts,
                optioCapacity: _config.OptioCapacity,
                isBackgroundWorker: i > 0);
        }

        for (int i = 0; i < threadCount; i++)
        {
            _centurions[i].Enlist(_centurions);
        }

        for (int i = 1; i < threadCount; i++)
        {
            _centurions[i].Start();
        }

        _sanctum.Start();

        _tabularium = new Cohors[_config.MaxCohorts];
        _commanders = new IPriorPilus[_config.MaxCohorts];
        _ordo = new Cohors[_config.MaxCohorts];
        _consilium = new Gradus[_config.MaxCohorts];
        _phaseCounts = new int[_config.MaxCohorts];
        _phaseOffsets = new int[_config.MaxCohorts];
    }

    /// <summary>
    /// Enlists a new Cohort (System) into the Legion.
    /// Registration order is binding for resource hazards (RAW / WAR / WAW).
    /// </summary>
    public void RegisterCohors(Cohors cohors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cohors);

        if (_cohortCount >= _tabularium.Length)
            throw new InvalidOperationException("Tabularium is full. Increase LegatusConfig.MaxCohorts.");

        int slot = _cohortCount++;
        cohors.Slot = slot;
        _tabularium[slot] = cohors;
        _commanders[slot] = cohors.Commander;
    }

    /// <summary>
    /// Constructs the Battle Plan (Consilium) as a hazard-preserving DAG (RAW, WAR, WAW).
    /// Registration order is binding for any cohorts sharing a conflicting resource bit,
    /// guaranteeing zero deadlocks and exact sequential semantics while maximizing parallelism.
    /// </summary>
    public void ConstructConsilium()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _gradusCount = 0;
        if (_cohortCount == 0)
            return;

        Array.Clear(_phaseCounts, 0, _cohortCount);
        int maxPhase = 0;

        // 1. Compute earliest legal phase for each cohort respecting all earlier hazards (RAW/WAR/WAW).
        //    Since edges only go forward (j < i), the resulting dependency graph is strictly acyclic (0 deadlocks).
        for (int i = 0; i < _cohortCount; i++)
        {
            Cohors current = _tabularium[i];
            int earliestPhase = 0;

            for (int j = 0; j < i; j++)
            {
                Cohors prev = _tabularium[j];

                bool waw = prev.WriteMask.Confligere(in current.WriteMask);
                bool raw = prev.WriteMask.Confligere(in current.ReadMask);
                bool war = prev.ReadMask.Confligere(in current.WriteMask);

                if (waw || raw || war)
                {
                    int candidatePhase = prev.PhaseIndex + 1;
                    if (candidatePhase > earliestPhase)
                        earliestPhase = candidatePhase;
                }
            }

            current.PhaseIndex = earliestPhase;
            _phaseCounts[earliestPhase]++;
            if (earliestPhase > maxPhase)
                maxPhase = earliestPhase;
        }

        // 2. Pack cohorts into flat _ordo array grouped by phase (preserving registration order inside each phase).
        _gradusCount = maxPhase + 1;
        int offset = 0;
        for (int p = 0; p < _gradusCount; p++)
        {
            int count = _phaseCounts[p];
            _consilium[p] = new Gradus { StartIndex = offset, Count = count };
            _phaseOffsets[p] = offset;
            offset += count;
        }

        for (int i = 0; i < _cohortCount; i++)
        {
            Cohors c = _tabularium[i];
            int dst = _phaseOffsets[c.PhaseIndex]++;
            _ordo[dst] = c;
        }
    }

    /// <summary>
    /// Commands the Legion to execute one complete frame across all Consilium phases.
    /// Guarantees every task in phase p finishes before phase p+1 begins, and all tasks finish before returning.
    /// </summary>
    public void ExecuteFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long frameIndex = ++_frameCounter;
        int stride = _config.TelemetrySampleStride <= 1 ? 1 : _config.TelemetrySampleStride;
        bool recordTelemetry = (frameIndex % stride) == 0;
        Centurio primus = _centurions[0];

        for (int p = 0; p < _gradusCount; p++)
        {
            Gradus gradus = _consilium[p];
            if (gradus.Count == 0)
                continue;

            long nextEpoch = _phaseEpoch.Value + 1;
            int ringSlot = (int)(nextEpoch & EpochRingMask);

            // Safety guard: ensure no preempted worker still references this ring slot from 8 epochs ago.
            long staleEpoch = nextEpoch - EpochRingSize;
            if (staleEpoch > 0)
            {
                WaitForStaleEpochDrain(staleEpoch);
            }

            PhaseFrame frame = _phaseRing[ringSlot];
            int activeItemCount = 0;
            int maxActiveThreads = 1;
            long totalPhaseChunks = 0;

            for (int i = 0; i < gradus.Count; i++)
            {
                Cohors cohors = _ordo[gradus.StartIndex + i];
                int totalItems = cohors.Commander.GetTotalItemCount();
                if (totalItems <= 0)
                    continue;

                Strategia strat = _scriba.Legere(cohors.Slot);
                int activeThreads = Math.Clamp(strat.ThreadCount, 1, _centurions.Length);
                if (totalItems < activeThreads)
                    activeThreads = totalItems;

                int batchSize = Math.Clamp(strat.BatchSize, 1, totalItems);
                int totalChunks = (int)(((long)totalItems + batchSize - 1) / batchSize);

                frame.Items[activeItemCount++] = new CohortWorkItem
                {
                    CohortSlot = cohors.Slot,
                    CohortId = cohors.Id,
                    TotalItems = totalItems,
                    BatchSize = batchSize,
                    TotalChunks = totalChunks,
                    ActiveThreads = activeThreads
                };

                totalPhaseChunks += totalChunks;
                if (activeThreads > maxActiveThreads)
                    maxActiveThreads = activeThreads;
            }

            if (totalPhaseChunks == 0)
                continue;

            frame.ItemCount = activeItemCount;
            frame.MaxActiveThreads = maxActiveThreads;
            frame.RecordTelemetry = recordTelemetry;
            frame.CpuPressure = _sanctum.CurrentCpuPressure;

            for (int w = 0; w < maxActiveThreads; w++)
            {
                frame.InboxClaimed[w] = 0;
            }

            // 1. Publish phase work count and epoch with full memory barrier BEFORE waking workers.
            frame.SetRemainingTasks(totalPhaseChunks);
            Interlocked.Exchange(ref _phaseEpoch.Value, nextEpoch);

            // 2. Wake only the workers participating in this phase (Dekker check skips syscalls when spinning).
            for (int w = 1; w < maxActiveThreads; w++)
            {
                _centurions[w].WakeUpIfParked();
            }

            // 3. Primus joins the battle and enforces the exact phase barrier (frame.RemainingTasks == 0).
            primus.RunPhase(nextEpoch);
        }
    }

    /// <summary>
    /// Diagnostic helper: total chunks stolen across all Centurions since startup.
    /// </summary>
    public long GetTotalSteals()
    {
        long sum = 0;
        for (int i = 0; i < _centurions.Length; i++)
            sum += _centurions[i].TotalSteals;
        return sum;
    }

    /// <summary>
    /// Diagnostic helper: total chunks executed across all Centurions since startup.
    /// </summary>
    public long GetTotalTasksExecuted()
    {
        long sum = 0;
        for (int i = 0; i < _centurions.Length; i++)
            sum += _centurions[i].TotalTasksExecuted;
        return sum;
    }

    /// <summary>
    /// Diagnostic helper: returns the phase index assigned to the cohort at registration index <paramref name="slot"/>.
    /// </summary>
    public int GetCohortPhase(int slot) => _tabularium[slot].PhaseIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal IPriorPilus GetCommander(int cohortSlot) => _commanders[cohortSlot];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PhaseFrame GetPhaseFrame(long epoch) => _phaseRing[(int)(epoch & EpochRingMask)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PhaseFrame GetPhaseFrameBySlot(int slot) => _phaseRing[slot];

    private void WaitForStaleEpochDrain(long staleEpoch)
    {
        var spin = new SpinWait();
        for (int i = 1; i < _centurions.Length; i++)
        {
            while (_centurions[i].ActiveEpoch == staleEpoch)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sanctum.Dispose();

        for (int i = 1; i < _centurions.Length; i++)
        {
            _centurions[i].Dispose();
        }
        _centurions[0].Dispose();
    }

    // ==========================================
    // AUTO-WIRING LOGIC (REFLECTION)
    // ==========================================

    private OraculumBatchFunc LoadOraculum(int threadCount, out bool loadedHaruspex)
    {
        loadedHaruspex = false;
        try
        {
            var assemblyName = new AssemblyName("Legio.Haruspex");
            Assembly assembly = Assembly.Load(assemblyName);

            Type? oracleType = assembly.GetType("Legio.Haruspex.Oracle");
            MethodInfo? createMethod = oracleType?.GetMethod("createEngine", BindingFlags.Public | BindingFlags.Static);

            if (createMethod != null)
            {
                object? engine = createMethod.Invoke(
                    null,
                    new object[] { _config.MaxCohorts, _config.OracleModel, threadCount, _config.InitialBatchSize });

                if (engine != null)
                {
                    MethodInfo? evaluateMethod = engine.GetType().GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Instance);
                    if (evaluateMethod != null)
                    {
                        loadedHaruspex = true;
                        return (OraculumBatchFunc)Delegate.CreateDelegate(typeof(OraculumBatchFunc), engine, evaluateMethod);
                    }
                }
            }

            _config.DiagnosticSink?.Invoke("Legio.Haruspex loaded, but Oracle.createEngine/Evaluate was not found. Using fallback.");
        }
        catch (Exception ex)
        {
            _config.DiagnosticSink?.Invoke($"Failed to load Legio.Haruspex ({ex.GetType().Name}: {ex.Message}). Using fallback.");
        }

        return FallbackOraculum;
    }

    private void FallbackOraculum(
        in Topologia topo,
        float cpuPressure,
        ReadOnlySpan<Tessera> omens,
        Span<Strategia> strategiesOut,
        Span<byte> updatedMask)
    {
        int threads = Math.Clamp(_centurions?.Length ?? topo.LogicalCoreCount, 1, Math.Max(1, topo.LogicalCoreCount));
        var fallback = new Strategia
        {
            BatchSize = Math.Max(16, _config.InitialBatchSize),
            ThreadCount = threads,
            Confidence = 0.5f,
            AffinityHint = cpuPressure > 0.85f ? 1 : 0
        };

        int limit = Math.Min(strategiesOut.Length, updatedMask.Length);
        for (int i = 0; i < omens.Length; i++)
        {
            int c = omens[i].CohortId;
            if ((uint)c < (uint)limit && updatedMask[c] == 0)
            {
                strategiesOut[c] = fallback;
                updatedMask[c] = 1;
            }
        }
    }
}