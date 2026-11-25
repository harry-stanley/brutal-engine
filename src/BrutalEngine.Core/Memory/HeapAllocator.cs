using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrutalEngine.Core.Memory;

/// <summary>
/// General-purpose unmanaged heap backed by the C runtime's aligned allocator
/// (<see cref="NativeMemory.AlignedAlloc"/> / <see cref="NativeMemory.AlignedFree"/>).
/// </summary>
/// <remarks>
/// <para>Stateless and thread-safe. Allocation never allocates managed memory.</para>
/// <para>
/// <see cref="Free"/> must only receive pointers from this allocator. Passing anything else is undefined
/// behaviour, as with C's <c>free</c>. Wrap the heap in a tracking allocator to detect such
/// mistakes and leaks.
/// </para>
/// </remarks>
public sealed unsafe class HeapAllocator : INativeAllocator
{
    /// <inheritdoc />
    public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
    {
        ValidateRequest(byteCount, alignment);
        return NativeMemory.AlignedAlloc(byteCount, alignment);
    }

    /// <inheritdoc />
    public void Free(void* memory)
    {
        if (memory != null)
        {
            NativeMemory.AlignedFree(memory);
        }
    }

    /// <summary>Validates an allocation request against the <see cref="INativeAllocator.Allocate"/> contract.</summary>
    /// <param name="byteCount">Requested size.</param>
    /// <param name="alignment">Requested alignment.</param>
    /// <exception cref="ArgumentOutOfRangeException">The request breaks the contract.</exception>
    internal static void ValidateRequest(nuint byteCount, nuint alignment)
    {
        if (byteCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "The allocation size must be greater than zero.");
        }

        if (!BitOperations.IsPow2(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment must be a power of two.");
        }
    }
}
