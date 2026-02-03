using Legio.Core.Executio;
using Legio.Core.Armarium;

namespace Legio.Benchmark;

/// <summary>
/// Simulates a computationally heavy system.
/// Can be configured to be "Light" (Memory Bound) or "Super Heavy" (CPU Bound).
/// </summary>
public unsafe class HeavyMathSystem : PriorPilus
{
    private readonly float[] _data;
    private readonly int _complexity;

    /// <summary>
    /// Creates the system.
    /// </summary>
    /// <param name="complexity">Number of heavy math iterations per item.</param>
    public HeavyMathSystem(int id, int count, int complexity) : base(id)
    {
        _complexity = complexity;
        _data = new float[count];
        Random r = new Random(42);
        for (int i = 0; i < count; i++) _data[i] = (float)r.NextDouble() * 100f;
    }

    protected override int GetTotalItemCount() => _data.Length;

    /// <summary>
    /// The worker logic.
    /// </summary>
    protected override void ExecuteBatchLogic(int start, int length)
    {
        int iterations = _complexity;
        
        // Fixed pinning for maximum pointer performance (no bounds checking)
        fixed (float* ptr = _data)
        {
            float* batchPtr = ptr + start;
            
            for (int i = 0; i < length; i++)
            {
                float val = batchPtr[i];
                
                // Simulate HEAVY workload (e.g., iterative solver)
                for (int j = 0; j < iterations; j++)
                {
                    val = MathF.Sin(val) * MathF.Cos(val) + MathF.Sqrt(MathF.Abs(val));
                }
                
                batchPtr[i] = val;
            }
        }
    }

    public void RunSerial() => ExecuteBatchLogic(0, _data.Length);

    public void RunParallelFor()
    {
        Parallel.For(0, _data.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            float val = _data[i];
            for (int j = 0; j < _complexity; j++)
            {
                val = MathF.Sin(val) * MathF.Cos(val) + MathF.Sqrt(MathF.Abs(val));
            }
            _data[i] = val;
        });
    }
}