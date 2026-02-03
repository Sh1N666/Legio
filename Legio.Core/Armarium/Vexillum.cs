using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Legio.Core.Armarium;

/// <summary>
/// Vexillum (The Standard/Flag). 
/// Represents a bitmask of occupied memory territory (components).
/// It uses 256 bits (4 x ulong), allowing it to handle up to 256 distinct component types.
/// Zero allocation: this is a pure value type stored on the stack.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Vexillum
{
    // Fixed buffer - key to zero-allocation. 
    // Embedded directly within the struct, not an array on the heap.
    private fixed ulong _signa[4]; 

    /// <summary>
    /// Raises the standard for a specific bit (marks the component as used/active).
    /// </summary>
    /// <param name="index">The bit index (0-255).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tollere(int index)
    {
        // Guard check without throwing exceptions for maximum performance
        if ((uint)index >= 256) return; 
        _signa[index / 64] |= (1UL << (index % 64));
    }

    /// <summary>
    /// Checks if two standards conflict (Collision/Contention).
    /// Returns TRUE if both Vexilla claim the same bit.
    /// </summary>
    /// <param name="alia">The other Vexillum to check against.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Confligerea(in Vexillum alia)
    {
        // Manual Loop Unrolling for maximum SIMD efficiency.
        // Checks 64 bits at a time.
        fixed (ulong* ptr = _signa)
        fixed (ulong* aliaPtr = alia._signa)
        {
            if ((ptr[0] & aliaPtr[0]) != 0) return true;
            if ((ptr[1] & aliaPtr[1]) != 0) return true;
            if ((ptr[2] & aliaPtr[2]) != 0) return true;
            if ((ptr[3] & aliaPtr[3]) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Joins two standards together (Bitwise OR / Logical Sum).
    /// Used by the Legatus to build the cumulative mask for a battle phase.
    /// </summary>
    /// <param name="alia">The other Vexillum to merge.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Iungere(in Vexillum alia)
    {
        fixed (ulong* ptr = _signa)
        fixed (ulong* aliaPtr = alia._signa)
        {
            ptr[0] |= aliaPtr[0];
            ptr[1] |= aliaPtr[1];
            ptr[2] |= aliaPtr[2];
            ptr[3] |= aliaPtr[3];
        }
    }

    /// <summary>
    /// Cleans the standard (Resets all bits to zero).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Purgare()
    {
        fixed (ulong* ptr = _signa)
        {
            ptr[0] = 0; ptr[1] = 0; ptr[2] = 0; ptr[3] = 0;
        }
    }
}