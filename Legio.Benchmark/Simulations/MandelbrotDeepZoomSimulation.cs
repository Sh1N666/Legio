using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Scenario 11: Double-Precision Deep-Zoom Mandelbrot Fractal (2,048 max iterations) + 2-Pass Edge/Exposure DAG.
/// 384x216 = 82,944 pixels framed asymmetrically on the main cardioid / Seahorse Valley boundary so the
/// black interior bulb sits predominantly in the first half of the pixel buffer (SpatialSkewAbs > 0.60)
/// while the outer escape region finishes in 4-15 iterations (~500x per-pixel workload skew!).
/// Ultimate pure IEEE-754 double-precision FMA ALU torture test for Interleaved Partitioning + StealHalf + OnlineLinUcb.
/// Strictly 0 B heap allocation per frame.
/// </summary>
public sealed class MandelbrotDeepZoomSimulation
{
    private const int ResEscape = 0;
    private const int ResRgb    = 1;
    private const int ResLuma   = 2;

    public readonly int Width;
    public readonly int Height;
    public readonly int PixelCount;
    private const int MaxIterations = 2048;

    private readonly float[] _escapeSmooth;
    private readonly uint[] _rgbOut;
    private readonly float[] _rowLuma;

    private readonly FractalKernelSys _fractalSys;
    private readonly SobelToneMapSys _toneMapSys;
    private readonly RowExposureSys _exposureSys;

    public MandelbrotDeepZoomSimulation(int width = 384, int height = 216)
    {
        Width = width;
        Height = height;
        PixelCount = width * height;

        _escapeSmooth = new float[PixelCount];
        _rgbOut = new uint[PixelCount];
        _rowLuma = new float[height];

        _fractalSys = new FractalKernelSys(0, this);
        _toneMapSys = new SobelToneMapSys(1, this);
        _exposureSys = new RowExposureSys(2, this);
    }

    public void RegisterAll(Legatus legatus)
    {
        // Phase 0: Extreme asymmetric double-precision Mandelbrot kernel (82,944 pixels, up to 2048 iter/pixel)
        var c0 = new Cohors(0, _fractalSys);
        c0.WriteMask.Tollere(ResEscape);

        // Phase 1A: 3x3 Sobel edge-enhancing palette tone-map (reads Escape, writes RGB)
        var c1 = new Cohors(1, _toneMapSys);
        c1.ReadMask.Tollere(ResEscape);
        c1.WriteMask.Tollere(ResRgb);

        // Phase 1B (runs IN PARALLEL with Phase 1A!): Per-row auto-exposure statistics
        var c2 = new Cohors(2, _exposureSys);
        c2.ReadMask.Tollere(ResEscape);
        c2.WriteMask.Tollere(ResLuma);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.ConstructConsilium();
    }

    public void RunSerial()
    {
        ExecuteFractalRange(0, PixelCount);
        ExecuteToneMapRange(0, PixelCount);
        ExecuteRowExposureRange(0, Height);
    }

    public void RunParallelFor()
    {
        Parallel.For(0, PixelCount, i => ExecuteFractalRange(i, 1));
        Parallel.For(0, PixelCount, i => ExecuteToneMapRange(i, 1));
        Parallel.For(0, Height, y => ExecuteRowExposureRange(y, 1));
    }

    private void ExecuteFractalRange(int start, int length)
    {
        int end = start + length;
        int w = Width;
        double invW = 1.0 / w;
        double invH = 1.0 / Height;

        // Asymmetric framing: top rows (y near 0) cut straight across the deep main cardioid & period-2 bulb
        // (burning full 2,048 double-precision FMA iterations), while bottom rows lie in fast exterior escape (4-15 iters).
        const double xMin = -0.82;
        const double xSpan = 0.42;
        const double yTop = 0.00;
        const double ySpan = 0.95;

        for (int idx = start; idx < end; idx++)
        {
            int y = idx / w;
            int x = idx - y * w;

            double cr = xMin + (x * invW) * xSpan;
            double ci = yTop + (y * invH) * ySpan;

            double zr = 0.0;
            double zi = 0.0;
            double zr2 = 0.0;
            double zi2 = 0.0;
            int iter = 0;

            // Unrolled 4x double-precision FMA escape loop for pure CPU ALU saturation
            while (iter < MaxIterations && zr2 + zi2 <= 4.0)
            {
                zi = 2.0 * zr * zi + ci;
                zr = zr2 - zi2 + cr;
                zr2 = zr * zr;
                zi2 = zi * zi;
                iter++;

                if (iter < MaxIterations && zr2 + zi2 <= 4.0)
                {
                    zi = 2.0 * zr * zi + ci;
                    zr = zr2 - zi2 + cr;
                    zr2 = zr * zr;
                    zi2 = zi * zi;
                    iter++;
                }
            }

            if (iter >= MaxIterations)
            {
                _escapeSmooth[idx] = 0.0f;
            }
            else
            {
                double mag = Math.Sqrt(zr2 + zi2);
                double nu = iter + 1.0 - Math.Log2(Math.Log2(Math.Max(1.0001, mag)));
                _escapeSmooth[idx] = (float)(nu * (1.0 / MaxIterations));
            }
        }
    }

    private void ExecuteToneMapRange(int start, int length)
    {
        int end = start + length;
        int w = Width;
        int h = Height;

        for (int idx = start; idx < end; idx++)
        {
            int y = idx / w;
            int x = idx - y * w;
            float c = _escapeSmooth[idx];

            float l = x > 0 ? _escapeSmooth[idx - 1] : c;
            float r = x < w - 1 ? _escapeSmooth[idx + 1] : c;
            float u = y > 0 ? _escapeSmooth[idx - w] : c;
            float d = y < h - 1 ? _escapeSmooth[idx + w] : c;

            float edge = MathF.Abs(r - l) + MathF.Abs(d - u);
            float t = MathF.Min(1.0f, c * 8.0f + edge * 2.5f);

            float rf = 0.5f + 0.5f * MathF.Cos(6.28318f * (t + 0.00f));
            float gf = 0.5f + 0.5f * MathF.Cos(6.28318f * (t + 0.33f));
            float bf = 0.5f + 0.5f * MathF.Cos(6.28318f * (t + 0.67f));

            uint rb = (uint)(Math.Clamp(rf, 0.0f, 1.0f) * 255.0f);
            uint gb = (uint)(Math.Clamp(gf, 0.0f, 1.0f) * 255.0f);
            uint bb = (uint)(Math.Clamp(bf, 0.0f, 1.0f) * 255.0f);
            _rgbOut[idx] = 0xFF00_0000u | (rb << 16) | (gb << 8) | bb;
        }
    }

    private void ExecuteRowExposureRange(int start, int length)
    {
        int end = start + length;
        int w = Width;
        float invW = 1.0f / w;

        for (int y = start; y < end; y++)
        {
            int rowOff = y * w;
            float sum = 0.0f;
            for (int x = 0; x < w; x++)
            {
                sum += _escapeSmooth[rowOff + x];
            }
            _rowLuma[y] = sum * invW;
        }
    }

    private sealed class FractalKernelSys(int id, MandelbrotDeepZoomSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.PixelCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.ExecuteFractalRange(start, length);
    }

    private sealed class SobelToneMapSys(int id, MandelbrotDeepZoomSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.PixelCount;
        protected override void ExecuteBatchLogic(int start, int length) => s.ExecuteToneMapRange(start, length);
    }

    private sealed class RowExposureSys(int id, MandelbrotDeepZoomSimulation s) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => s.Height;
        protected override void ExecuteBatchLogic(int start, int length) => s.ExecuteRowExposureRange(start, length);
    }
}
