using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Legio.Core.Augurium;
using Legio.Protocol;

namespace Legio.Core.Armarium;

/// <summary>
/// Circulus (The Ring).
/// A lock-free, zero-allocation Multi-Producer Single-Consumer (MPSC) ring buffer.
/// Used to stream Tesserae (metrics) from worker threads to the Trecenarius without blocking execution.
/// </summary>
public unsafe class Circulus
{
    // The raw buffer of Tesserae.
    private readonly Tessera[] _buffer;
    
    // Sequence numbers for each slot to handle the "ABA problem" and detect slot availability.
    // This ensures a writer doesn't overwrite a slot that hasn't been read yet.
    private readonly int[] _sequences;

    private readonly int _mask;
    private readonly int _capacity;

    // Cache-line padding to prevent False Sharing between Head and Tail.
    // Standard cache line is 64 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Padding { }

    [StructLayout(LayoutKind.Sequential)]
    private struct CacheLine
    {
        public volatile int Value;
        // Padding to ensure 'Value' takes up a full cache line alone
        private long _p1, _p2, _p3, _p4, _p5, _p6, _p7; 
    }

    private CacheLine _head; // Write Index (shared by producers)
#pragma warning disable CS0169 // Field is never used
    private Padding _pad1;   // Spacer to prevent False Sharing
#pragma warning restore CS0169
    private CacheLine _tail; // Read Index (owned by consumer)

    public Circulus(int capacity = 4096)
    {
        // Capacity must be a power of 2 for fast bitwise masking
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of 2");

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new Tessera[capacity];
        _sequences = new int[capacity];

        // Initialize sequence numbers
        for (int i = 0; i < capacity; i++)
        {
            _sequences[i] = i; // Initial sequence matches the index
        }

        _head.Value = 0;
        _tail.Value = 0;
    }

    /// <summary>
    /// Attempts to write a report into the ring.
    /// Returns false if the buffer is full (metrics are dropped to preserve performance).
    /// Safe for concurrent calls from multiple threads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in Tessera item)
    {
        int head, nextHead;
        
        // Optimistic concurrency loop
        do
        {
            head = _head.Value;
            nextHead = (head + 1); // We don't mask here, numbers grow indefinitely
            
            // Check if the slot matches the expected sequence for writing
            if (_sequences[head & _mask] != head)
            {
                // Buffer is full or slot is busy (consumer hasn't caught up)
                // In a high-perf simulation, we prefer dropping a metric over blocking a thread.
                return false; 
            }
        }
        // Try to reserve the slot
        while (Interlocked.CompareExchange(ref _head.Value, nextHead, head) != head);

        // We won the race for 'head'. Write the data.
        _buffer[head & _mask] = item;

        // Commit the write by updating the sequence number to allow reading.
        // We set it to (head + 1) which is what the consumer expects.
        Volatile.Write(ref _sequences[head & _mask], head + 1);
        
        return true;
    }

    /// <summary>
    /// Attempts to read a report from the ring.
    /// Only called by the Trecenarius (Single Consumer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out Tessera item)
    {
        int tail = _tail.Value;
        
        // Check if the sequence number at the tail matches the expected value (tail + 1)
        // This means the writer has finished writing and incremented the sequence.
        if (_sequences[tail & _mask] != (tail + 1))
        {
            item = default;
            return false; // Buffer empty or write in progress
        }

        // Read the data
        item = _buffer[tail & _mask];

        // Advance the tail locally (since we are single consumer)
        _tail.Value = tail + 1;

        // Reset sequence number to allow writing to this slot in the next cycle (tail + capacity)
        Volatile.Write(ref _sequences[tail & _mask], tail + _capacity);

        return true;
    }
}