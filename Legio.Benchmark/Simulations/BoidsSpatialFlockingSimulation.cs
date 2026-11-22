using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Classic Craig Reynolds Boids Crowd Flocking Simulation (32,768 agents) with a Zero-Allocation
/// Lock-Free Spatial Hash Grid.
/// Exercises:
/// - 5 Cohorts, 4 DAG Phases (GridReset || PredatorUpdate -> Lock-Free SpatialBinning ->
///   FlockSteering -> BoidIntegration)
/// - Genuine hardware atomic contention (Interlocked.Exchange on hot flock cells during binning)
/// - Severe memory-bound spatial clustering (35% of boids tightly packed in 4 flocks -> 20x-40x
///   more neighbor evaluations per boid than lone scouts -> heavy Optio work stealing).
/// </summary>
public sealed class BoidsSpatialFlockingSimulation
{
    private const int ResPos      = 0;
    private const int ResVel      = 1;
    private const int ResGrid     = 2;
    private const int ResPredator = 3;
    private const int ResAccel    = 4;

    private const int GridDim = 32;
    private const int GridCells = GridDim * GridDim; // 1024 spatial buckets
    private const float WorldSize = 256.0f;
    private const float InvCellSize = GridDim / WorldSize;
    private const float PerceptionSq = 64.0f;
    private const float SeparationSq = 9.0f;
    private const float Dt = 0.016f;
    private const int MaxNeighborsPerBoid = 48;

    public readonly int BoidCount;
    public readonly int PredatorCount = 64;

    // SoA Boid buffers
    private readonly float[] _px, _py;
    private readonly float[] _vx, _vy;
    private readonly float[] _ax, _ay;

    // Lock-free intrusive spatial linked-list (0 B allocation!)
    private readonly int[] _cellHead;
    private readonly int[] _nextInCell;

    // Predators
    private readonly float[] _predX, _predY;
    private readonly float[] _predVx, _predVy;

    private readonly PriorPilus[] _cohorts;

    public BoidsSpatialFlockingSimulation(int boidCount = 32_768)
    {
        BoidCount = boidCount;

        _px = new float[boidCount];
        _py = new float[boidCount];
        _vx = new float[boidCount];
        _vy = new float[boidCount];
        _ax = new float[boidCount];
        _ay = new float[boidCount];

        _cellHead = new int[GridCells];
        _nextInCell = new int[boidCount];

        _predX = new float[PredatorCount];
        _predY = new float[PredatorCount];
        _predVx = new float[PredatorCount];
        _predVy = new float[PredatorCount];

        var rng = new Random(1987);
        float[] flockCentersX = [42f, 196f, 64f, 210f];
        float[] flockCentersY = [54f, 62f, 190f, 198f];

        for (int i = 0; i < boidCount; i++)
        {
            if (i < (boidCount * 7) / 20)
            {
                // 35% of boids packed tightly into 4 dense flocks -> extreme cell & neighbor skew
                int f = i & 3;
                _px[i] = Wrap(flockCentersX[f] + ((float)rng.NextDouble() * 2f - 1f) * 14f);
                _py[i] = Wrap(flockCentersY[f] + ((float)rng.NextDouble() * 2f - 1f) * 14f);
            }
            else
            {
                _px[i] = (float)rng.NextDouble() * WorldSize;
                _py[i] = (float)rng.NextDouble() * WorldSize;
            }

            float angle = (float)rng.NextDouble() * 6.283185f;
            float speed = 6.0f + (float)rng.NextDouble() * 6.0f;
            _vx[i] = MathF.Cos(angle) * speed;
            _vy[i] = MathF.Sin(angle) * speed;
        }

        for (int p = 0; p < PredatorCount; p++)
        {
            _predX[p] = (float)rng.NextDouble() * WorldSize;
            _predY[p] = (float)rng.NextDouble() * WorldSize;
            float angle = (float)rng.NextDouble() * 6.283185f;
            _predVx[p] = MathF.Cos(angle) * 16.0f;
            _predVy[p] = MathF.Sin(angle) * 16.0f;
        }

        _cohorts =
        [
            new DelegateCohort(0, () => GridCells, StepGridReset),
            new DelegateCohort(1, () => PredatorCount, StepPredators),
            new DelegateCohort(2, () => BoidCount, StepSpatialBinning),
            new DelegateCohort(3, () => BoidCount, StepFlockSteering),
            new DelegateCohort(4, () => BoidCount, StepIntegrateBoids)
        ];
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0A: Reset 1024 grid heads — W(Grid)
        var c0 = new Cohors(0, _cohorts[0]);
        c0.WriteMask.Tollere(ResGrid);

        // Phase 0B (parallel with 0A!): Update 64 predators — W(Predator)
        var c1 = new Cohors(1, _cohorts[1]);
        c1.WriteMask.Tollere(ResPredator);

        // Phase 1: Lock-free spatial binning — R(Pos), R/W(Grid)
        var c2 = new Cohors(2, _cohorts[2]);
        c2.ReadMask.Tollere(ResPos);
        c2.ReadMask.Tollere(ResGrid);
        c2.WriteMask.Tollere(ResGrid);

        // Phase 2: 3x3 Neighborhood Flocking + Predator Avoidance — R(Pos, Vel, Grid, Predator) -> W(Accel)
        var c3 = new Cohors(3, _cohorts[3]);
        c3.ReadMask.Tollere(ResPos);
        c3.ReadMask.Tollere(ResVel);
        c3.ReadMask.Tollere(ResGrid);
        c3.ReadMask.Tollere(ResPredator);
        c3.WriteMask.Tollere(ResAccel);

        // Phase 3: Integration — R(Accel) -> W(Pos, Vel)
        var c4 = new Cohors(4, _cohorts[4]);
        c4.ReadMask.Tollere(ResAccel);
        c4.WriteMask.Tollere(ResPos);
        c4.WriteMask.Tollere(ResVel);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.RegisterCohors(c4);
        legatus.ConstructConsilium();
    }

    private void StepGridReset(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
            _cellHead[i] = -1;
    }

    private void StepPredators(int start, int length)
    {
        int end = start + length;
        for (int p = start; p < end; p++)
        {
            // Gently curve predator trajectory
            float vx = _predVx[p];
            float vy = _predVy[p];
            float rot = 0.015f;
            _predVx[p] = vx - vy * rot;
            _predVy[p] = vy + vx * rot;
            _predX[p] = Wrap(_predX[p] + _predVx[p] * Dt);
            _predY[p] = Wrap(_predY[p] + _predVy[p] * Dt);
        }
    }

    private void StepSpatialBinning(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int cx = (int)(_px[i] * InvCellSize) & (GridDim - 1);
            int cy = (int)(_py[i] * InvCellSize) & (GridDim - 1);
            int cell = (cy << 5) | cx;

            // Lock-free atomic push onto cell's intrusive list (real cache-line contention on hot cells!)
            int prev = Interlocked.Exchange(ref _cellHead[cell], i);
            _nextInCell[i] = prev;
        }
    }

    private void StepFlockSteering(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i];
            float vx = _vx[i], vy = _vy[i];

            int cx = (int)(px * InvCellSize) & (GridDim - 1);
            int cy = (int)(py * InvCellSize) & (GridDim - 1);

            float sepX = 0f, sepY = 0f;
            float aliX = 0f, aliY = 0f;
            float cohX = 0f, cohY = 0f;
            int neighbors = 0;

            for (int dy = -1; dy <= 1 && neighbors < MaxNeighborsPerBoid; dy++)
            {
                int ny = (cy + dy) & (GridDim - 1);
                int row = ny << 5;
                for (int dx = -1; dx <= 1 && neighbors < MaxNeighborsPerBoid; dx++)
                {
                    int nx = (cx + dx) & (GridDim - 1);
                    int j = _cellHead[row | nx];
                    int hops = 0;

                    while (j != -1 && hops < 24 && neighbors < MaxNeighborsPerBoid)
                    {
                        hops++;
                        if (j != i)
                        {
                            float rx = px - _px[j];
                            float ry = py - _py[j];
                            float dSq = rx * rx + ry * ry;

                            if (dSq > 1e-5f && dSq < PerceptionSq)
                            {
                                aliX += _vx[j];
                                aliY += _vy[j];
                                cohX += _px[j];
                                cohY += _py[j];
                                neighbors++;

                                if (dSq < SeparationSq)
                                {
                                    float inv = 1.0f / dSq;
                                    sepX += rx * inv;
                                    sepY += ry * inv;
                                }
                            }
                        }

                        j = _nextInCell[j];
                    }
                }
            }

            float ax = 0f, ay = 0f;
            if (neighbors > 0)
            {
                float invN = 1.0f / neighbors;
                ax += (aliX * invN - vx) * 0.45f;
                ay += (aliY * invN - vy) * 0.45f;
                ax += (cohX * invN - px) * 0.18f;
                ay += (cohY * invN - py) * 0.18f;
                ax += sepX * 2.8f;
                ay += sepY * 2.8f;
            }

            // Predator avoidance (check 8 nearest/strided predators)
            int pStart = i & 7;
            for (int p = pStart; p < PredatorCount; p += 8)
            {
                float rx = px - _predX[p];
                float ry = py - _predY[p];
                float dSq = rx * rx + ry * ry + 1.0f;
                if (dSq < 400.0f)
                {
                    float flee = 90.0f / dSq;
                    ax += rx * flee;
                    ay += ry * flee;
                }
            }

            // Keep the 35% clustered flock boids orbiting their 4 territory hubs over long 10k+ frame runs
            if (i < (BoidCount * 7) / 20)
            {
                int f = i & 3;
                float hx = f switch { 0 => 42f, 1 => 196f, 2 => 64f, _ => 210f };
                float hy = f switch { 0 => 54f, 1 => 62f, 2 => 190f, _ => 198f };
                ax += (hx - px) * 0.25f;
                ay += (hy - py) * 0.25f;
            }

            _ax[i] = ax;
            _ay[i] = ay;
        }
    }

    private void StepIntegrateBoids(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float vx = _vx[i] + _ax[i] * Dt;
            float vy = _vy[i] + _ay[i] * Dt;

            float speedSq = vx * vx + vy * vy;
            if (speedSq > 225.0f) // max speed = 15
            {
                float inv = 15.0f / MathF.Sqrt(speedSq);
                vx *= inv;
                vy *= inv;
            }
            else if (speedSq < 16.0f && speedSq > 1e-5f) // min speed = 4
            {
                float inv = 4.0f / MathF.Sqrt(speedSq);
                vx *= inv;
                vy *= inv;
            }

            _vx[i] = vx;
            _vy[i] = vy;
            _px[i] = Wrap(_px[i] + vx * Dt);
            _py[i] = Wrap(_py[i] + vy * Dt);
        }
    }

    private static float Wrap(float v)
    {
        if (v < 0f) return v + WorldSize;
        if (v >= WorldSize) return v - WorldSize;
        return v;
    }

    public void RunSerial()
    {
        StepGridReset(0, GridCells);
        StepPredators(0, PredatorCount);
        StepSpatialBinning(0, BoidCount);
        StepFlockSteering(0, BoidCount);
        StepIntegrateBoids(0, BoidCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        StepGridReset(0, GridCells);
        StepPredators(0, PredatorCount);

        const int chunk = 1024;
        int blocks = (BoidCount + chunk - 1) / chunk;

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            StepSpatialBinning(s, Math.Min(chunk, BoidCount - s));
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            StepFlockSteering(s, Math.Min(chunk, BoidCount - s));
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            StepIntegrateBoids(s, Math.Min(chunk, BoidCount - s));
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
