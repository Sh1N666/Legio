using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Brutal O(N^2) All-Pairs Astrophysical N-Body Gravity + Soft-Core Collision Simulation
/// (3,072 bodies -> 9,437,184 pairwise force evaluations per frame!).
/// Exercises:
/// - Extreme sustained FPU + L1/L2 streaming load across all physical &amp; SMT execution units
/// - 4 Cohorts, 3 DAG Phases:
///     Phase 0A: O(N^2) All-Pairs Gravity &amp; Short-Range Repulsion (R: Pos, Mass -> W: Force)
///     Phase 0B: Thermal Radiation &amp; Tidal Heating (parallel with 0A! R: Pos, Vel -> W: Temp)
///     Phase 1 : Symplectic Leapfrog Integration (R: Force, Temp -> W: Pos, Vel)
///     Phase 2 : Virial Energy &amp; Orbital Stability Diagnostics (R: Pos, Vel, Mass -> W: Diag)
/// - Strictly 0 B GC allocation.
/// </summary>
public sealed class NBodyGravitySimulation
{
    private const int ResPos   = 0;
    private const int ResVel   = 1;
    private const int ResMass  = 2;
    private const int ResForce = 3;
    private const int ResTemp  = 4;
    private const int ResDiag  = 5;

    private const float Dt = 0.005f;
    private const float SofteningSq = 0.25f;

    public readonly int BodyCount;

    private readonly float[] _px, _py, _pz;
    private readonly float[] _vx, _vy, _vz;
    private readonly float[] _fx, _fy, _fz;
    private readonly float[] _mass;
    private readonly float[] _temp;
    private readonly float[] _virialEnergy;

    private readonly PriorPilus[] _cohorts;

    public NBodyGravitySimulation(int bodyCount = 3_072)
    {
        BodyCount = bodyCount;
        _px = new float[bodyCount];
        _py = new float[bodyCount];
        _pz = new float[bodyCount];
        _vx = new float[bodyCount];
        _vy = new float[bodyCount];
        _vz = new float[bodyCount];
        _fx = new float[bodyCount];
        _fy = new float[bodyCount];
        _fz = new float[bodyCount];
        _mass = new float[bodyCount];
        _temp = new float[bodyCount];
        _virialEnergy = new float[bodyCount];

        var rng = new Random(314159);
        for (int i = 0; i < bodyCount; i++)
        {
            float angle = (float)rng.NextDouble() * 6.283185f;
            float radius = 2.0f + MathF.Pow((float)rng.NextDouble(), 0.7f) * 45.0f;
            _px[i] = MathF.Cos(angle) * radius;
            _py[i] = ((float)rng.NextDouble() * 2f - 1f) * 4.0f;
            _pz[i] = MathF.Sin(angle) * radius;

            // Tangential orbital velocity
            float orbitalSpeed = MathF.Sqrt(180.0f / (radius + 2.0f));
            _vx[i] = -MathF.Sin(angle) * orbitalSpeed;
            _vy[i] = ((float)rng.NextDouble() * 2f - 1f) * 0.2f;
            _vz[i] = MathF.Cos(angle) * orbitalSpeed;

            _mass[i] = (i < 16) ? 120.0f : (0.5f + (float)rng.NextDouble() * 2.5f);
            _temp[i] = 300.0f + (float)rng.NextDouble() * 1200.0f;
        }

        _cohorts =
        [
            new DelegateCohort(0, () => BodyCount, StepAllPairsGravity),
            new DelegateCohort(1, () => BodyCount, StepThermalRadiation),
            new DelegateCohort(2, () => BodyCount, StepLeapfrogIntegrate),
            new DelegateCohort(3, () => BodyCount, StepVirialDiagnostics)
        ];
    }

    public void RegisterAll(Legatus legatus)
    {
        var c0 = new Cohors(0, _cohorts[0]);
        c0.ReadMask.Tollere(ResPos);
        c0.ReadMask.Tollere(ResMass);
        c0.WriteMask.Tollere(ResForce);

        var c1 = new Cohors(1, _cohorts[1]);
        c1.ReadMask.Tollere(ResPos);
        c1.ReadMask.Tollere(ResVel);
        c1.WriteMask.Tollere(ResTemp);

        var c2 = new Cohors(2, _cohorts[2]);
        c2.ReadMask.Tollere(ResForce);
        c2.ReadMask.Tollere(ResTemp);
        c2.WriteMask.Tollere(ResPos);
        c2.WriteMask.Tollere(ResVel);

        var c3 = new Cohors(3, _cohorts[3]);
        c3.ReadMask.Tollere(ResPos);
        c3.ReadMask.Tollere(ResVel);
        c3.ReadMask.Tollere(ResMass);
        c3.WriteMask.Tollere(ResDiag);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.ConstructConsilium();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepAllPairsGravity(int start, int length)
    {
        int end = start + length;
        int n = BodyCount;
        float[] px = _px, py = _py, pz = _pz, mass = _mass;

        for (int i = start; i < end; i++)
        {
            float xi = px[i], yi = py[i], zi = pz[i];
            float fx = 0f, fy = 0f, fz = 0f;

            for (int j = 0; j < n; j++)
            {
                float dx = px[j] - xi;
                float dy = py[j] - yi;
                float dz = pz[j] - zi;
                float distSq = dx * dx + dy * dy + dz * dz + SofteningSq;
                float invDist = 1.0f / MathF.Sqrt(distSq);
                float invDist3 = invDist * invDist * invDist;
                float s = mass[j] * invDist3;

                // Short-range soft-sphere collision repulsion
                if (distSq < 1.25f && j != i)
                {
                    s -= 3.5f * (1.25f - distSq) * invDist;
                }

                fx += dx * s;
                fy += dy * s;
                fz += dz * s;
            }

            _fx[i] = fx;
            _fy[i] = fy;
            _fz[i] = fz;
        }
    }

    private void StepThermalRadiation(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float rSq = _px[i] * _px[i] + _py[i] * _py[i] + _pz[i] * _pz[i] + 4.0f;
            float vSq = _vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i];
            float t = _temp[i];
            float cooling = 1e-11f * t * t * t * t;
            float tidalHeat = (250.0f / rSq) + 0.08f * vSq;
            _temp[i] = Math.Clamp(t + (tidalHeat - cooling) * Dt, 50.0f, 9000.0f);
        }
    }

    private void StepLeapfrogIntegrate(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float thermalDamp = 1.0f - MathF.Min(0.01f, _temp[i] * 1e-7f);
            float vx = (_vx[i] + _fx[i] * Dt) * thermalDamp;
            float vy = (_vy[i] + _fy[i] * Dt) * thermalDamp;
            float vz = (_vz[i] + _fz[i] * Dt) * thermalDamp;

            _vx[i] = vx;
            _vy[i] = vy;
            _vz[i] = vz;

            _px[i] += vx * Dt;
            _py[i] += vy * Dt;
            _pz[i] += vz * Dt;
        }
    }

    private void StepVirialDiagnostics(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float vSq = _vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i];
            float r = MathF.Sqrt(_px[i] * _px[i] + _py[i] * _py[i] + _pz[i] * _pz[i] + 1.0f);
            _virialEnergy[i] = 0.5f * _mass[i] * vSq - (180.0f * _mass[i] / r);
        }
    }

    public void RunSerial()
    {
        StepAllPairsGravity(0, BodyCount);
        StepThermalRadiation(0, BodyCount);
        StepLeapfrogIntegrate(0, BodyCount);
        StepVirialDiagnostics(0, BodyCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        const int chunk = 96;
        int blocks = (BodyCount + chunk - 1) / chunk;

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, BodyCount - s);
            StepAllPairsGravity(s, len);
            StepThermalRadiation(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, BodyCount - s);
            StepLeapfrogIntegrate(s, len);
        });

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, BodyCount - s);
            StepVirialDiagnostics(s, len);
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
