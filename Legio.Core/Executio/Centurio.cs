using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Protocol;

namespace Legio.Core.Executio;

/// <summary>
/// Centurio (The Commander of 100).
/// Worker executing local Optio chunks (LIFO) and stealing work from peer Centurions (FIFO)
/// with Dekker-fenced parking and zero-allocation aggregated phase telemetry.
/// </summary>
public sealed class Centurio : IDisposable
{
    private const int SpinIterationsBeforePark = 1024;

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
    private readonly int[] _cohortItems;
    private readonly int[] _touchedSlots;
    private int _touchedCount;

    private Centurio[] _legion = null!;
    private uint _rngState;
    private long _lastCompletedEpoch;

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
        _cohortItems = new int[maxCohorts];
        _touchedSlots = new int[maxCohorts];
        _rngState = (uint)(id * 747796405 + 2891336453) | 1u;
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
            Volatile.Write(ref _activeEpoch.Value, -1);
            return;
        }

        bool recordTelemetry = frame.RecordTelemetry;
        int phaseSteals = 0;
        int phaseContentions = 0;
        _touchedCount = 0;

        // 1. HYBRID INITIAL DISTRIBUTION:
        // Claim our contiguous initial slice and push chunks into our own Optio (strict single-owner Push).
        if (Interlocked.CompareExchange(ref frame.InboxClaimed[Id], 1, 0) == 0)
        {
            EnqueueWorkerSlice(frame, Id, recordTelemetry);
        }

        // 2. EXECUTION + WORK STEALING LOOP (exact phase barrier: exits only when frame.RemainingTasks == 0).
        var spin = new SpinWait();
        while (frame.RemainingTasks > 0)
        {
            // A. Primary: drain our local Optio (LIFO -> sequential forward memory traversal).
            if (Optio.TryPop(out TaskUnit localTask))
            {
                ExecuteChunk(in localTask, recordTelemetry);
                spin.Reset();
                continue;
            }

            // B. Secondary: steal from peer Centurions' Optio deques (FIFO -> opposite end of victim slice).
            if (TryStealFromLegion(maxActive, out TaskUnit stolenTask, ref phaseContentions))
            {
                phaseSteals++;
                Interlocked.Increment(ref _totalSteals);
                ExecuteChunk(in stolenTask, recordTelemetry);
                spin.Reset();
                continue;
            }

            // C. Fallback: steal an unclaimed initial inbox from a peer delayed by OS wakeup jitter.
            if (TryStealUnclaimedInbox(frame, maxActive, recordTelemetry))
            {
                phaseSteals++;
                Interlocked.Increment(ref _totalSteals);
                spin.Reset();
                continue;
            }

            // D. All queues are empty, waiting for peer threads to complete in-flight chunks.
            //    sleep1Threshold: -1 prevents SpinWait from triggering a 1ms OS sleep mid-phase.
            if (frame.RemainingTasks > 0)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        // 3. AGGREGATED TELEMETRY (at most 1 Tessera per touched cohort per sampled phase).
        if (recordTelemetry && _touchedCount > 0)
        {
            EmitPhaseTelemetry(frame, phaseSteals, phaseContentions);
        }

        if (phaseContentions > 0)
        {
            Interlocked.Add(ref _totalContentions, phaseContentions);
        }

        Volatile.Write(ref _activeEpoch.Value, -1);
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

            int chunksPerWorker = item.TotalChunks / item.ActiveThreads;
            int extra = item.TotalChunks % item.ActiveThreads;
            int firstChunk = targetWorkerId * chunksPerWorker + Math.Min(targetWorkerId, extra);
            int myChunks = chunksPerWorker + (targetWorkerId < extra ? 1 : 0);

            if (myChunks <= 0)
                continue;

            int batch = item.BatchSize;
            int total = item.TotalItems;
            int cohortSlot = item.CohortSlot;

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
                    // Optio full: execute inline immediately so no chunk is ever lost.
                    ExecuteChunk(in unit, recordTelemetry);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteChunk(in TaskUnit task, bool recordTelemetry)
    {
        IPriorPilus commander = _legatus.GetCommander(task.CohortSlot);

        if (recordTelemetry)
        {
            long startTicks = Stopwatch.GetTimestamp();
            commander.ExecuteBatch(task.Start, task.Length);
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            AccumulateTelemetry(task.CohortSlot, elapsed, task.Length);
        }
        else
        {
            commander.ExecuteBatch(task.Start, task.Length);
        }

        _totalTasksExecuted++;
        _legatus.GetPhaseFrameBySlot(task.PhaseSlot).DecrementRemainingTasks();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AccumulateTelemetry(int cohortSlot, long elapsedTicks, int itemCount)
    {
        if (_cohortItems[cohortSlot] == 0)
        {
            _touchedSlots[_touchedCount++] = cohortSlot;
        }

        _cohortTicks[cohortSlot] += elapsedTicks;
        _cohortItems[cohortSlot] += itemCount;
    }

    private void EmitPhaseTelemetry(PhaseFrame frame, int phaseSteals, int phaseContentions)
    {
        float cpuPressure = frame.CpuPressure;
        int frameItemCount = frame.ItemCount;

        for (int i = 0; i < _touchedCount; i++)
        {
            int slot = _touchedSlots[i];
            int items = _cohortItems[slot];
            long ticks = _cohortTicks[slot];

            _cohortItems[slot] = 0;
            _cohortTicks[slot] = 0;

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
                ItemCount = items,
                TotalCohortItems = totalCohortItems,
                CenturioId = Id,
                CohortId = slot,
                StealCount = phaseSteals,
                ContentionCount = phaseContentions,
                CpuPressure = cpuPressure
            };

            _trecenarius.Report(in tessera);
        }

        _touchedCount = 0;
    }

    private bool TryStealFromLegion(int maxActive, out TaskUnit stolenTask, ref int contentions)
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
                    return true;

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

            if (Volatile.Read(ref frame.InboxClaimed[victimId]) == 0 &&
                Interlocked.CompareExchange(ref frame.InboxClaimed[victimId], 1, 0) == 0)
            {
                EnqueueWorkerSlice(frame, victimId, recordTelemetry);
                return true;
            }
        }

        return false;
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

            // Short adaptive spin so back-to-back phases / frames avoid OS futex latency.
            for (int spin = 0; spin < SpinIterationsBeforePark; spin++)
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
}