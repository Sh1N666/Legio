using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Protocol;

namespace Legio.Core.Armarium;

/// <summary>
/// Circulus (The Ring).
/// A lock-free, zero-allocation Multi-Producer Single-Consumer (MPSC) bounded ring buffer
/// based on Dmitry Vyukov's sequence-slot algorithm.
/// </summary>
public sealed class Circulus
{
    private readonly Tessera[] _buffer;
    private readonly long[] _sequences;
    private readonly long _mask;
    private readonly int _capacity;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)]
        public long Value;
    }

    private PaddedLong _head; // Shared by producers (isolated on its own 64-byte cache line)
    private PaddedLong _tail; // Owned by single consumer (isolated on its own 64-byte cache line)

    public int Capacity => _capacity;

    public Circulus(int capacity = 4096)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a power of 2 and at least 2.");

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new Tessera[capacity];
        _sequences = new long[capacity];

        for (int i = 0; i < capacity; i++)
        {
            _sequences[i] = i;
        }

        _head.Value = 0;
        _tail.Value = 0;
    }

    /// <summary>
    /// Attempts to write a telemetry tablet into the ring buffer.
    /// Returns false only when the ring is genuinely full (never on transient producer contention).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in Tessera item)
    {
        long head = Volatile.Read(ref _head.Value);

        while (true)
        {
            int slot = (int)(head & _mask);
            long seq = Volatile.Read(ref _sequences[slot]);
            long diff = seq - head;

            if (diff == 0)
            {
                long prev = Interlocked.CompareExchange(ref _head.Value, head + 1, head);
                if (prev == head)
                {
                    _buffer[slot] = item;
                    // Release fence: guarantees item payload is visible before sequence signals readiness.
                    Volatile.Write(ref _sequences[slot], head + 1);
                    return true;
                }

                head = prev;
            }
            else if (diff < 0)
            {
                // Buffer is full; drop metric rather than blocking a worker thread.
                return false;
            }
            else
            {
                // Another producer already claimed and committed this slot; reload head and retry.
                head = Volatile.Read(ref _head.Value);
            }
        }
    }

    /// <summary>
    /// Attempts to read a telemetry tablet from the ring buffer.
    /// Single-Consumer only (called exclusively by Trecenarius on the Sanctum thread).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out Tessera item)
    {
        long tail = _tail.Value;
        int slot = (int)(tail & _mask);

        // Acquire fence: pairs with producer's Volatile.Write so payload read cannot be reordered above this check.
        long seq = Volatile.Read(ref _sequences[slot]);
        if (seq != tail + 1)
        {
            item = default;
            return false;
        }

        item = _buffer[slot];
        _tail.Value = tail + 1;

        // Release fence: allows producers to reuse this slot in the next wrap-around cycle.
        Volatile.Write(ref _sequences[slot], tail + _capacity);
        return true;
    }
}