using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Scenario 10: 3D Smoothed Particle Hydrodynamics (SPH) Real-Time Fluid Simulation (Müller et al. 2003).
/// 12,288 fluid particles in a dam-break configuration creating extreme spatial density clustering
/// (dense corner = 50-90 neighbors/particle vs sparse splash = 3-10 neighbors/particle),
/// brutal random 27-cell linked-list L3 cache gathers, heavy Poly6/Spiky/Viscosity FPU kernels,
/// and 6 cohorts across 5 strict DAG phases with lock-free atomic grid insertion.
/// Strictly 0 B heap allocation per frame.
/// </summary>
public sealed class FluidSphSimulation
{
    private const int ResPos      = 0;
    private const int ResVel      = 1;
    private const int ResCellHead = 2;
    private const int ResNext     = 3;
    private const int ResDensity  = 4;
    private const int ResPressure = 5;
    private const int ResForce    = 6;
    private const int ResFoam     = 7;

    private const int GridSize = 8192;
    private const int GridMask = GridSize - 1;
    private const float H = 0.62f;
    private const float H2 = H * H;
    private const float InvCellSize = 1.0f / H;
    private const float RestDensity = 1000.0f;
    private const float GasStiffness = 28.0f;
    private const float Viscosity = 0.08f;
    private const float ParticleMass = 1.0f;
    private const float Poly6Coeff = 1.56668147f;   // 315 / (64 * pi * h^9) scaled for normalized units
    private const float SpikyGradCoeff = -14.3239f; // -45 / (pi * h^6) scaled
    private const float ViscLapCoeff = 14.3239f;    // 45 / (pi * h^6) scaled
    private const float DomainSize = 12.0f;
    private const float Dt = 0.004f;

    public readonly int ParticleCount;

    private readonly float[] _px, _py, _pz;
    private readonly float[] _vx, _vy, _vz;
    private readonly float[] _fx, _fy, _fz;
    private readonly float[] _density;
    private readonly float[] _pressure;
    private readonly float[] _foam;
    private readonly int[] _cellHead;
    private readonly int[] _next;

    private readonly ClearGridSys _clearGrid;
    private readonly HashInsertSys _hashInsert;
    private readonly DensityPressureSys _densityPressure;
    private readonly SphForceSys _sphForces;
    private readonly SprayFoamSys _sprayFoam;
    private readonly IntegrateSys _integrate;

    public FluidSphSimulation(int particleCount = 12_288)
    {
        ParticleCount = particleCount;
        _px = new float[particleCount];
        _py = new float[particleCount];
        _pz = new float[particleCount];
        _vx = new float[particleCount];
        _vy = new float[particleCount];
        _vz = new float[particleCount];
        _fx = new float[particleCount];
        _fy = new float[particleCount];
        _fz = new float[particleCount];
        _density = new float[particleCount];
        _pressure = new float[particleCount];
        _foam = new float[particleCount];
        _cellHead = new int[GridSize];
        _next = new int[particleCount];

        // Deterministic dam-break initialization:
        // First 35% of indices are tightly packed in a high-density corner (extreme neighbor load & spatial skew),
        // remaining 65% are distributed sparsely across the tank.
        int denseCount = (particleCount * 35) / 100;
        uint rng = 0x517CC1B7u;
        for (int i = 0; i < particleCount; i++)
        {
            float rx = NextFloat(ref rng);
            float ry = NextFloat(ref rng);
            float rz = NextFloat(ref rng);

            if (i < denseCount)
            {
                _px[i] = 0.4f + rx * 2.5f;
                _py[i] = 0.4f + ry * 2.6f;
                _pz[i] = 0.4f + rz * 2.5f;
            }
            else
            {
                _px[i] = 3.6f + rx * 7.8f;
                _py[i] = 0.4f + ry * 8.2f;
                _pz[i] = 0.4f + rz * 7.8f;
            }

            _vx[i] = (NextFloat(ref rng) - 0.5f) * 0.4f;
            _vy[i] = (NextFloat(ref rng) - 0.5f) * 0.2f;
            _vz[i] = (NextFloat(ref rng) - 0.5f) * 0.4f;
            _density[i] = RestDensity;
        }

        _clearGrid = new ClearGridSys(0, this);
        _hashInsert = new HashInsertSys(1, this);
        _densityPressure = new DensityPressureSys(2, this);
        _sphForces = new SphForceSys(3, this);
        _sprayFoam = new SprayFoamSys(4, this);
        _integrate = new IntegrateSys(5, this);
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0: Clear hash grid heads
        var c0 = new Cohors(0, _clearGrid);
        c0.WriteMask.Tollere(ResCellHead);

        // Phase 1: Lock-free atomic linked-list particle insertion
        var c1 = new Cohors(1, _hashInsert);
        c1.ReadMask.Tollere(ResPos);
        c1.WriteMask.Tollere(ResCellHead);
        c1.WriteMask.Tollere(ResNext);

        // Phase 2: 27-cell Poly6 density & Tait pressure
        var c2 = new Cohors(2, _densityPressure);
        c2.ReadMask.Tollere(ResPos);
        c2.ReadMask.Tollere(ResCellHead);
        c2.ReadMask.Tollere(ResNext);
        c2.WriteMask.Tollere(ResDensity);
        c2.WriteMask.Tollere(ResPressure);

        // Phase 3A: 27-cell Spiky pressure gradient + Laplacian viscosity forces
        var c3 = new Cohors(3, _sphForces);
        c3.ReadMask.Tollere(ResPos);
        c3.ReadMask.Tollere(ResVel);
        c3.ReadMask.Tollere(ResDensity);
        c3.ReadMask.Tollere(ResPressure);
        c3.ReadMask.Tollere(ResCellHead);
        c3.ReadMask.Tollere(ResNext);
        c3.WriteMask.Tollere(ResForce);

        // Phase 3B (runs IN PARALLEL with Phase 3A!): Surface spray/foam vorticity metric
        var c4 = new Cohors(4, _sprayFoam);
        c4.ReadMask.Tollere(ResVel);
        c4.ReadMask.Tollere(ResDensity);
        c4.WriteMask.Tollere(ResFoam);

        // Phase 4: Symplectic Euler integration + container wall collisions + gentle confinement
        var c5 = new Cohors(5, _integrate);
        c5.ReadMask.Tollere(ResForce);
        c5.WriteMask.Tollere(ResPos);
        c5.WriteMask.Tollere(ResVel);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.RegisterCohors(c4);
        legatus.RegisterCohors(c5);
        legatus.ConstructConsilium();
    }

    public void RunSerial()
    {
        ClearGridRange(0, GridSize);
        HashInsertRange(0, ParticleCount);
        DensityPressureRange(0, ParticleCount);
        SphForceRange(0, ParticleCount);
        SprayFoamRange(0, ParticleCount);
        IntegrateRange(0, ParticleCount);
    }

    public void RunParallelFor()
    {
        Parallel.For(0, GridSize, i => _cellHead[i] = -1);
        Parallel.For(0, ParticleCount, i => HashInsertRange(i, 1));
        Parallel.For(0, ParticleCount, i => DensityPressureRange(i, 1));
        Parallel.For(0, ParticleCount, i => SphForceRange(i, 1));
        Parallel.For(0, ParticleCount, i => SprayFoamRange(i, 1));
        Parallel.For(0, ParticleCount, i => IntegrateRange(i, 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellHash(int cx, int cy, int cz)
    {
        return ((cx * 73856093) ^ (cy * 19349663) ^ (cz * 83492791)) & GridMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NextFloat(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FF_FFFFu) * (1.0f / 16777216.0f);
    }

    private void ClearGridRange(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
            _cellHead[i] = -1;
    }

    private void HashInsertRange(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int cx = (int)(_px[i] * InvCellSize);
            int cy = (int)(_py[i] * InvCellSize);
            int cz = (int)(_pz[i] * InvCellSize);
            int h = CellHash(cx, cy, cz);

            int prev = Interlocked.Exchange(ref _cellHead[h], i);
            _next[i] = prev;
        }
    }

    private void DensityPressureRange(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float xi = _px[i];
            float yi = _py[i];
            float zi = _pz[i];
            int cx = (int)(xi * InvCellSize);
            int cy = (int)(yi * InvCellSize);
            int cz = (int)(zi * InvCellSize);

            float rho = ParticleMass * Poly6Coeff * H2 * H2 * H2;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = _cellHead[CellHash(cx + dx, cy + dy, cz + dz)];
                        int guard = 0;
                        while (j >= 0 && guard < 96)
                        {
                            float rx = xi - _px[j];
                            float ry = yi - _py[j];
                            float rz = zi - _pz[j];
                            float r2 = rx * rx + ry * ry + rz * rz;
                            if (r2 < H2)
                            {
                                float diff = H2 - r2;
                                rho += ParticleMass * Poly6Coeff * diff * diff * diff;
                            }
                            j = _next[j];
                            guard++;
                        }
                    }
                }
            }

            float clampedRho = MathF.Max(RestDensity * 0.5f, rho);
            _density[i] = clampedRho;
            float ratio = clampedRho / RestDensity;
            _pressure[i] = MathF.Max(0.0f, GasStiffness * (ratio * ratio * ratio - 1.0f));
        }
    }

    private void SphForceRange(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float xi = _px[i];
            float yi = _py[i];
            float zi = _pz[i];
            float vxi = _vx[i];
            float vyi = _vy[i];
            float vzi = _vz[i];
            float pi = _pressure[i];
            float rhoI = _density[i];

            int cx = (int)(xi * InvCellSize);
            int cy = (int)(yi * InvCellSize);
            int cz = (int)(zi * InvCellSize);

            float fpx = 0.0f, fpy = 0.0f, fpz = 0.0f;
            float fvx = 0.0f, fvy = 0.0f, fvz = 0.0f;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = _cellHead[CellHash(cx + dx, cy + dy, cz + dz)];
                        int guard = 0;
                        while (j >= 0 && guard < 96)
                        {
                            if (j != i)
                            {
                                float rx = xi - _px[j];
                                float ry = yi - _py[j];
                                float rz = zi - _pz[j];
                                float r2 = rx * rx + ry * ry + rz * rz;
                                if (r2 > 1e-7f && r2 < H2)
                                {
                                    float r = MathF.Sqrt(r2);
                                    float hr = H - r;
                                    float invRhoJ = 1.0f / _density[j];

                                    // Spiky pressure gradient kernel
                                    float pressMag = -ParticleMass * (pi + _pressure[j]) * 0.5f * invRhoJ * SpikyGradCoeff * hr * hr / r;
                                    fpx += rx * pressMag;
                                    fpy += ry * pressMag;
                                    fpz += rz * pressMag;

                                    // Viscosity Laplacian kernel
                                    float viscMag = Viscosity * ParticleMass * invRhoJ * ViscLapCoeff * hr;
                                    fvx += (_vx[j] - vxi) * viscMag;
                                    fvy += (_vy[j] - vyi) * viscMag;
                                    fvz += (_vz[j] - vzi) * viscMag;
                                }
                            }
                            j = _next[j];
                            guard++;
                        }
                    }
                }
            }

            // Gravity + subtle attractor keeping dense dam corner clustered across hundreds of frames
            float invRhoI = 1.0f / rhoI;
            float pullX = i < (ParticleCount * 35) / 100 ? (1.6f - xi) * 1.2f : 0.0f;
            float pullZ = i < (ParticleCount * 35) / 100 ? (1.6f - zi) * 1.2f : 0.0f;
            _fx[i] = (fpx + fvx) * invRhoI + pullX;
            _fy[i] = (fpy + fvy) * invRhoI - 9.81f;
            _fz[i] = (fpz + fvz) * invRhoI + pullZ;
        }
    }

    private void SprayFoamRange(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float speed2 = _vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i];
            float lowDensity = MathF.Max(0.0f, 1.0f - _density[i] / RestDensity);
            _foam[i] = MathF.Sqrt(speed2) * (0.25f + lowDensity);
        }
    }

    private void IntegrateRange(int start, int length)
    {
        int end = start + length;
        const float minBound = 0.2f;
        const float maxBound = DomainSize - 0.2f;
        const float damping = 0.996f;

        for (int i = start; i < end; i++)
        {
            float vx = Math.Clamp((_vx[i] + _fx[i] * Dt) * damping, -12.0f, 12.0f);
            float vy = Math.Clamp((_vy[i] + _fy[i] * Dt) * damping, -12.0f, 12.0f);
            float vz = Math.Clamp((_vz[i] + _fz[i] * Dt) * damping, -12.0f, 12.0f);

            float x = _px[i] + vx * Dt;
            float y = _py[i] + vy * Dt;
            float z = _pz[i] + vz * Dt;

            if (x < minBound) { x = minBound; vx = -vx * 0.5f; }
            else if (x > maxBound) { x = maxBound; vx = -vx * 0.5f; }

            if (y < minBound) { y = minBound; vy = -vy * 0.45f; }
            else if (y > maxBound) { y = maxBound; vy = -vy * 0.45f; }

            if (z < minBound) { z = minBound; vz = -vz * 0.5f; }
            else if (z > maxBound) { z = maxBound; vz = -vz * 0.5f; }

            _px[i] = x; _py[i] = y; _pz[i] = z;
            _vx[i] = vx; _vy[i] = vy; _vz[i] = vz;
        }
    }

    private sealed class ClearGridSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => GridSize;
        protected override void ExecuteBatchLogic(int start, int length) => s.ClearGridRange(start, length);
    }

    private sealed class HashInsertSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.HashInsertRange(start, length);
    }

    private sealed class DensityPressureSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.DensityPressureRange(start, length);
    }

    private sealed class SphForceSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.SphForceRange(start, length);
    }

    private sealed class SprayFoamSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.SprayFoamRange(start, length);
    }

    private sealed class IntegrateSys(int id, FluidSphSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.IntegrateRange(start, length);
    }
}
