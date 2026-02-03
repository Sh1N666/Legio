using Legio.Core.Armarium;
using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Trecenarius (The Intelligence Officer).
/// Responsible for collecting Tesserae from the field (Circulus)
/// and preparing the data for the Haruspex (Oracle).
/// </summary>
public class Trecenarius
{
    /// <summary>
    /// The input buffer where Centurions drop their reports.
    /// </summary>
    public readonly Circulus InputBuffer;

    // Temporary scratchpad to hold data before sending to F# 
    // (to avoid calling F# for every single item).
    private readonly Tessera[] _batchCache;
    private int _cacheCount;
    private const int BATCH_LIMIT = 64; // How many reports to aggregate before learning

    public Trecenarius(int bufferCapacity = 4096)
    {
        InputBuffer = new Circulus(bufferCapacity);
        _batchCache = new Tessera[BATCH_LIMIT];
        _cacheCount = 0;
    }

    /// <summary>
    /// Allows a Centurion to report progress.
    /// </summary>
    public void Report(in Tessera tessera)
    {
        // Fire and forget. If buffer full, we lose data, which is acceptable for metrics.
        InputBuffer.TryWrite(tessera);
    }

    /// <summary>
    /// The maintenance cycle called by the Sancti thread.
    /// Drains the ring buffer and prepares a batch for the Oracle.
    /// </summary>
    /// <returns>A span of collected reports, or Empty if not enough data.</returns>
    public ReadOnlySpan<Tessera> CollectOmens()
    {
        _cacheCount = 0;
        
        // Drain up to BATCH_LIMIT items from the ring buffer
        while (_cacheCount < BATCH_LIMIT && InputBuffer.TryRead(out Tessera t))
        {
            _batchCache[_cacheCount++] = t;
        }

        if (_cacheCount == 0)
        {
            return ReadOnlySpan<Tessera>.Empty;
        }

        return new ReadOnlySpan<Tessera>(_batchCache, 0, _cacheCount);
    }
}