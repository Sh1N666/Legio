using Legio.Core.Armarium;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Trecenarius (The Intelligence Officer).
/// Collects Tesserae from the MPSC Circulus ring buffer and drains the latest statistical window
/// for periodic Haruspex Oracle evaluation with zero heap allocations.
/// </summary>
public sealed class Trecenarius
{
    public readonly Circulus InputBuffer;

    private readonly Tessera[] _windowBuffer;
    private readonly int _windowCapacity;

    public Trecenarius(int bufferCapacity = 4096, int windowCapacity = 512)
    {
        InputBuffer = new Circulus(bufferCapacity);
        _windowCapacity = Math.Max(32, windowCapacity);
        _windowBuffer = new Tessera[_windowCapacity];
    }

    /// <summary>
    /// Drops a telemetry tablet into the lock-free ring buffer.
    /// </summary>
    public void Report(in Tessera tessera)
    {
        InputBuffer.TryWrite(in tessera);
    }

    /// <summary>
    /// Drains all currently queued reports from the ring buffer, retaining up to the most recent
    /// windowCapacity tablets so the Oracle always evaluates fresh data without stalling on stale backlog.
    /// Zero heap allocations.
    /// </summary>
    public ReadOnlySpan<Tessera> CollectOmens()
    {
        int totalRead = 0;

        while (InputBuffer.TryRead(out Tessera t))
        {
            if (totalRead < _windowCapacity)
            {
                _windowBuffer[totalRead] = t;
            }
            else
            {
                // Ring overwrite keeps the freshest tablets within the pre-allocated window.
                _windowBuffer[totalRead % _windowCapacity] = t;
            }

            totalRead++;
        }

        if (totalRead == 0)
            return ReadOnlySpan<Tessera>.Empty;

        int validCount = totalRead < _windowCapacity ? totalRead : _windowCapacity;
        return new ReadOnlySpan<Tessera>(_windowBuffer, 0, validCount);
    }
}