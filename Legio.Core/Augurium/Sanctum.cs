using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Zero-allocation batch delegate invoked periodically on the dedicated Sanctum thread.
/// Evaluates a time window of Tesserae and writes updated per-Cohort strategies into strategiesOut.
/// </summary>
public delegate void OraculumBatchFunc(
    in Topologia topo,
    float cpuPressure,
    ReadOnlySpan<Tessera> omens,
    Span<Strategia> strategiesOut,
    Span<byte> updatedMask);

/// <summary>
/// Sanctum (The Sanctuary).
/// Runs the Haruspex ML model on a dedicated, lowest-priority background thread at a configurable
/// periodic interval so telemetry aggregation and ML inference never stall worker Centurions.
/// </summary>
public sealed class Sanctum : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopSignal;
    private readonly Trecenarius _trecenarius;
    private readonly Scriba _scriba;
    private readonly Speculator _speculator;
    private readonly OraculumBatchFunc _oracleLogic;
    private readonly int _intervalMs;
    private readonly Action<string>? _diagnosticSink;

    // Pre-allocated output buffers for per-cohort strategy updates (zero alloc per ritual cycle).
    private readonly Strategia[] _strategyScratch;
    private readonly byte[] _updatedMask;

    private volatile bool _isRunning;
    private long _ritualCount;
    private bool _disposed;

    public float CurrentCpuPressure => _speculator.ProcessCpuLoad;
    public long RitualCount => Volatile.Read(ref _ritualCount);

    public Sanctum(
        Trecenarius trecenarius,
        Scriba scriba,
        OraculumBatchFunc oracleLogic,
        int maxCohorts,
        int oracleIntervalMs = 150,
        int cpuMonitorIntervalMs = 500,
        Action<string>? diagnosticSink = null)
    {
        _trecenarius = trecenarius ?? throw new ArgumentNullException(nameof(trecenarius));
        _scriba = scriba ?? throw new ArgumentNullException(nameof(scriba));
        _oracleLogic = oracleLogic ?? throw new ArgumentNullException(nameof(oracleLogic));
        _intervalMs = Math.Max(10, oracleIntervalMs);
        _diagnosticSink = diagnosticSink;

        _speculator = new Speculator(cpuMonitorIntervalMs);
        _stopSignal = new ManualResetEventSlim(false);
        _strategyScratch = new Strategia[maxCohorts];
        _updatedMask = new byte[maxCohorts];
        _isRunning = true;

        _thread = new Thread(RitualLoop)
        {
            Name = "Sanctum_Haruspex",
            IsBackground = true,
            Priority = ThreadPriority.Lowest
        };
    }

    public void Start() => _thread.Start();

    private void RitualLoop()
    {
        Topologia topology = Mensor.Result;

        while (_isRunning)
        {
            // Sleep periodically first or between windows so each Oracle invocation sees a full,
            // statistically meaningful window of frames rather than noisy micro-samples.
            if (_stopSignal.Wait(_intervalMs))
                break;

            if (!_isRunning)
                break;

            try
            {
                // 1. Zero-allocation OS CPU pressure check.
                _speculator.Explorare();
                float cpuPressure = _speculator.ProcessCpuLoad;

                // 2. Drain accumulated telemetry window from Circulus.
                ReadOnlySpan<Tessera> omens = _trecenarius.CollectOmens();
                if (omens.IsEmpty)
                    continue;

                Array.Clear(_updatedMask, 0, _updatedMask.Length);

                // 3. Consult the Haruspex ML model on this dedicated background thread (0 B alloc).
                _oracleLogic(in topology, cpuPressure, omens, _strategyScratch, _updatedMask);

                // 4. Publish updated per-Cohort strategies atomically via Scriba SeqLock.
                for (int slot = 0; slot < _updatedMask.Length; slot++)
                {
                    if (_updatedMask[slot] != 0)
                    {
                        _scriba.Scribere(slot, in _strategyScratch[slot]);
                    }
                }

                Interlocked.Increment(ref _ritualCount);
            }
            catch (Exception ex)
            {
                _diagnosticSink?.Invoke($"Sanctum ritual error ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isRunning = false;
        _stopSignal.Set();

        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }

        _stopSignal.Dispose();
        _speculator.Dispose();
    }
}