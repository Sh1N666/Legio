using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Scriba (The Scribe).
/// Lock-free, zero-allocation per-Cohort strategy table using a SeqLock per 64-byte padded slot.
/// Guarantees 100% tear-free reads of the 16-byte Strategia struct without locks or heap allocations.
/// </summary>
public sealed class Scriba
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct StrategySlot
    {
        // Even = stable, Odd = write in progress.
        [FieldOffset(0)]
        public long Sequence;

        [FieldOffset(8)]
        public Strategia Value;
    }

    private readonly StrategySlot[] _slots;

    public int Capacity => _slots.Length;

    public Scriba(int maxCohorts = 64, int defaultBatchSize = 1024, int defaultThreadCount = 0)
    {
        if (maxCohorts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCohorts), "MaxCohorts must be positive.");

        int threads = defaultThreadCount > 0 ? defaultThreadCount : Math.Max(1, Environment.ProcessorCount);
        int batch = Math.Max(16, defaultBatchSize);

        var defaultStrategy = new Strategia
        {
            BatchSize = batch,
            ThreadCount = threads,
            Confidence = 0.5f,
            AffinityHint = 0,
            SpinIterationsBeforePark = 1024,
            PartitionMode = PartitionMode.Contiguous,
            StealMode = StealMode.StealOne
        };

        _slots = new StrategySlot[maxCohorts];
        for (int i = 0; i < maxCohorts; i++)
        {
            _slots[i].Sequence = 0;
            _slots[i].Value = defaultStrategy;
        }
    }

    /// <summary>
    /// Reads the current strategy for a specific cohort slot (wait-free under no concurrent write,
    /// guaranteed tear-free via SeqLock, zero allocations).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Strategia Legere(int cohortSlot = 0)
    {
        if ((uint)cohortSlot >= (uint)_slots.Length)
            cohortSlot = 0;

        ref StrategySlot slot = ref _slots[cohortSlot];

        while (true)
        {
            long seq1 = Volatile.Read(ref slot.Sequence);
            if ((seq1 & 1L) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            Strategia copy = slot.Value;

            // Prevent reading seq2 before copying all 16 bytes of Strategia (critical on ARM64 and JIT).
            Interlocked.MemoryBarrier();
            long seq2 = Volatile.Read(ref slot.Sequence);

            if (seq1 == seq2)
                return copy;
        }
    }

    /// <summary>
    /// Publishes a new strategy for a specific cohort slot. Single writer (Sanctum thread).
    /// </summary>
    public void Scribere(int cohortSlot, in Strategia novaStrategia)
    {
        if ((uint)cohortSlot >= (uint)_slots.Length)
            return;

        ref StrategySlot slot = ref _slots[cohortSlot];
        long seq = slot.Sequence;

        // Odd sequence signals write-in-progress with full barrier before mutating Value.
        Interlocked.Exchange(ref slot.Sequence, seq + 1);
        slot.Value = novaStrategia;

        // Release store to even sequence makes the new Value atomically visible.
        Volatile.Write(ref slot.Sequence, seq + 2);
    }

    /// <summary>
    /// Broadcasts a strategy to all slots (compatibility helper for global fallback).
    /// </summary>
    public void ScribereAll(in Strategia novaStrategia)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Scribere(i, in novaStrategia);
        }
    }
}