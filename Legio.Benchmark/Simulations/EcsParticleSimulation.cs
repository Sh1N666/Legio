using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Realistic 4-Cohort Structure-of-Arrays (SoA) 3D Particle Physics Simulation.
/// Exercises:
/// - Multi-phase DAG scheduling via Vexillum masks (3 phases, 2 cohorts running concurrently in Phase 0)
/// - Spatially skewed neighbor pressure evaluation (dense clusters -> heavy Optio work stealing)
/// - Zero-allocation execution across frames
/// </summary>
public sealed class EcsParticleSimulation
{
    // Vexillum resource bit indices
    private const int ResPos = 0;
    private const int ResVel = 1;
    private const int ResForce = 2;
    private const int ResDensity = 3;
    private const int ResEnergy = 4;

    private const float Dt = 0.016f;
    private const float Bounds = 100.0f;

    public readonly int ParticleCount;

    // SoA component buffers
    private readonly float[] _px, _py, _pz;
    private readonly float[] _vx, _vy, _vz;
    private readonly float[] _fx, _fy, _fz;
    private readonly float[] _pressureX, _pressureY, _pressureZ;
    private readonly float[] _energy;
    private readonly int[] _clusterWeight; // Spatially skewed neighbor work per particle

    // 4 attractors + cluster centroids
    private readonly float[] _attrX = [-35f, 35f, 0f, 15f];
    private readonly float[] _attrY = [20f, -25f, 30f, -10f];
    private readonly float[] _attrZ = [-15f, 25f, -30f, 15f];

    public readonly PriorPilus ForceCohort;
    public readonly PriorPilus PressureCohort;
    public readonly PriorPilus IntegrateCohort;
    public readonly PriorPilus BoundsCohort;

    public EcsParticleSimulation(int particleCount)
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
        _pressureX = new float[particleCount];
        _pressureY = new float[particleCount];
        _pressureZ = new float[particleCount];
        _energy = new float[particleCount];
        _clusterWeight = new int[particleCount];

        var rng = new Random(1337);
        for (int i = 0; i < particleCount; i++)
        {
            _px[i] = ((float)rng.NextDouble() * 2f - 1f) * Bounds * 0.8f;
            _py[i] = ((float)rng.NextDouble() * 2f - 1f) * Bounds * 0.8f;
            _pz[i] = ((float)rng.NextDouble() * 2f - 1f) * Bounds * 0.8f;
            _vx[i] = ((float)rng.NextDouble() * 2f - 1f) * 5f;
            _vy[i] = ((float)rng.NextDouble() * 2f - 1f) * 5f;
            _vz[i] = ((float)rng.NextDouble() * 2f - 1f) * 5f;

            // Spatial skew: first 15% of entities reside in a dense core cluster (24x more neighbor checks)
            _clusterWeight[i] = i < (particleCount * 3 / 20) ? 48 : 2;
        }

        ForceCohort = new GravityAndVortexCohort(0, this);
        PressureCohort = new ClusteredPressureCohort(1, this);
        IntegrateCohort = new SymplecticIntegrationCohort(2, this);
        BoundsCohort = new BoundsAndEnergyCohort(3, this);
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0A: Reads Pos, Vel -> Writes Force
        var c0 = new Cohors(0, ForceCohort);
        c0.ReadMask.Tollere(ResPos);
        c0.ReadMask.Tollere(ResVel);
        c0.WriteMask.Tollere(ResForce);

        // Phase 0B (runs in parallel with 0A!): Reads Pos -> Writes Density
        var c1 = new Cohors(1, PressureCohort);
        c1.ReadMask.Tollere(ResPos);
        c1.WriteMask.Tollere(ResDensity);

        // Phase 1 (depends on both 0A and 0B): Reads Force, Density -> Writes Pos, Vel
        var c2 = new Cohors(2, IntegrateCohort);
        c2.ReadMask.Tollere(ResForce);
        c2.ReadMask.Tollere(ResDensity);
        c2.WriteMask.Tollere(ResPos);
        c2.WriteMask.Tollere(ResVel);

        // Phase 2 (depends on Phase 1): Reads/Writes Pos, Vel -> Writes Energy
        var c3 = new Cohors(3, BoundsCohort);
        c3.ReadMask.Tollere(ResPos);
        c3.ReadMask.Tollere(ResVel);
        c3.WriteMask.Tollere(ResPos);
        c3.WriteMask.Tollere(ResVel);
        c3.WriteMask.Tollere(ResEnergy);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.ConstructConsilium();
    }

    internal void StepForce(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            float fx = -0.05f * _vx[i];
            float fy = -0.05f * _vy[i] - 9.81f;
            float fz = -0.05f * _vz[i];

            for (int a = 0; a < 4; a++)
            {
                float dx = _attrX[a] - px;
                float dy = _attrY[a] - py;
                float dz = _attrZ[a] - pz;
                float distSq = dx * dx + dy * dy + dz * dz + 4.0f;
                float invDist = 1.0f / MathF.Sqrt(distSq);
                float pull = 120.0f * invDist * invDist * invDist;

                // Gravity + tangential curl vortex
                fx += dx * pull - dz * invDist * 1.5f;
                fy += dy * pull;
                fz += dz * pull + dx * invDist * 1.5f;
            }

            _fx[i] = fx;
            _fy[i] = fy;
            _fz[i] = fz;
        }
    }

    internal void StepPressure(int start, int length)
    {
        int end = start + length;
        int n = ParticleCount;

        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            int neighbors = _clusterWeight[i];
            float rx = 0f, ry = 0f, rz = 0f;

            // SPH-style local repulsion kernel against strided neighborhood
            int baseIdx = (i * 13) % n;
            for (int k = 0; k < neighbors; k++)
            {
                int j = baseIdx + k;
                if (j >= n) j -= n;

                float dx = px - _px[j];
                float dy = py - _py[j];
                float dz = pz - _pz[j];
                float distSq = dx * dx + dy * dy + dz * dz + 1.0f;
                if (distSq < 400.0f)
                {
                    float inv = 1.0f / MathF.Sqrt(distSq);
                    float w = (20.0f - (1.0f / inv)) * inv * 0.15f;
                    rx += dx * w;
                    ry += dy * w;
                    rz += dz * w;
                }
            }

            _pressureX[i] = rx;
            _pressureY[i] = ry;
            _pressureZ[i] = rz;
        }
    }

    internal void StepIntegrate(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float vx = _vx[i] + (_fx[i] + _pressureX[i]) * Dt;
            float vy = _vy[i] + (_fy[i] + _pressureY[i]) * Dt;
            float vz = _vz[i] + (_fz[i] + _pressureZ[i]) * Dt;

            _vx[i] = vx;
            _vy[i] = vy;
            _vz[i] = vz;

            _px[i] += vx * Dt;
            _py[i] += vy * Dt;
            _pz[i] += vz * Dt;
        }
    }

    internal void StepBoundsAndEnergy(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            float vx = _vx[i], vy = _vy[i], vz = _vz[i];

            if (px > Bounds) { px = Bounds; vx = -vx * 0.85f; }
            else if (px < -Bounds) { px = -Bounds; vx = -vx * 0.85f; }

            if (py > Bounds) { py = Bounds; vy = -vy * 0.85f; }
            else if (py < -Bounds) { py = -Bounds; vy = -vy * 0.85f; }

            if (pz > Bounds) { pz = Bounds; vz = -vz * 0.85f; }
            else if (pz < -Bounds) { pz = -Bounds; vz = -vz * 0.85f; }

            _px[i] = px; _py[i] = py; _pz[i] = pz;
            _vx[i] = vx; _vy[i] = vy; _vz[i] = vz;
            _energy[i] = 0.5f * (vx * vx + vy * vy + vz * vz);
        }
    }

    public void RunSerial()
    {
        StepForce(0, ParticleCount);
        StepPressure(0, ParticleCount);
        StepIntegrate(0, ParticleCount);
        StepBoundsAndEnergy(0, ParticleCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        const int chunk = 2048;
        int blocks = (ParticleCount + chunk - 1) / chunk;

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, ParticleCount - s);
            StepForce(s, len);
            StepPressure(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, ParticleCount - s);
            StepIntegrate(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, ParticleCount - s);
            StepBoundsAndEnergy(s, len);
        });
    }

    private sealed class GravityAndVortexCohort(int id, EcsParticleSimulation sim) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => sim.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => sim.StepForce(start, length);
    }

    private sealed class ClusteredPressureCohort(int id, EcsParticleSimulation sim) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => sim.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => sim.StepPressure(start, length);
    }

    private sealed class SymplecticIntegrationCohort(int id, EcsParticleSimulation sim) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => sim.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => sim.StepIntegrate(start, length);
    }

    private sealed class BoundsAndEnergyCohort(int id, EcsParticleSimulation sim) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => sim.ParticleCount;
        protected override void ExecuteBatchLogic(int start, int length) => sim.StepBoundsAndEnergy(start, length);
    }
}
