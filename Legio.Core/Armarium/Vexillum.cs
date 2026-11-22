using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Legio.Core.Armarium;

/// <summary>
/// Vexillum (The Standard/Flag).
/// Represents a 256-bit mask of memory territory (component/resource IDs 0..255).
/// Zero-allocation value type accelerated via Vector256 SIMD when available.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vexillum
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public readonly bool IsEmpty => (_s0 | _s1 | _s2 | _s3) == 0;

    /// <summary>
    /// Raises the standard for a specific resource bit (0..255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tollere(int index)
    {
        if ((uint)index >= 256) return;
        ulong bit = 1UL << (index & 63);
        switch (index >> 6)
        {
            case 0: _s0 |= bit; break;
            case 1: _s1 |= bit; break;
            case 2: _s2 |= bit; break;
            case 3: _s3 |= bit; break;
        }
    }

    /// <summary>
    /// Lowers the standard for a specific resource bit (0..255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deponere(int index)
    {
        if ((uint)index >= 256) return;
        ulong mask = ~(1UL << (index & 63));
        switch (index >> 6)
        {
            case 0: _s0 &= mask; break;
            case 1: _s1 &= mask; break;
            case 2: _s2 &= mask; break;
            case 3: _s3 &= mask; break;
        }
    }

    /// <summary>
    /// Returns true if the specified resource bit is raised.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Habet(int index)
    {
        if ((uint)index >= 256) return false;
        ulong bit = 1UL << (index & 63);
        return (index >> 6) switch
        {
            0 => (_s0 & bit) != 0,
            1 => (_s1 & bit) != 0,
            2 => (_s2 & bit) != 0,
            _ => (_s3 & bit) != 0
        };
    }

    /// <summary>
    /// Checks if two standards conflict (share at least one resource bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Confligere(in Vexillum alia)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var a = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in _s0));
            var b = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in alia._s0));
            return (a & b) != Vector256<ulong>.Zero;
        }

        return ((_s0 & alia._s0) | (_s1 & alia._s1) | (_s2 & alia._s2) | (_s3 & alia._s3)) != 0;
    }

    /// <summary>
    /// Merges another standard into this one (bitwise OR).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Iungere(in Vexillum alia)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            ref var a = ref Unsafe.As<ulong, Vector256<ulong>>(ref _s0);
            var b = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.AsRef(in alia._s0));
            a |= b;
            return;
        }

        _s0 |= alia._s0;
        _s1 |= alia._s1;
        _s2 |= alia._s2;
        _s3 |= alia._s3;
    }

    /// <summary>
    /// Resets all 256 bits to zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Purgare()
    {
        _s0 = 0;
        _s1 = 0;
        _s2 = 0;
        _s3 = 0;
    }
}