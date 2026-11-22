using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Extreme Multi-Phase Barrier &amp; Memory-Bandwidth Torture Test:
/// 256x256 (65,536-vertex) Position-Based Dynamics (PBD) Cloth Physics Simulation using
/// Red-Black Graph-Colored Gauss-Seidel Distance Constraints (2 iterations x 4 color passes = 8 solver phases).
/// Total: 10 SEQUENTIAL DAG PHASES PER SINGLE FRAME, all strictly synchronized via Vexillum WAW/RAW hazards!
///   Phase 0   : Wind + Gravity Verlet Prediction (65,536 vertices)
///   Phase 1..4: PBD Iteration 1 (HorizEven -> HorizOdd -> VertEven -> VertOdd: 130,560 constraints)
///   Phase 5..8: PBD Iteration 2 (HorizEven -> HorizOdd -> VertEven -> VertOdd: 130,560 constraints)
///   Phase 9   : 8-Sphere Body Collision + Finite-Difference Vertex Normal Recomputation (65,536 vertices)
/// Stresses phase-barrier latency, memory bandwidth, and zero-allocation dispatch to the absolute limit.
/// </summary>
public sealed class ClothVerletPbdSimulation
{
    private const int ResPos     = 0;
    private const int ResPrevPos = 1;
    private const int ResNormals = 2;

    public const int GridW = 256;
    public const int GridH = 256;
    public const int VertexCount = GridW * GridH; // 65,536 vertices

    private const float RestLen = 0.08f;
    private const float Dt = 0.016f;
    private const float Damping = 0.992f;

    private readonly float[] _px, _py, _pz;
    private readonly float[] _oldX, _oldY, _oldZ;
    private readonly float[] _nx, _ny, _nz;
    private readonly byte[] _pinned;

    // 8 dynamic collider spheres pressing into the cloth
    private readonly float[] _colX = [-3.0f, 3.0f, 0.0f, -5.0f, 5.0f, -2.0f, 2.0f, 0.0f];
    private readonly float[] _colY = [-4.0f, -4.0f, -6.0f, -8.0f, -8.0f, -11.0f, -11.0f, -14.0f];
    private readonly float[] _colZ = [0.6f, -0.6f, 0.9f, 0.4f, -0.4f, 0.7f, -0.7f, 0.5f];
    private readonly float[] _colR = [1.8f, 1.8f, 2.2f, 1.5f, 1.5f, 1.9f, 1.9f, 2.4f];

    // Half-edge counts per color pass:
    // Horizontal pairs per row: 128 even edges + 127 odd edges -> 256 * 128 = 32,768 even, 256 * 127 = 32,512 odd
    private const int HorizEvenCount = GridH * (GridW / 2);       // 32,768
    private const int HorizOddCount  = GridH * (GridW / 2 - 1);   // 32,512
    private const int VertEvenCount  = (GridH / 2) * GridW;       // 32,768
    private const int VertOddCount   = (GridH / 2 - 1) * GridW;   // 32,512

    private float _windPhase;
    private readonly PriorPilus[] _cohorts;

    public ClothVerletPbdSimulation()
    {
        _px = new float[VertexCount];
        _py = new float[VertexCount];
        _pz = new float[VertexCount];
        _oldX = new float[VertexCount];
        _oldY = new float[VertexCount];
        _oldZ = new float[VertexCount];
        _nx = new float[VertexCount];
        _ny = new float[VertexCount];
        _nz = new float[VertexCount];
        _pinned = new byte[VertexCount];

        float halfW = (GridW - 1) * RestLen * 0.5f;
        for (int y = 0; y < GridH; y++)
        {
            for (int x = 0; x < GridW; x++)
            {
                int i = y * GridW + x;
                float px = x * RestLen - halfW;
                float py = -y * RestLen;
                float pz = MathF.Sin(x * 0.1f) * 0.05f;
                _px[i] = _oldX[i] = px;
                _py[i] = _oldY[i] = py;
                _pz[i] = _oldZ[i] = pz;

                // Pin top edge corners and periodic anchor rivets
                if (y == 0 && (x == 0 || x == GridW - 1 || (x & 31) == 0))
                    _pinned[i] = 1;
            }
        }

        // 10 cohorts across 10 strictly ordered DAG phases!
        _cohorts =
        [
            new DelegateCohort(0, () => VertexCount, StepVerletPredict),
            new DelegateCohort(1, () => HorizEvenCount, StepHorizEven),
            new DelegateCohort(2, () => HorizOddCount, StepHorizOdd),
            new DelegateCohort(3, () => VertEvenCount, StepVertEven),
            new DelegateCohort(4, () => VertOddCount, StepVertOdd),
            new DelegateCohort(5, () => HorizEvenCount, StepHorizEven),
            new DelegateCohort(6, () => HorizOddCount, StepHorizOdd),
            new DelegateCohort(7, () => VertEvenCount, StepVertEven),
            new DelegateCohort(8, () => VertOddCount, StepVertOdd),
            new DelegateCohort(9, () => VertexCount, StepCollideAndNormals)
        ];
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0: Verlet predict — R/W(Pos, PrevPos)
        var c0 = new Cohors(0, _cohorts[0]);
        c0.ReadMask.Tollere(ResPos);
        c0.ReadMask.Tollere(ResPrevPos);
        c0.WriteMask.Tollere(ResPos);
        c0.WriteMask.Tollere(ResPrevPos);
        legatus.RegisterCohors(c0);

        // Phases 1..8: 8 Red-Black graph-colored constraint solver passes — R/W(Pos)
        // Each conflicts via WAW+RAW on ResPos with the previous pass -> automatically sequenced into phases 1..8!
        for (int id = 1; id <= 8; id++)
        {
            var c = new Cohors(id, _cohorts[id]);
            c.ReadMask.Tollere(ResPos);
            c.WriteMask.Tollere(ResPos);
            legatus.RegisterCohors(c);
        }

        // Phase 9: Collisions & normals — R/W(Pos), W(Normals)
        var c9 = new Cohors(9, _cohorts[9]);
        c9.ReadMask.Tollere(ResPos);
        c9.WriteMask.Tollere(ResPos);
        c9.WriteMask.Tollere(ResNormals);
        legatus.RegisterCohors(c9);

        legatus.ConstructConsilium();
    }

    private void StepVerletPredict(int start, int length)
    {
        int end = start + length;
        float wind = 4.5f + 2.5f * MathF.Sin(_windPhase);
        float dtSq = Dt * Dt;

        for (int i = start; i < end; i++)
        {
            if (_pinned[i] != 0)
                continue;

            float x = _px[i], y = _py[i], z = _pz[i];
            float vx = (x - _oldX[i]) * Damping;
            float vy = (y - _oldY[i]) * Damping;
            float vz = (z - _oldZ[i]) * Damping;

            _oldX[i] = x;
            _oldY[i] = y;
            _oldZ[i] = z;

            // Turbulent wind gust varies spatially across cloth folds
            float gust = wind * (0.6f + 0.4f * MathF.Sin(x * 0.8f + y * 0.5f + _windPhase));
            _px[i] = x + vx + gust * 0.15f * dtSq;
            _py[i] = y + vy - 9.81f * dtSq;
            _pz[i] = z + vz + gust * dtSq;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepHorizEven(int start, int length)
    {
        const int pairsPerRow = GridW / 2;
        int end = start + length;
        for (int e = start; e < end; e++)
        {
            int row = e / pairsPerRow;
            int col = (e - row * pairsPerRow) << 1;
            int a = row * GridW + col;
            SolveDistancePair(a, a + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepHorizOdd(int start, int length)
    {
        const int pairsPerRow = GridW / 2 - 1;
        int end = start + length;
        for (int e = start; e < end; e++)
        {
            int row = e / pairsPerRow;
            int col = ((e - row * pairsPerRow) << 1) + 1;
            int a = row * GridW + col;
            SolveDistancePair(a, a + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepVertEven(int start, int length)
    {
        int end = start + length;
        for (int e = start; e < end; e++)
        {
            int pairRow = e / GridW;
            int col = e - pairRow * GridW;
            int a = (pairRow << 1) * GridW + col;
            SolveDistancePair(a, a + GridW);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepVertOdd(int start, int length)
    {
        int end = start + length;
        for (int e = start; e < end; e++)
        {
            int pairRow = e / GridW;
            int col = e - pairRow * GridW;
            int a = ((pairRow << 1) + 1) * GridW + col;
            SolveDistancePair(a, a + GridW);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SolveDistancePair(int a, int b)
    {
        float dx = _px[b] - _px[a];
        float dy = _py[b] - _py[a];
        float dz = _pz[b] - _pz[a];
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz + 1e-10f);
        float diff = (dist - RestLen) / dist;

        bool pinA = _pinned[a] != 0;
        bool pinB = _pinned[b] != 0;

        if (!pinA && !pinB)
        {
            float corr = diff * 0.45f;
            float cx = dx * corr, cy = dy * corr, cz = dz * corr;
            _px[a] += cx; _py[a] += cy; _pz[a] += cz;
            _px[b] -= cx; _py[b] -= cy; _pz[b] -= cz;
        }
        else if (!pinA)
        {
            float corr = diff * 0.9f;
            _px[a] += dx * corr; _py[a] += dy * corr; _pz[a] += dz * corr;
        }
        else if (!pinB)
        {
            float corr = diff * 0.9f;
            _px[b] -= dx * corr; _py[b] -= dy * corr; _pz[b] -= dz * corr;
        }
    }

    private void StepCollideAndNormals(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];

            if (_pinned[i] == 0)
            {
                for (int s = 0; s < 8; s++)
                {
                    float dx = px - _colX[s];
                    float dy = py - _colY[s];
                    float dz = pz - _colZ[s];
                    float dSq = dx * dx + dy * dy + dz * dz;
                    float r = _colR[s];
                    if (dSq < r * r && dSq > 1e-8f)
                    {
                        float inv = r / MathF.Sqrt(dSq);
                        px = _colX[s] + dx * inv;
                        py = _colY[s] + dy * inv;
                        pz = _colZ[s] + dz * inv;
                    }
                }

                _px[i] = px; _py[i] = py; _pz[i] = pz;
            }

            // Finite-difference surface normal for GPU vertex shading
            int y = i / GridW;
            int x = i - y * GridW;
            int iR = (x + 1 < GridW) ? i + 1 : i;
            int iL = (x > 0) ? i - 1 : i;
            int iD = (y + 1 < GridH) ? i + GridW : i;
            int iU = (y > 0) ? i - GridW : i;

            float tx = _px[iR] - _px[iL], ty = _py[iR] - _py[iL], tz = _pz[iR] - _pz[iL];
            float bx = _px[iD] - _px[iU], by = _py[iD] - _py[iU], bz = _pz[iD] - _pz[iU];
            float nx = ty * bz - tz * by;
            float ny = tz * bx - tx * bz;
            float nz = tx * by - ty * bx;
            float nInv = 1.0f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-8f);
            _nx[i] = nx * nInv;
            _ny[i] = ny * nInv;
            _nz[i] = nz * nInv;
        }
    }

    public void AdvanceWind() => _windPhase += 0.04f;

    public void RunSerial()
    {
        AdvanceWind();
        StepVerletPredict(0, VertexCount);
        for (int iter = 0; iter < 2; iter++)
        {
            StepHorizEven(0, HorizEvenCount);
            StepHorizOdd(0, HorizOddCount);
            StepVertEven(0, VertEvenCount);
            StepVertOdd(0, VertOddCount);
        }
        StepCollideAndNormals(0, VertexCount);
    }

    public void RunParallelFor()
    {
        AdvanceWind();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        RunChunked(VertexCount, 1024, opts, StepVerletPredict);
        for (int iter = 0; iter < 2; iter++)
        {
            RunChunked(HorizEvenCount, 1024, opts, StepHorizEven);
            RunChunked(HorizOddCount, 1024, opts, StepHorizOdd);
            RunChunked(VertEvenCount, 1024, opts, StepVertEven);
            RunChunked(VertOddCount, 1024, opts, StepVertOdd);
        }
        RunChunked(VertexCount, 1024, opts, StepCollideAndNormals);
    }

    private static void RunChunked(int count, int chunk, ParallelOptions opts, Action<int, int> kernel)
    {
        int blocks = (count + chunk - 1) / chunk;
        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            kernel(s, Math.Min(chunk, count - s));
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
