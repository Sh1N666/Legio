using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Brutal CPU-Bound Stochastic Monte Carlo Path Tracer (240x135 = 32,400 pixels x 6 SPP x up to 6 bounces).
/// Exercises:
/// - Pure 100% FPU / transcendental / branch-heavy ray-sphere intersection across 16 analytic materials
///   (Lambertian matte, polished metal, Schlick-Fresnel glass dielectric with Snell refraction, emissive area light)
/// - Russian Roulette path termination creating authentic stochastic per-pixel runtime variance
/// - 2-Phase Rendering DAG:
///     Phase 0: Multi-Bounce Path Tracing -> W(HdrRgb)
///     Phase 1: 3x3 Bilateral Edge-Preserving Denoise + ACES Filmic Tone Mapping -> R(HdrRgb), W(LdrOut)
/// - Strictly 0 B GC allocation (stack-only ray state + deterministic per-pixel Xorshift32).
/// </summary>
public sealed class MonteCarloPathTracingSimulation
{
    private const int ResHdrBuffer = 0;
    private const int ResLdrOutput = 1;

    private const int MatDiffuse  = 0;
    private const int MatMetal    = 1;
    private const int MatGlass    = 2;
    private const int MatEmissive = 3;

    public readonly int Width;
    public readonly int Height;
    public readonly int PixelCount;
    public readonly int SamplesPerPixel;
    public readonly int MaxBounces;

    // Scene geometry (SoA for cache-friendly inner loop)
    private readonly int _sphereCount;
    private readonly float[] _sx, _sy, _sz, _sr, _srSq;
    private readonly float[] _albedoR, _albedoG, _albedoB;
    private readonly float[] _roughness;
    private readonly int[] _material;

    // Framebuffers
    private readonly float[] _hdrR, _hdrG, _hdrB;
    private readonly uint[] _ldrRgba;

    private readonly PriorPilus _traceCohort;
    private readonly PriorPilus _postCohort;

    public MonteCarloPathTracingSimulation(int width = 240, int height = 135, int spp = 6, int maxBounces = 6)
    {
        Width = width;
        Height = height;
        PixelCount = width * height;
        SamplesPerPixel = spp;
        MaxBounces = maxBounces;

        _hdrR = new float[PixelCount];
        _hdrG = new float[PixelCount];
        _hdrB = new float[PixelCount];
        _ldrRgba = new uint[PixelCount];

        // 16-sphere Cornell-inspired studio scene with heavy glass/metal reflections in center
        _sphereCount = 16;
        _sx = new float[_sphereCount];
        _sy = new float[_sphereCount];
        _sz = new float[_sphereCount];
        _sr = new float[_sphereCount];
        _srSq = new float[_sphereCount];
        _albedoR = new float[_sphereCount];
        _albedoG = new float[_sphereCount];
        _albedoB = new float[_sphereCount];
        _roughness = new float[_sphereCount];
        _material = new int[_sphereCount];

        // Ground & overhead emissive light
        SetSphere(0,  0.0f, -100.5f, -3.0f, 100.0f, 0.55f, 0.56f, 0.60f, 0.0f, MatDiffuse);
        SetSphere(1,  0.0f,    5.2f, -3.2f,   2.2f, 9.50f, 8.80f, 7.20f, 0.0f, MatEmissive);

        // Hero cluster in screen center -> intense multi-bounce refraction & reflection skew!
        SetSphere(2,  0.0f,  0.35f, -3.0f, 0.85f, 0.98f, 0.99f, 1.00f, 1.50f, MatGlass);
        SetSphere(3, -1.6f,  0.25f, -3.4f, 0.75f, 0.92f, 0.72f, 0.35f, 0.05f, MatMetal);
        SetSphere(4,  1.6f,  0.25f, -3.2f, 0.75f, 0.82f, 0.22f, 0.20f, 0.00f, MatDiffuse);
        SetSphere(5, -0.7f, -0.15f, -1.9f, 0.35f, 0.96f, 0.97f, 1.00f, 1.52f, MatGlass);
        SetSphere(6,  0.75f,-0.15f, -2.0f, 0.35f, 0.25f, 0.78f, 0.88f, 0.12f, MatMetal);

        // Surrounding orbiting spheres creating rich GI inter-reflections
        var rng = new Random(2026);
        for (int i = 7; i < _sphereCount; i++)
        {
            float angle = (i - 7) * (6.283185f / (_sphereCount - 7));
            float dist = 2.6f + (i % 3) * 0.7f;
            float rad = 0.28f + (i % 3) * 0.12f;
            int mat = (i % 3) switch { 0 => MatDiffuse, 1 => MatMetal, _ => MatGlass };
            SetSphere(
                i,
                MathF.Cos(angle) * dist,
                -0.5f + rad,
                -3.2f + MathF.Sin(angle) * dist * 0.65f,
                rad,
                0.3f + (float)rng.NextDouble() * 0.65f,
                0.3f + (float)rng.NextDouble() * 0.65f,
                0.3f + (float)rng.NextDouble() * 0.65f,
                mat == MatGlass ? 1.5f : 0.08f + (float)rng.NextDouble() * 0.25f,
                mat);
        }

        _traceCohort = new DelegateCohort(0, () => PixelCount, StepPathTrace);
        _postCohort = new DelegateCohort(1, () => PixelCount, StepDenoiseAndToneMap);
    }

    private void SetSphere(int idx, float x, float y, float z, float r, float cr, float cg, float cb, float rough, int mat)
    {
        _sx[idx] = x; _sy[idx] = y; _sz[idx] = z;
        _sr[idx] = r; _srSq[idx] = r * r;
        _albedoR[idx] = cr; _albedoG[idx] = cg; _albedoB[idx] = cb;
        _roughness[idx] = rough;
        _material[idx] = mat;
    }

    public void RegisterAll(Legatus legatus)
    {
        var c0 = new Cohors(0, _traceCohort);
        c0.WriteMask.Tollere(ResHdrBuffer);

        var c1 = new Cohors(1, _postCohort);
        c1.ReadMask.Tollere(ResHdrBuffer);
        c1.WriteMask.Tollere(ResLdrOutput);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.ConstructConsilium();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StepPathTrace(int start, int length)
    {
        int end = start + length;
        float invW = 1.0f / Width;
        float invH = 1.0f / Height;
        float aspect = (float)Width / Height;
        float invSpp = 1.0f / SamplesPerPixel;

        float[] sx = _sx, sy = _sy, sz = _sz, sr = _sr, srSq = _srSq;
        float[] ar = _albedoR, ag = _albedoG, ab = _albedoB, rough = _roughness;
        int[] mats = _material;
        int sphereCount = _sphereCount;

        for (int idx = start; idx < end; idx++)
        {
            int py = idx / Width;
            int px = idx - py * Width;
            uint rng = (uint)(idx * 747796405u + 2891336453u) | 1u;

            float accR = 0f, accG = 0f, accB = 0f;

            for (int s = 0; s < SamplesPerPixel; s++)
            {
                float u = (px + NextFloat(ref rng)) * invW;
                float v = 1.0f - (py + NextFloat(ref rng)) * invH;

                // Camera at (0, 0.9, 1.35) looking toward (0, 0.2, -3.0)
                float ox = 0.0f, oy = 0.9f, oz = 1.35f;
                float dx = (2.0f * u - 1.0f) * aspect * 0.62f;
                float dy = (2.0f * v - 1.0f) * 0.62f - 0.16f;
                float dz = -1.0f;
                float dInv = 1.0f / MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                dx *= dInv; dy *= dInv; dz *= dInv;

                float thrR = 1.0f, thrG = 1.0f, thrB = 1.0f;

                for (int bounce = 0; bounce < MaxBounces; bounce++)
                {
                    float closestT = 1e20f;
                    int hitIdx = -1;

                    for (int k = 0; k < sphereCount; k++)
                    {
                        float ocx = ox - sx[k];
                        float ocy = oy - sy[k];
                        float ocz = oz - sz[k];
                        float b = ocx * dx + ocy * dy + ocz * dz;
                        float c = ocx * ocx + ocy * ocy + ocz * ocz - srSq[k];
                        float disc = b * b - c;
                        if (disc > 0.0f)
                        {
                            float sqrtD = MathF.Sqrt(disc);
                            float t = -b - sqrtD;
                            if (t < 0.001f) t = -b + sqrtD;
                            if (t > 0.001f && t < closestT)
                            {
                                closestT = t;
                                hitIdx = k;
                            }
                        }
                    }

                    if (hitIdx < 0)
                    {
                        // Sky gradient environment light
                        float skyT = 0.5f * (dy + 1.0f);
                        float skyR = (1.0f - skyT) * 0.85f + skyT * 0.28f;
                        float skyG = (1.0f - skyT) * 0.90f + skyT * 0.48f;
                        float skyB = (1.0f - skyT) * 0.98f + skyT * 0.88f;
                        accR += thrR * skyR;
                        accG += thrG * skyG;
                        accB += thrB * skyB;
                        break;
                    }

                    int mat = mats[hitIdx];
                    if (mat == MatEmissive)
                    {
                        accR += thrR * ar[hitIdx];
                        accG += thrG * ag[hitIdx];
                        accB += thrB * ab[hitIdx];
                        break;
                    }

                    // Hit point & outward normal
                    float hx = ox + dx * closestT;
                    float hy = oy + dy * closestT;
                    float hz = oz + dz * closestT;
                    float invR = 1.0f / sr[hitIdx];
                    float nx = (hx - sx[hitIdx]) * invR;
                    float ny = (hy - sy[hitIdx]) * invR;
                    float nz = (hz - sz[hitIdx]) * invR;

                    bool frontFace = (dx * nx + dy * ny + dz * nz) < 0.0f;
                    if (!frontFace) { nx = -nx; ny = -ny; nz = -nz; }

                    // Random unit vector (rejection-free spherical sampling)
                    float zRand = NextFloat(ref rng) * 2.0f - 1.0f;
                    float phi = NextFloat(ref rng) * 6.2831853f;
                    float rPlanar = MathF.Sqrt(MathF.Max(0.0f, 1.0f - zRand * zRand));
                    float rx = rPlanar * MathF.Cos(phi);
                    float ry = rPlanar * MathF.Sin(phi);
                    float rz = zRand;

                    if (mat == MatDiffuse)
                    {
                        dx = nx + rx;
                        dy = ny + ry;
                        dz = nz + rz;
                        thrR *= ar[hitIdx];
                        thrG *= ag[hitIdx];
                        thrB *= ab[hitIdx];
                    }
                    else if (mat == MatMetal)
                    {
                        float dot = dx * nx + dy * ny + dz * nz;
                        float fuzz = rough[hitIdx];
                        dx = dx - 2.0f * dot * nx + fuzz * rx;
                        dy = dy - 2.0f * dot * ny + fuzz * ry;
                        dz = dz - 2.0f * dot * nz + fuzz * rz;
                        if (dx * nx + dy * ny + dz * nz <= 0.0f)
                            break;

                        thrR *= ar[hitIdx];
                        thrG *= ag[hitIdx];
                        thrB *= ab[hitIdx];
                    }
                    else // MatGlass (Dielectric with Schlick Fresnel + Snell Refraction)
                    {
                        float ior = rough[hitIdx];
                        float ratio = frontFace ? (1.0f / ior) : ior;
                        float cosTheta = MathF.Min(-(dx * nx + dy * ny + dz * nz), 1.0f);
                        float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
                        float r0 = (1.0f - ratio) / (1.0f + ratio);
                        r0 *= r0;
                        float oneMinus = 1.0f - cosTheta;
                        float schlick = r0 + (1.0f - r0) * oneMinus * oneMinus * oneMinus * oneMinus * oneMinus;

                        if (ratio * sinTheta > 1.0f || schlick > NextFloat(ref rng))
                        {
                            float dot = dx * nx + dy * ny + dz * nz;
                            dx -= 2.0f * dot * nx;
                            dy -= 2.0f * dot * ny;
                            dz -= 2.0f * dot * nz;
                        }
                        else
                        {
                            float perpX = ratio * (dx + cosTheta * nx);
                            float perpY = ratio * (dy + cosTheta * ny);
                            float perpZ = ratio * (dz + cosTheta * nz);
                            float perpSq = perpX * perpX + perpY * perpY + perpZ * perpZ;
                            float para = -MathF.Sqrt(MathF.Max(0.0f, 1.0f - perpSq));
                            dx = perpX + para * nx;
                            dy = perpY + para * ny;
                            dz = perpZ + para * nz;
                        }
                    }

                    float lenInv = 1.0f / MathF.Sqrt(dx * dx + dy * dy + dz * dz + 1e-8f);
                    dx *= lenInv; dy *= lenInv; dz *= lenInv;
                    ox = hx + dx * 0.002f;
                    oy = hy + dy * 0.002f;
                    oz = hz + dz * 0.002f;

                    // Russian Roulette termination after bounce 2 (authentic stochastic work variance!)
                    if (bounce >= 2)
                    {
                        float pSurvive = MathF.Min(0.92f, MathF.Max(thrR, MathF.Max(thrG, thrB)));
                        if (NextFloat(ref rng) > pSurvive)
                            break;
                        float invP = 1.0f / pSurvive;
                        thrR *= invP; thrG *= invP; thrB *= invP;
                    }
                }
            }

            _hdrR[idx] = accR * invSpp;
            _hdrG[idx] = accG * invSpp;
            _hdrB[idx] = accB * invSpp;
        }
    }

    private void StepDenoiseAndToneMap(int start, int length)
    {
        int end = start + length;
        int w = Width;
        int h = Height;

        for (int idx = start; idx < end; idx++)
        {
            int y = idx / w;
            int x = idx - y * w;

            float cr = _hdrR[idx], cg = _hdrG[idx], cb = _hdrB[idx];
            float sumR = cr, sumG = cg, sumB = cb, wSum = 1.0f;

            // 3x3 edge-preserving bilateral denoise
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if ((uint)ny >= (uint)h) continue;
                int row = ny * w;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    if ((uint)nx >= (uint)w) continue;
                    int j = row + nx;
                    float nr = _hdrR[j], ng = _hdrG[j], nb = _hdrB[j];
                    float dr = nr - cr, dg = ng - cg, db = nb - cb;
                    float rangeW = MathF.Exp(-(dr * dr + dg * dg + db * db) * 4.5f) * 0.5f;
                    sumR += nr * rangeW;
                    sumG += ng * rangeW;
                    sumB += nb * rangeW;
                    wSum += rangeW;
                }
            }

            float invW = 1.0f / wSum;
            uint r8 = AcesToByte(sumR * invW);
            uint g8 = AcesToByte(sumG * invW);
            uint b8 = AcesToByte(sumB * invW);
            _ldrRgba[idx] = 0xFF000000u | (r8 << 16) | (g8 << 8) | b8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AcesToByte(float x)
    {
        // ACES filmic tone curve + gamma 2.0 fast approximation
        float mapped = (x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f);
        float gamma = MathF.Sqrt(Math.Clamp(mapped, 0.0f, 1.0f));
        return (uint)(gamma * 255.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float NextFloat(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FFFFFFu) * (1.0f / 16777216.0f);
    }

    public void RunSerial()
    {
        StepPathTrace(0, PixelCount);
        StepDenoiseAndToneMap(0, PixelCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        const int traceChunk = 256;
        int traceBlocks = (PixelCount + traceChunk - 1) / traceChunk;
        Parallel.For(0, traceBlocks, opts, b =>
        {
            int s = b * traceChunk;
            StepPathTrace(s, Math.Min(traceChunk, PixelCount - s));
        });

        const int postChunk = 1024;
        int postBlocks = (PixelCount + postChunk - 1) / postChunk;
        Parallel.For(0, postBlocks, opts, b =>
        {
            int s = b * postChunk;
            StepDenoiseAndToneMap(s, Math.Min(postChunk, PixelCount - s));
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
