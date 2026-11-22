using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Non-Stationary Dynamic Load-Shift Simulation (49,152 entities, 4 cohorts, 3 DAG phases).
/// Cycles continuously through 4 distinct game-runtime regimes every 320 frames:
///   Regime 0 [0..79]   : Calm Exploration (uniform light work, 2 ops/entity)
///   Regime 1 [80..159] : Front-Band Explosion (first 18% spikes to 56x nonlinear shockwave!)
///   Regime 2 [160..239]: Global Turbulence Storm (100% heavy uniform work, 16 ops/entity)
///   Regime 3 [240..319]: Rear-Band Ambush Cluster (last 16% spikes to 52x work — tests far-end FIFO stealing!)
/// Designed specifically to stress online ML adaptation (StaticHeuristic vs FuzzySugeno vs OFN vs Bandit)
/// and frame-pacing stability (P95 / P99 / 1% Low FPS) under abrupt load shifts.
/// </summary>
public sealed class DynamicLoadWaveSimulation
{
    private const int ResPos     = 0;
    private const int ResVel     = 1;
    private const int ResAmbient = 2;
    private const int ResHazard  = 3;
    private const int ResMetrics = 4;

    private const float Dt = 0.016f;
    public const int CycleLength = 128;

    public readonly int EntityCount;

    private readonly float[] _px, _py, _pz;
    private readonly float[] _vx, _vy, _vz;
    private readonly float[] _ambX, _ambY, _ambZ;
    private readonly float[] _hazX, _hazY, _hazZ;
    private readonly float[] _metrics;

    private int _frameTick;

    private readonly PriorPilus[] _cohorts;

    public int CurrentRegime => (_frameTick & (CycleLength - 1)) >> 5;

    public DynamicLoadWaveSimulation(int entityCount = 49_152)
    {
        EntityCount = entityCount;

        _px = new float[entityCount];
        _py = new float[entityCount];
        _pz = new float[entityCount];
        _vx = new float[entityCount];
        _vy = new float[entityCount];
        _vz = new float[entityCount];
        _ambX = new float[entityCount];
        _ambY = new float[entityCount];
        _ambZ = new float[entityCount];
        _hazX = new float[entityCount];
        _hazY = new float[entityCount];
        _hazZ = new float[entityCount];
        _metrics = new float[entityCount];

        var rng = new Random(7777);
        for (int i = 0; i < entityCount; i++)
        {
            _px[i] = ((float)rng.NextDouble() * 2f - 1f) * 80f;
            _py[i] = ((float)rng.NextDouble() * 2f - 1f) * 80f;
            _pz[i] = ((float)rng.NextDouble() * 2f - 1f) * 80f;
            _vx[i] = ((float)rng.NextDouble() * 2f - 1f) * 4f;
            _vy[i] = ((float)rng.NextDouble() * 2f - 1f) * 4f;
            _vz[i] = ((float)rng.NextDouble() * 2f - 1f) * 4f;
        }

        _cohorts =
        [
            new DelegateCohort(0, () => EntityCount, StepAmbientPhysics),
            new DelegateCohort(1, () => EntityCount, StepDynamicHazard),
            new DelegateCohort(2, () => EntityCount, StepMergeAndIntegrate),
            new DelegateCohort(3, () => EntityCount, StepMetricsAndClamp)
        ];
    }

    public void AdvanceFrameTick()
    {
        _frameTick++;
    }

    public void ResetWave()
    {
        _frameTick = 0;
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0A: AmbientPhysics — R(Pos, Vel) -> W(Ambient)
        var c0 = new Cohors(0, _cohorts[0]);
        c0.ReadMask.Tollere(ResPos);
        c0.ReadMask.Tollere(ResVel);
        c0.WriteMask.Tollere(ResAmbient);

        // Phase 0B (parallel with 0A!): DynamicHazard — R(Pos) -> W(Hazard)
        var c1 = new Cohors(1, _cohorts[1]);
        c1.ReadMask.Tollere(ResPos);
        c1.WriteMask.Tollere(ResHazard);

        // Phase 1: Merge & Integrate — R(Ambient, Hazard) -> W(Pos, Vel)
        var c2 = new Cohors(2, _cohorts[2]);
        c2.ReadMask.Tollere(ResAmbient);
        c2.ReadMask.Tollere(ResHazard);
        c2.WriteMask.Tollere(ResPos);
        c2.WriteMask.Tollere(ResVel);

        // Phase 2: Metrics & Clamp — R(Pos, Vel) -> W(Pos, Vel, Metrics)
        var c3 = new Cohors(3, _cohorts[3]);
        c3.ReadMask.Tollere(ResPos);
        c3.ReadMask.Tollere(ResVel);
        c3.WriteMask.Tollere(ResPos);
        c3.WriteMask.Tollere(ResVel);
        c3.WriteMask.Tollere(ResMetrics);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.ConstructConsilium();
    }

    private void StepAmbientPhysics(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            float inv = 1.0f / MathF.Sqrt(px * px + py * py + pz * pz + 16.0f);
            _ambX[i] = -px * inv * 6.0f - _vx[i] * 0.04f;
            _ambY[i] = -py * inv * 6.0f - _vy[i] * 0.04f - 4.9f;
            _ambZ[i] = -pz * inv * 6.0f - _vz[i] * 0.04f;
        }
    }

    private void StepDynamicHazard(int start, int length)
    {
        int end = start + length;
        int n = EntityCount;
        int regime = CurrentRegime;
        int frontCutoff = (n * 9) / 50;  // 18%
        int rearStart   = (n * 42) / 50; // last 16%

        for (int i = start; i < end; i++)
        {
            int iterations = regime switch
            {
                0 => 2,
                1 => (i < frontCutoff) ? 56 : 2,
                2 => 16,
                _ => (i >= rearStart) ? 52 : 2
            };

            float px = _px[i], py = _py[i], pz = _pz[i];
            float hx = 0f, hy = 0f, hz = 0f;
            int baseIdx = (i * 19) % n;

            for (int k = 0; k < iterations; k++)
            {
                int j = baseIdx + k;
                if (j >= n) j -= n;

                float dx = px - _px[j];
                float dy = py - _py[j];
                float dz = pz - _pz[j];
                float dSq = dx * dx + dy * dy + dz * dz + 2.0f;
                float inv = 1.0f / MathF.Sqrt(dSq);
                float wave = MathF.Sin(dSq * 0.02f) * inv * 0.35f;
                hx += dx * wave;
                hy += dy * wave;
                hz += dz * wave;
            }

            _hazX[i] = hx;
            _hazY[i] = hy;
            _hazZ[i] = hz;
        }
    }

    private void StepMergeAndIntegrate(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float vx = _vx[i] + (_ambX[i] + _hazX[i]) * Dt;
            float vy = _vy[i] + (_ambY[i] + _hazY[i]) * Dt;
            float vz = _vz[i] + (_ambZ[i] + _hazZ[i]) * Dt;

            _vx[i] = vx;
            _vy[i] = vy;
            _vz[i] = vz;

            _px[i] += vx * Dt;
            _py[i] += vy * Dt;
            _pz[i] += vz * Dt;
        }
    }

    private void StepMetricsAndClamp(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            float vx = _vx[i], vy = _vy[i], vz = _vz[i];

            if (px > 100f) { px = 100f; vx = -vx * 0.8f; }
            else if (px < -100f) { px = -100f; vx = -vx * 0.8f; }
            if (py > 100f) { py = 100f; vy = -vy * 0.8f; }
            else if (py < -100f) { py = -100f; vy = -vy * 0.8f; }
            if (pz > 100f) { pz = 100f; vz = -vz * 0.8f; }
            else if (pz < -100f) { pz = -100f; vz = -vz * 0.8f; }

            _px[i] = px; _py[i] = py; _pz[i] = pz;
            _vx[i] = vx; _vy[i] = vy; _vz[i] = vz;
            _metrics[i] = 0.5f * (vx * vx + vy * vy + vz * vz);
        }
    }

    public void RunSerial()
    {
        StepAmbientPhysics(0, EntityCount);
        StepDynamicHazard(0, EntityCount);
        StepMergeAndIntegrate(0, EntityCount);
        StepMetricsAndClamp(0, EntityCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        const int chunk = 1536;
        int blocks = (EntityCount + chunk - 1) / chunk;

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, EntityCount - s);
            StepAmbientPhysics(s, len);
            StepDynamicHazard(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, EntityCount - s);
            StepMergeAndIntegrate(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, EntityCount - s);
            StepMetricsAndClamp(s, len);
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
