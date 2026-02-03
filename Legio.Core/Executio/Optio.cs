using System.Runtime.CompilerServices;

namespace Legio.Core.Executio;

/// <summary>
/// Optio (The Deputy).
/// Manages the local task queue for a Centurion.
/// Implements a lock-free Work Stealing Deque (Double-Ended Queue).
/// </summary>
public unsafe class Optio
{
    // The actual buffer of tasks. Power of 2 size.
    private readonly TaskUnit[] _buffer;
    private readonly int _mask;

    // Volatile indices for lock-free synchronization
    private volatile int _head; // Written by Owner (Push/Pop)
    private volatile int _tail; // Written by Thieves (Steal)

    public Optio(int capacity = 4096)
    {
        _buffer = new TaskUnit[capacity];
        _mask = capacity - 1;
        _head = 0;
        _tail = 0;
    }

    /// <summary>
    /// Adds a new task to the local queue (LIFO).
    /// Only called by the Owner (Centurion).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(TaskUnit task)
    {
        int head = _head;
        _buffer[head & _mask] = task;
        
        // Memory fence to ensure the task is visible before incrementing head
        Interlocked.MemoryBarrier(); 
        
        _head = head + 1;
    }

    /// <summary>
    /// Takes a task from the local queue (LIFO).
    /// Only called by the Owner (Centurion).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out TaskUnit task)
    {
        int head = _head - 1;
        Interlocked.Exchange(ref _head, head); // Decrement head atomically

        int tail = _tail;
        if (head < tail)
        {
            // Queue is empty
            _head = tail;
            task = default;
            return false;
        }

        task = _buffer[head & _mask];

        // Check for race condition with a thief
        if (head == tail)
        {
            int tailNext = tail + 1;
            // Try to increment tail to win against a thief
            if (Interlocked.CompareExchange(ref _tail, tailNext, tail) != tail)
            {
                // Lost the race to a thief (FAILED POP)
                _head = tail + 1; // Restore head
                task = default;
                return false;
            }
            // Won the race, queue is now empty and consistent
            _head = tail + 1; 
        }

        return true;
    }

    /// <summary>
    /// Steals a task from the other end of the queue (FIFO).
    /// Called by other Centurions (Thieves).
    /// </summary>
    public bool TrySteal(out TaskUnit task)
    {
        int tail = _tail;
        Interlocked.MemoryBarrier(); // Ensure we see the latest head
        int head = _head;

        if (head <= tail)
        {
            task = default;
            return false; // Empty
        }

        // Speculatively read the task
        task = _buffer[tail & _mask];

        // Try to advance tail (commit steal)
        if (Interlocked.CompareExchange(ref _tail, tail + 1, tail) != tail)
        {
            return false; // Lost race to another thief or the owner
        }

        return true;
    }
    
    /// <summary>
    /// Checks if the queue has any work locally.
    /// </summary>
    public bool HasWork => _head > _tail;
}