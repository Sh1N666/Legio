using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Non-uniform 3D Signed Distance Field (SDF) Raymarching + Soft-Shadow Simulation.
/// Rays intersecting the central torus/sphere geometry take up to 80 marching steps + normal/shadow probes,
/// while outer sky rays exit early in ~6 steps -> strong spatial load imbalance ideal for testing
/// Optio work stealing and online ML batch-size adaptation as the camera orbits.
/// </summary>
public sealed class RaymarchSdfSimulation : PriorPilus
{
    public readonly int Width;
    public readonly int Height;
    public readonly int PixelCount;

    private readonly float[] _luminanceBuffer;
    private float _cameraAngle;

    public RaymarchSdfSimulation(int id, int width, int height) : base(id)
    {
        Width = width;
        Height = height;
        PixelCount = width * height;
        _luminanceBuffer = new float[PixelCount];
    }

    public void AdvanceCamera()
    {
        _cameraAngle += 0.03f;
    }

    protected override int GetTotalItemCount() => PixelCount;

    protected override void ExecuteBatchLogic(int start, int length)
    {
        float angle = _cameraAngle;
        float camX = MathF.Sin(angle) * 4.2f;
        float camY = 1.4f;
        float camZ = MathF.Cos(angle) * 4.2f;

        // Forward vector toward origin
        float invLen = 1.0f / MathF.Sqrt(camX * camX + camY * camY + camZ * camZ);
        float fwdX = -camX * invLen, fwdY = -camY * invLen, fwdZ = -camZ * invLen;

        // Right vector = normalize(cross(fwd, up))
        float rx = -fwdZ, rz = fwdX;
        float rInv = 1.0f / MathF.Sqrt(rx * rx + rz * rz + 1e-6f);
        rx *= rInv; rz *= rInv;

        // Camera Up = cross(right, fwd)
        float ux = -rz * fwdY;
        float uy = rz * fwdX - rx * fwdZ;
        float uz = rx * fwdY;

        float invW = 2.0f / Width;
        float invH = 2.0f / Height;
        float aspect = (float)Width / Height;

        int end = start + length;
        for (int idx = start; idx < end; idx++)
        {
            int y = idx / Width;
            int x = idx - y * Width;

            float ndcX = (x * invW - 1.0f) * aspect;
            float ndcY = 1.0f - y * invH;

            float dx = fwdX * 1.6f + rx * ndcX + ux * ndcY;
            float dy = fwdY * 1.6f + uy * ndcY;
            float dz = fwdZ * 1.6f + rz * ndcX + uz * ndcY;
            float dInv = 1.0f / MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            dx *= dInv; dy *= dInv; dz *= dInv;

            float t = 0.0f;
            bool hit = false;

            for (int step = 0; step < 80; step++)
            {
                float px = camX + dx * t;
                float py = camY + dy * t;
                float pz = camZ + dz * t;

                float dist = SceneSdf(px, py, pz);
                if (dist < 0.002f)
                {
                    hit = true;
                    break;
                }

                t += dist;
                if (t > 14.0f)
                    break;
            }

            if (hit)
            {
                float px = camX + dx * t;
                float py = camY + dy * t;
                float pz = camZ + dz * t;

                // Finite-difference surface normal + soft directional lighting
                const float eps = 0.002f;
                float nx = SceneSdf(px + eps, py, pz) - SceneSdf(px - eps, py, pz);
                float ny = SceneSdf(px, py + eps, pz) - SceneSdf(px, py - eps, pz);
                float nz = SceneSdf(px, py, pz + eps) - SceneSdf(px, py, pz - eps);
                float nInv = 1.0f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-8f);
                nx *= nInv; ny *= nInv; nz *= nInv;

                float diff = MathF.Max(0.0f, nx * 0.577f + ny * 0.707f + nz * 0.408f);
                float shadow = SoftShadow(px + nx * 0.02f, py + ny * 0.02f, pz + nz * 0.02f);
                _luminanceBuffer[idx] = 0.12f + diff * shadow * 0.88f;
            }
            else
            {
                _luminanceBuffer[idx] = 0.05f + 0.15f * MathF.Max(0.0f, dy);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SceneSdf(float x, float y, float z)
    {
        // Central torus
        float qx = MathF.Sqrt(x * x + z * z) - 1.35f;
        float torus = MathF.Sqrt(qx * qx + y * y) - 0.42f;

        // Orbiting spheres (smooth min blend)
        float s1 = MathF.Sqrt((x - 1.1f) * (x - 1.1f) + (y - 0.5f) * (y - 0.5f) + z * z) - 0.55f;
        float s2 = MathF.Sqrt((x + 1.1f) * (x + 1.1f) + (y + 0.4f) * (y + 0.4f) + z * z) - 0.48f;

        // Ground plane with subtle wave ripple
        float plane = y + 1.1f + 0.08f * MathF.Sin(x * 2.5f) * MathF.Cos(z * 2.5f);

        float d = SmoothMin(torus, s1, 0.25f);
        d = SmoothMin(d, s2, 0.25f);
        return MathF.Min(d, plane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothMin(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0.0f) / k;
        return MathF.Min(a, b) - h * h * k * 0.25f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftShadow(float ox, float oy, float oz)
    {
        float res = 1.0f;
        float t = 0.05f;
        for (int i = 0; i < 16; i++)
        {
            float h = SceneSdf(ox + 0.577f * t, oy + 0.707f * t, oz + 0.408f * t);
            if (h < 0.001f) return 0.0f;
            res = MathF.Min(res, 8.0f * h / t);
            t += h;
            if (t > 8.0f) break;
        }
        return res;
    }

    public void Register(Legatus legatus)
    {
        var cohors = new Cohors(0, this);
        cohors.WriteMask.Tollere(0);
        legatus.RegisterCohors(cohors);
        legatus.ConstructConsilium();
    }

    public void RunSerial() => ExecuteBatchLogic(0, PixelCount);

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        const int chunk = 512;
        int blocks = (PixelCount + chunk - 1) / chunk;

        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, PixelCount - s);
            ExecuteBatchLogic(s, len);
        });
    }
}
