using System.Buffers;
using System.Runtime.CompilerServices;

namespace Legio.Core.Memoria;

/// <summary>
/// A tactical unit of the Legion.
/// Represents a contiguous region of memory containing data elements.
/// This struct is a lightweight view (Slice) and does not own the memory.
/// </summary>
/// <typeparam name="T">The type of soldier (Entity/Component) in this unit.</typeparam>
public readonly struct Manipulus<T>
{
    /// <summary>
    /// The backing memory storage.
    /// </summary>
    public readonly Memory<T> Data;

    /// <summary>
    /// Returns the number of elements in this Maniple.
    /// </summary>
    public int Length => Data.Length;

    /// <summary>
    /// Checks if the Maniple is empty.
    /// </summary>
    public bool IsEmpty => Data.IsEmpty;

    /// <summary>
    /// Recruits a new Maniple from a raw array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Manipulus(T[] data)
    {
        Data = new Memory<T>(data);
    }

    /// <summary>
    /// Forms a Maniple from an existing memory region.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Manipulus(Memory<T> data)
    {
        Data = data;
    }

    /// <summary>
    /// Grants direct access to the battle line (Span).
    /// Critical for performance: Span is stack-only and enables bounds-check elimination.
    /// </summary>
    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data.Span;
    }

    /// <summary>
    /// Splits the Maniple into a smaller formation (Slicing).
    /// Zero-allocation operation.
    /// </summary>
    /// <param name="start">The starting index.</param>
    /// <param name="length">The number of elements to slice.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Manipulus<T> Slice(int start, int length)
    {
        return new Manipulus<T>(Data.Slice(start, length));
    }

    /// <summary>
    /// Acquires a raw pointer to the memory for unsafe operations.
    /// WARNING: This pins the memory. The handle must be disposed to unpin.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe MemoryHandle Pin()
    {
        return Data.Pin();
    }

    // ==========================================
    // OPERATORS & SUGAR
    // ==========================================

    /// <summary>
    /// Implicitly converts a raw array into a Manipulus.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Manipulus<T>(T[] array) => new Manipulus<T>(array);

    /// <summary>
    /// Implicitly converts Memory to Manipulus.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Manipulus<T>(Memory<T> memory) => new Manipulus<T>(memory);

    /// <summary>
    /// Allows accessing individual elements by index.
    /// </summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Data.Span[index];
    }
}