using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Protocol;

namespace Legio.Core.Executio;

/// <summary>
/// Centurio (The Commander of 100).
/// Worker executing local Optio chunks (LIFO) and stealing work from peer Centurions (FIFO)
/// with Dekker-fenced parking, adaptive PartitionMode (Contiguous/Interleaved),
/// adaptive StealMode (StealOne/StealHalf), OS thread affinity pinning, adaptive spin budget,
/// and zero-allocation aggregated phase telemetry (including BarrierWaitTicks and spatial half-ticks).
/// </summary>
public sealed unsafe class Centurio : IDisposable
{
    private const int DefaultSpinIterationsBeforePark = 1024;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)]
        public long Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedInt
    {
        [FieldOffset(0)]
        public int Value;
    }

    public readonly int Id;
    public readonly Optio Optio;

    private readonly Legatus _legatus;
    private readonly Trecenarius _trecenarius;
    private readonly Thread? _thread;
    private readonly AutoResetEvent? _wakeEvent;
    private readonly bool _isBackgroundWorker;

    // Pre-allocated per-phase telemetry scratch buffers (zero heap allocation in hot path).
    private readonly long[] _cohortTicks;
    private readonly long[] _cohortFirstHalfTicks;
    private readonly long[] _cohortSecondHalfTicks;
    private readonly int[] _cohortItems;
    private readonly int[] _touchedSlots;
    private int _touchedCount;

    private Centurio[] _legion = null!;
    private uint _rngState;
    private long _lastCompletedEpoch;
    private int _appliedAffinityHint;
    private volatile int _spinIterationsBeforePark;

    private PaddedLong _activeEpoch;
    private PaddedInt _isParked;
    private volatile bool _isRunning;
    private bool _disposed;

    // Diagnostic counters (useful for verifying work stealing in tests and benchmarks).
    private long _totalTasksExecuted;
    private long _totalSteals;
    private long _totalContentions;

    public long TotalTasksExecuted => Volatile.Read(ref _totalTasksExecuted);
    public long TotalSteals => Volatile.Read(ref _totalSteals);
    public long TotalContentions => Volatile.Read(ref _totalContentions);
    internal long ActiveEpoch => Volatile.Read(ref _activeEpoch.Value);

    internal Centurio(
        int id,
        Legatus legatus,
        Trecenarius trecenarius,
        int maxCohorts,
        int optioCapacity,
        bool isBackgroundWorker)
    {
        Id = id;
        _legatus = legatus;
        _trecenarius = trecenarius;
        _isBackgroundWorker = isBackgroundWorker;
        Optio = new Optio(optioCapacity);

        _cohortTicks = new long[maxCohorts];
        _cohortFirstHalfTicks = new long[maxCohorts];
        _cohortSecondHalfTicks = new long[maxCohorts];
        _cohortItems = new int[maxCohorts];
        _touchedSlots = new int[maxCohorts];
        _rngState = (uint)(id * 747796405 + 2891336453) | 1u;
        _appliedAffinityHint = (int)AffinityMode.None;
        _spinIterationsBeforePark = DefaultSpinIterationsBeforePark;
        _activeEpoch.Value = -1;
        _isParked.Value = 0;
        _isRunning = true;

        if (isBackgroundWorker)
        {
            _wakeEvent = new AutoResetEvent(false);
            _thread = new Thread(MilitiaLoop)
            {
                Name = $"Centurio_{id}",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
        }
    }

    internal void Enlist(Centurio[] legion)
    {
        _legion = legion;
    }

    internal void Start()
    {
        _thread?.Start();
    }

    /// <summary>
    /// Wakes the worker only if it is parked in the OS kernel (Dekker pair with MilitiaLoop).
    /// Avoids kernel futex overhead during rapid back-to-back phases.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WakeUpIfParked()
    {
        if (Volatile.Read(ref _isParked.Value) != 0)
        {
            _wakeEvent?.Set();
        }
    }

    /// <summary>
    /// Executes one execution phase (Gradus) identified by a monotonic epoch.
    /// Called by Primus directly and by background workers inside MilitiaLoop.
    /// </summary>
    internal void RunPhase(long epoch)
    {
        // Announce active epoch with full barrier BEFORE inspecting the ring slot.
        Interlocked.Exchange(ref _activeEpoch.Value, epoch);

        // Guard against extreme OS preemption where this epoch has already been superseded.
        if (_legatus.CurrentEpoch != epoch)
        {
            Volatile.Write(ref _activeEpoch.Value, -1);
            return;
        }

        PhaseFrame frame = _legatus.GetPhaseFrame(epoch);
        int maxActive = frame.MaxActiveThreads;

        // Honor the ML model's ThreadCount: idle workers do not steal from phases with fewer active threads.
        if (Id >= maxActive)
        {
            DrainResidualOptio();
            Volatile.Write(ref _activeEpoch.Value, -1);
            return;
        }

        // Option B.4: Update adaptive spin budget when changed.
        int frameSpin = frame.SpinIterationsBeforePark;
        if (frameSpin > 0 && frameSpin != _spinIterationsBeforePark)
        {
            _spinIterationsBeforePark = Math.Clamp(frameSpin, 64, 4096);
        }

        // Option B.3: Apply OS thread affinity pinning only on background workers and ONLY when hint changes.
        if (_isBackgroundWorker && frame.AffinityHint != _appliedAffinityHint)
        {
            ApplyThreadAffinity(frame.AffinityHint);
            _appliedAffinityHint = frame.AffinityHint;
        }

        bool recordTelemetry = frame.RecordTelemetry;
        StealMode stealMode = frame.StealMode;
        int userSpinLimit = Math.Clamp(_spinIterationsBeforePark >> 4, 8, 128);
        int phaseSteals = 0;
        int phaseContentions = 0;
        long phaseBarrierWaitTicks = 0;
        long waitStartTicks = 0;
        _touchedCount = 0;

        // 1. HYBRID INITIAL DISTRIBUTION (Option B.1: Contiguous or Interleaved):
        // Claim our initial slice and push chunks into our own Optio (strict single-owner Push).
        if (Interlocked.CompareExchange(ref frame.InboxClaimed[Id].Value, 1, 0) == 0)
        {
            EnqueueWorkerSlice(frame, Id, recordTelemetry);
        }

        // 2. EXECUTION + WORK STEALING LOOP (exact phase barrier: exits only when frame.RemainingTasks == 0).
        var spin = new SpinWait();
        int emptyPolls = 0;
        while (frame.RemainingTasks > 0)
        {
            // A. Primary: drain our local Optio (LIFO -> sequential forward memory traversal).
            if (Optio.TryPop(out TaskUnit localTask))
            {
                if (waitStartTicks != 0)
                {
                    phaseBarrierWaitTicks += Stopwatch.GetTimestamp() - waitStartTicks;
                    waitStartTicks = 0;
                }

                ExecuteChunk(in localTask, recordTelemetry && localTask.PhaseSlot == frame.Slot);
                emptyPolls = 0;
                spin.Reset();
                continue;
            }

            // B. Secondary: steal from peer Centurions' Optio deques (Option B.2: StealOne or StealHalf).
            if (TryStealFromLegion(maxActive, stealMode, out TaskUnit stolenTask, ref phaseContentions, ref phaseSteals, frame, recordTelemetry))
            {
                if (waitStartTicks != 0)
                {
                    phaseBarrierWaitTicks += Stopwatch.GetTimestamp() - waitStartTicks;
                    waitStartTicks = 0;
                }

                ExecuteChunk(in stolenTask, recordTelemetry && stolenTask.PhaseSlot == frame.Slot);
                emptyPolls = 0;
                spin.Reset();
                continue;
            }

            // C. Fallback: steal an unclaimed initial inbox from a peer delayed by OS wakeup jitter.
            if (TryStealUnclaimedInbox(frame, maxActive, recordTelemetry))
            {
                if (waitStartTicks != 0)
                {
                    phaseBarrierWaitTicks += Stopwatch.GetTimestamp() - waitStartTicks;
                    waitStartTicks = 0;
                }

                phaseSteals++;
                _totalSteals++;
                emptyPolls = 0;
                spin.Reset();
                continue;
            }

            // D. All queues are empty, waiting at phase barrier for straggler peers (Option A.1 telemetry).
            if (frame.RemainingTasks > 0)
            {
                if (recordTelemetry && waitStartTicks == 0)
                {
                    waitStartTicks = Stopwatch.GetTimestamp();
                }

                if (emptyPolls < userSpinLimit)
                {
                    emptyPolls++;
                    Thread.SpinWait(16);
                }
                else
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }
        }

        // Invariant: NEVER exit a phase leaving any task (e.g., cross-epoch stolen chunk) stranded in our local Optio.
        DrainResidualOptio();

        if (waitStartTicks != 0)
        {
            phaseBarrierWaitTicks += Stopwatch.GetTimestamp() - waitStartTicks;
        }

        // 3. AGGREGATED TELEMETRY (at most 1 Tessera per touched cohort per sampled phase).
        if (recordTelemetry && _touchedCount > 0)
        {
            EmitPhaseTelemetry(frame, phaseSteals, phaseContentions, phaseBarrierWaitTicks);
        }

        if (phaseContentions > 0)
        {
            _totalContentions += phaseContentions;
        }

        Volatile.Write(ref _activeEpoch.Value, -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrainResidualOptio()
    {
        while (Optio.TryPop(out TaskUnit strayTask))
        {
            ExecuteChunk(in strayTask, recordTelemetry: false);
        }
    }

    private void EnqueueWorkerSlice(PhaseFrame frame, int targetWorkerId, bool recordTelemetry)
    {
        int phaseSlot = frame.Slot;
        int itemCount = frame.ItemCount;

        for (int i = 0; i < itemCount; i++)
        {
            ref readonly CohortWorkItem item = ref frame.Items[i];
            if (targetWorkerId >= item.ActiveThreads)
                continue;

            int batch = item.BatchSize;
            int total = item.TotalItems;
            int totalChunks = item.TotalChunks;
            int activeThreads = item.ActiveThreads;
            int cohortSlot = item.CohortSlot;

            if (item.PartitionMode == PartitionMode.Interleaved)
            {
                // Option B.1 (Interleaved/Strided): chunk c = targetWorkerId + k * activeThreads.
                // Every chunk [0..totalChunks-1] maps bijectively to (c % activeThreads, c / activeThreads).
                // Pushed in reverse k so owner's LIFO pops walk ascending memory indices.
                if (targetWorkerId >= totalChunks)
                    continue;

                int lastK = (totalChunks - 1 - targetWorkerId) / activeThreads;
                for (int k = lastK; k >= 0; k--)
                {
                    int c = targetWorkerId + k * activeThreads;
                    int start = c * batch;
                    int remaining = total - start;
                    int len = remaining < batch ? remaining : batch;

                    var unit = new TaskUnit
                    {
                        CohortSlot = cohortSlot,
                        Start = start,
                        Length = len,
                        PhaseSlot = phaseSlot
                    };

                    if (!Optio.TryPush(in unit))
                    {
                        ExecuteChunk(in unit, recordTelemetry);
                    }
                }
            }
            else
            {
                // Option B.1 (Contiguous): contiguous slice per worker for spatial cache locality.
                int chunksPerWorker = totalChunks / activeThreads;
                int extra = totalChunks % activeThreads;
                int firstChunk = targetWorkerId * chunksPerWorker + Math.Min(targetWorkerId, extra);
                int myChunks = chunksPerWorker + (targetWorkerId < extra ? 1 : 0);

                if (myChunks <= 0)
                    continue;

                // Push in reverse order so owner's LIFO pops walk forward sequentially through memory,
                // while thieves stealing FIFO start at the highest-index chunks (maximizing cache separation).
                for (int c = firstChunk + myChunks - 1; c >= firstChunk; c--)
                {
                    int start = c * batch;
                    int remaining = total - start;
                    int len = remaining < batch ? remaining : batch;

                    var unit = new TaskUnit
                    {
                        CohortSlot = cohortSlot,
                        Start = start,
                        Length = len,
                        PhaseSlot = phaseSlot
                    };

                    if (!Optio.TryPush(in unit))
                    {
                        ExecuteChunk(in unit, recordTelemetry);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteChunk(in TaskUnit task, bool recordTelemetry)
    {
        IPriorPilus commander = _legatus.GetCommander(task.CohortSlot);
        PhaseFrame ownerFrame = _legatus.GetPhaseFrameBySlot(task.PhaseSlot);

        if (recordTelemetry)
        {
            long startTicks = Stopwatch.GetTimestamp();
            commander.ExecuteBatch(task.Start, task.Length);
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            bool isFirstHalf = task.Start < ownerFrame.CohortHalfBoundary[task.CohortSlot];
            AccumulateTelemetry(task.CohortSlot, elapsed, task.Length, isFirstHalf);
        }
        else
        {
            commander.ExecuteBatch(task.Start, task.Length);
        }

        _totalTasksExecuted++;
        ownerFrame.DecrementRemainingTasks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AccumulateTelemetry(int cohortSlot, long elapsedTicks, int itemCount, bool isFirstHalf)
    {
        if (_cohortItems[cohortSlot] == 0)
        {
            _touchedSlots[_touchedCount++] = cohortSlot;
        }

        _cohortTicks[cohortSlot] += elapsedTicks;
        _cohortItems[cohortSlot] += itemCount;

        if (isFirstHalf)
            _cohortFirstHalfTicks[cohortSlot] += elapsedTicks;
        else
            _cohortSecondHalfTicks[cohortSlot] += elapsedTicks;
    }

    private void EmitPhaseTelemetry(PhaseFrame frame, int phaseSteals, int phaseContentions, long phaseBarrierWaitTicks)
    {
        float cpuPressure = frame.CpuPressure;
        int frameItemCount = frame.ItemCount;
        int activeWorkers = frame.MaxActiveThreads;
        long waitPerCohort = _touchedCount > 1 ? phaseBarrierWaitTicks / _touchedCount : phaseBarrierWaitTicks;

        for (int i = 0; i < _touchedCount; i++)
        {
            int slot = _touchedSlots[i];
            int items = _cohortItems[slot];
            long ticks = _cohortTicks[slot];
            long firstHalf = _cohortFirstHalfTicks[slot];
            long secondHalf = _cohortSecondHalfTicks[slot];

            _cohortItems[slot] = 0;
            _cohortTicks[slot] = 0;
            _cohortFirstHalfTicks[slot] = 0;
            _cohortSecondHalfTicks[slot] = 0;

            if (items <= 0)
                continue;

            int totalCohortItems = items;
            for (int k = 0; k < frameItemCount; k++)
            {
                if (frame.Items[k].CohortSlot == slot)
                {
                    totalCohortItems = frame.Items[k].TotalItems;
                    break;
                }
            }

            var tessera = new Tessera
            {
                DurationTicks = ticks,
                BarrierWaitTicks = waitPerCohort,
                FirstHalfTicks = firstHalf,
                SecondHalfTicks = secondHalf,
                ItemCount = items,
                TotalCohortItems = totalCohortItems,
                CenturioId = Id,
                CohortId = slot,
                StealCount = phaseSteals,
                ContentionCount = phaseContentions,
                CpuPressure = cpuPressure,
                ActiveWorkers = activeWorkers
            };

            _trecenarius.Report(in tessera);
        }

        _touchedCount = 0;
    }

    private bool TryStealFromLegion(
        int maxActive,
        StealMode stealMode,
        out TaskUnit stolenTask,
        ref int contentions,
        ref int phaseSteals,
        PhaseFrame frame,
        bool recordTelemetry)
    {
        if (maxActive <= 1 || _legion == null)
        {
            stolenTask = default;
            return false;
        }

        int startVictim = NextVictim(maxActive);
        for (int i = 0; i < maxActive; i++)
        {
            int victimId = startVictim + i;
            if (victimId >= maxActive) victimId -= maxActive;
            if (victimId == Id) continue;

            Optio victimOptio = _legion[victimId].Optio;

            // Retry briefly on CAS contention instead of abandoning a non-empty victim deque.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                StealResult result = victimOptio.TrySteal(out stolenTask);
                if (result == StealResult.Success)
                {
                    phaseSteals++;
                    _totalSteals++;

                    // Option B.2 (StealHalf): amortize CAS overhead by pulling up to half of victim's remaining
                    // backlog (capped at 7 extra chunks) into our empty local Optio deque.
                    // CRITICAL: only batch-steal chunks belonging to our exact current phase (frame.Slot) so a
                    // late worker from epoch E can never stash epoch E+1 chunks into its local Optio and park!
                    if (stealMode == StealMode.StealHalf && stolenTask.PhaseSlot == frame.Slot)
                    {
                        int victimRemaining = victimOptio.ApproximateCount;
                        int extraTarget = Math.Min(7, victimRemaining >> 1);
                        for (int e = 0; e < extraTarget; e++)
                        {
                            StealResult extraRes = victimOptio.TrySteal(out TaskUnit extraUnit);
                            if (extraRes == StealResult.Success)
                            {
                                phaseSteals++;
                                _totalSteals++;
                                if (extraUnit.PhaseSlot != frame.Slot)
                                {
                                    // Victim moved to a newer phase: complete this chunk immediately and stop batch-stealing.
                                    ExecuteChunk(in extraUnit, recordTelemetry: false);
                                    break;
                                }

                                if (!Optio.TryPush(in extraUnit))
                                {
                                    ExecuteChunk(in extraUnit, recordTelemetry);
                                    break;
                                }
                            }
                            else
                            {
                                if (extraRes == StealResult.Contended)
                                    contentions++;
                                break;
                            }
                        }
                    }

                    return true;
                }

                if (result == StealResult.Empty)
                    break;

                contentions++;
                Thread.SpinWait(4);
            }
        }

        stolenTask = default;
        return false;
    }

    private bool TryStealUnclaimedInbox(PhaseFrame frame, int maxActive, bool recordTelemetry)
    {
        if (maxActive <= 1)
            return false;

        int startVictim = NextVictim(maxActive);
        for (int i = 0; i < maxActive; i++)
        {
            int victimId = startVictim + i;
            if (victimId >= maxActive) victimId -= maxActive;

            if (Volatile.Read(ref frame.InboxClaimed[victimId].Value) == 0 &&
                Interlocked.CompareExchange(ref frame.InboxClaimed[victimId].Value, 1, 0) == 0)
            {
                EnqueueWorkerSlice(frame, victimId, recordTelemetry);
                return true;
            }
        }

        return false;
    }

    private void ApplyThreadAffinity(int affinityHint)
    {
        try
        {
            int logicalCores = Math.Max(1, Mensor.Result.LogicalCoreCount);

            if (OperatingSystem.IsLinux())
            {
                byte* mask = stackalloc byte[128];
                new Span<byte>(mask, 128).Clear();

                if (affinityHint == (int)AffinityMode.PhysicalCoresFirst)
                {
                    int cpu = Mensor.GetPreferredCpuId(Id);
                    if ((uint)cpu < 1024u)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }
                else if (affinityHint == (int)AffinityMode.PinnedAllLogical)
                {
                    int cpu = Id % logicalCores;
                    if ((uint)cpu < 1024u)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }
                else
                {
                    int count = Math.Min(logicalCores, 1024);
                    for (int cpu = 0; cpu < count; cpu++)
                        mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
                }

                LinuxSchedSetAffinity(0, 128, mask);
            }
            else if (OperatingSystem.IsWindows())
            {
                int limit = Math.Min(logicalCores, 64);
                ulong maskBits;
                if (affinityHint == (int)AffinityMode.PhysicalCoresFirst)
                {
                    int cpu = Mensor.GetPreferredCpuId(Id) % limit;
                    maskBits = 1UL << cpu;
                }
                else if (affinityHint == (int)AffinityMode.PinnedAllLogical)
                {
                    int cpu = Id % limit;
                    maskBits = 1UL << cpu;
                }
                else
                {
                    maskBits = limit == 64 ? ulong.MaxValue : ((1UL << limit) - 1UL);
                }

                SetThreadAffinityMask(GetCurrentThread(), (nuint)maskBits);
            }
        }
        catch
        {
            // Ignore affinity failures in restricted containers / cgroups
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextVictim(int bound)
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return (int)(x % (uint)bound);
    }

    private void MilitiaLoop()
    {
        while (_isRunning)
        {
            long currentEpoch = _legatus.CurrentEpoch;
            if (currentEpoch > _lastCompletedEpoch)
            {
                _lastCompletedEpoch = currentEpoch;
                RunPhase(currentEpoch);
                continue;
            }

            // Adaptive spin budget controlled by Haruspex Strategia.SpinIterationsBeforePark (Option B.4).
            int spinBudget = _spinIterationsBeforePark;
            for (int spin = 0; spin < spinBudget; spin++)
            {
                if (!_isRunning || _legatus.CurrentEpoch > _lastCompletedEpoch)
                    break;

                Thread.SpinWait(20);
            }

            if (!_isRunning)
                break;

            if (_legatus.CurrentEpoch > _lastCompletedEpoch)
                continue;

            // Dekker parking protocol: publish _isParked = 1 with full barrier BEFORE re-checking epoch.
            Interlocked.Exchange(ref _isParked.Value, 1);

            if (!_isRunning || _legatus.CurrentEpoch > _lastCompletedEpoch)
            {
                Volatile.Write(ref _isParked.Value, 0);
                continue;
            }

            _wakeEvent!.WaitOne();
            Volatile.Write(ref _isParked.Value, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isRunning = false;

        if (_isBackgroundWorker)
        {
            _wakeEvent?.Set();
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(2000);
            }
            _wakeEvent?.Dispose();
        }
    }

    [DllImport("libc", EntryPoint = "sched_setaffinity", ExactSpelling = true)]
    private static extern int LinuxSchedSetAffinity(int pid, nuint cpusetsize, void* mask);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);
}