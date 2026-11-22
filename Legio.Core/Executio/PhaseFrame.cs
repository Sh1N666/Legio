using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Legio.Core.Executio;

/// <summary>
/// Pre-allocated descriptor for one Cohort participating in an execution phase (Gradus).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CohortWorkItem
{
    public int CohortSlot;
    public int CohortId;
    public int TotalItems;
    public int BatchSize;
    public int TotalChunks;
    public int ActiveThreads;
}

/// <summary>
/// Pre-allocated phase frame stored in an 8-slot epoch ring inside Legatus.
/// Owns its own cache-line-padded RemainingTasks counter so consecutive phases never interfere.
/// </summary>
internal sealed class PhaseFrame
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)]
        public long Value;
    }

    public readonly CohortWorkItem[] Items;
    public readonly int[] InboxClaimed;
    public readonly int Slot;

    private PaddedLong _remainingTasks;

    public int ItemCount;
    public int MaxActiveThreads;
    public bool RecordTelemetry;
    public float CpuPressure;

    public PhaseFrame(int slot, int maxCohorts, int maxThreads)
    {
        Slot = slot;
        Items = new CohortWorkItem[maxCohorts];
        InboxClaimed = new int[maxThreads];
    }

    public long RemainingTasks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _remainingTasks.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetRemainingTasks(long count)
    {
        Volatile.Write(ref _remainingTasks.Value, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long DecrementRemainingTasks()
    {
        return Interlocked.Decrement(ref _remainingTasks.Value);
    }
}
