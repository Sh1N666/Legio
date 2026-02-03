using System.Runtime.CompilerServices;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Scriba (The Scribe).
/// Acts as the atomic bridge between the Execution Layer (C#) and the Oracle (F#).
/// Uses a Double-Buffering technique to allow lock-free reading of the current strategy.
/// </summary>
public unsafe class Scriba
{
    // Backing store for strategies.
    // Index 0 and 1 are swapped atomically.
    private readonly Strategia[] _buffer;
    
    // Points to the currently active strategy index (0 or 1).
    private volatile int _activeIndex;

    public Scriba()
    {
        _buffer = new Strategia[2];
        _activeIndex = 0;

        // Default conservative strategy to ensure safety before the first Oracle prediction.
        var defaultStrat = new Strategia
        {
            BatchSize = 1024,
            ThreadCount = Environment.ProcessorCount,
            Confidence = 0.5f,
            AffinityHint = 0
        };

        _buffer[0] = defaultStrat;
        _buffer[1] = defaultStrat;
    }

    /// <summary>
    /// Reads the current Battle Strategy.
    /// Called frequently by Prior Pilus (hot path).
    /// Zero allocation, wait-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Strategia Legere()
    {
        // Simple array access is atomic enough for reading a struct in this context
        // as long as we trust the index.
        return _buffer[_activeIndex];
    }

    /// <summary>
    /// Publishes a new strategy from the Oracle.
    /// Called by the Trecenarius/Sancti thread (cold path).
    /// </summary>
    public void Scribere(Strategia novaStrategia)
    {
        // Determine the "back" buffer (the one NOT currently being read)
        int backBufferIndex = (_activeIndex + 1) & 1; // Toggle 0 <-> 1

        // Write the new data to the back buffer
        _buffer[backBufferIndex] = novaStrategia;

        // Atomic swap: Point active index to the new buffer.
        // MemoryBarrier is implicitly handled by Interlocked or Volatile Write in .NET Core.
        // We use Exchange to ensure immediate visibility.
        Interlocked.Exchange(ref _activeIndex, backBufferIndex);
    }
}