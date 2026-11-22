using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Legio.Core.Executio;

/// <summary>
/// Distinguishes an empty deque from transient CAS contention during work stealing.
/// </summary>
public enum StealResult : byte
{
    Empty = 0,
    Success = 1,
    Contended = 2
}

/// <summary>
/// Optio (The Deputy).
/// Lock-free, zero-allocation Chase-Lev Work-Stealing Deque with 64-byte cache-line padding,
/// 64-bit monotonic indices (no 32-bit overflow/ABA), and explicit contention reporting.
/// </summary>
public sealed class Optio
{
    private readonly TaskUnit[] _buffer;
    private readonly long _mask;
    private readonly int _capacity;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)]
        public long Value;
    }

    private PaddedLong _head; // Bottom: mutated only by Owner (TryPush / TryPop)
    private PaddedLong _tail; // Top: mutated by Thieves (TrySteal) and Owner on last-item race

    public int Capacity => _capacity;

    public Optio(int capacity = 4096)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a power of 2 and at least 2.");

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new TaskUnit[capacity];
        _head.Value = 0;
        _tail.Value = 0;
    }

    /// <summary>
    /// Pushes a task onto the local end (LIFO). Owner thread only.
    /// Returns false if the deque is full (never silently overwrites unread slots).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(in TaskUnit task)
    {
        long head = _head.Value;
        long tail = Volatile.Read(ref _tail.Value);

        if (head - tail >= _capacity)
            return false;

        _buffer[(int)(head & _mask)] = task;

        // Release store: payload must be visible before _head becomes visible to thieves.
        Volatile.Write(ref _head.Value, head + 1);
        return true;
    }

    /// <summary>
    /// Pops a task from the local end (LIFO). Owner thread only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out TaskUnit task)
    {
        long head = _head.Value - 1;

        // Full memory fence (SeqCst): ensures decremented _head is visible before reading _tail.
        Interlocked.Exchange(ref _head.Value, head);

        long tail = Volatile.Read(ref _tail.Value);
        if (head < tail)
        {
            // Deque was already empty; restore _head to match _tail.
            Volatile.Write(ref _head.Value, tail);
            task = default;
            return false;
        }

        task = _buffer[(int)(head & _mask)];

        if (head > tail)
        {
            // Strictly more than one item existed; no thief can claim index `head`.
            return true;
        }

        // Exactly one item left (head == tail): compete with thieves via CAS on _tail.
        bool won = Interlocked.CompareExchange(ref _tail.Value, tail + 1, tail) == tail;

        // Regardless of outcome, the deque is now empty at tail + 1.
        Volatile.Write(ref _head.Value, tail + 1);

        if (!won)
        {
            task = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts to steal a task from the opposite end (FIFO). Safe for multiple concurrent thieves.
    /// Returns Contended when losing a CAS race on a non-empty deque so callers can retry instead of sleeping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StealResult TrySteal(out TaskUnit task)
    {
        long tail = Volatile.Read(ref _tail.Value);

        // Full fence: ensures _tail is loaded strictly before _head (pairs with TryPop's Interlocked.Exchange).
        Interlocked.MemoryBarrier();

        long head = Volatile.Read(ref _head.Value);
        if (head <= tail)
        {
            task = default;
            return StealResult.Empty;
        }

        // Speculatively copy blittable TaskUnit before CAS.
        TaskUnit candidate = _buffer[(int)(tail & _mask)];

        if (Interlocked.CompareExchange(ref _tail.Value, tail + 1, tail) != tail)
        {
            task = default;
            return StealResult.Contended;
        }

        task = candidate;
        return StealResult.Success;
    }

    /// <summary>
    /// Approximate check whether the deque currently contains work.
    /// </summary>
    public bool HasWork => Volatile.Read(ref _head.Value) > Volatile.Read(ref _tail.Value);

    /// <summary>
    /// Approximate item count currently in the deque.
    /// </summary>
    public int ApproximateCount
    {
        get
        {
            long diff = Volatile.Read(ref _head.Value) - Volatile.Read(ref _tail.Value);
            return diff > 0 ? (int)diff : 0;
        }
    }
}