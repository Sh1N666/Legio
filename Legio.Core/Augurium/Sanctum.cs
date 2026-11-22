using Legio.Protocol;

namespace Legio.Core.Augurium;

/// <summary>
/// Delegate representing the Oracle's divine function (F# logic).
/// Takes a context + metrics, returns a Strategy.
/// </summary>
public delegate Strategia OraculumFunc(Topologia topo, ReadOnlySpan<Tessera> omens);

/// <summary>
/// Sanctum (The Sanctuary).
/// Wraps the background thread where the Haruspex operates.
/// Runs at Lowest priority to never interfere with the battle (Main Thread/Centurions).
/// </summary>
public class Sanctum
{
    private readonly Thread _thread;
    private readonly Trecenarius _trecenarius;
    private readonly Scriba _scriba;
    private readonly Speculator _speculator;
    private readonly OraculumFunc _oracleLogic;
    
    private volatile bool _isRunning;

    public Sanctum(Trecenarius trecenarius, Scriba scriba, OraculumFunc oracleLogic)
    {
        _trecenarius = trecenarius;
        _scriba = scriba;
        _oracleLogic = oracleLogic;
        _speculator = new Speculator();
        _isRunning = true;

        _thread = new Thread(RitualLoop)
        {
            Name = "Sanctum_Haruspex",
            IsBackground = true,
            Priority = ThreadPriority.Lowest // CRITICAL: Do not steal cycles from Centurions
        };
    }

    public void Start() => _thread.Start();

    private void RitualLoop()
    {
        // One-time static analysis
        var topology = Mensor.Result;

        while (_isRunning)
        {
            // 1. Gather dynamic intel
            _speculator.Explorare();

            // 2. Collect Omens (Metrics) from the Ring Buffer
            var omens = _trecenarius.CollectOmens();

            if (omens.Length > 0)
            {
                // 3. Consult the Oracle (F# Pure Function)
                // We pass a Span, ensuring zero allocations during transfer
                Strategia prediction = _oracleLogic(topology, omens);

                // 4. Inscribe the new strategy for the Legion
                _scriba.Scribere(prediction);
            }

            // 5. Meditate
            // Sleep to avoid burning CPU in the background loop. 
            // The learning doesn't need to happen every microsecond.
            Thread.Sleep(16); // ~60 Hz update rate for intelligence is plenty
        }
    }
}