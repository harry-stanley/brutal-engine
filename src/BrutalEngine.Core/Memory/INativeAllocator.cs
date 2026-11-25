using System;
using System.Runtime.CompilerServices;

namespace BrutalEngine.Core.Memory;

/// <summary>
/// Allocates and frees unmanaged memory. Every engine allocation of unmanaged memory goes through an
/// implementation of this interface, so it can be tracked and leak-checked (docs/03-architecture.md, section 3.3).
/// </summary>
/// <remarks>
/// <para>
/// The <c>callerFile</c> and <c>callerLine</c> parameters of <see cref="Allocate"/> are filled in by the
/// compiler. Callers should not pass them. They let tracking allocators report where leaked memory was allocated,
/// without allocating managed memory (the compiler passes string literals).
/// </para>
/// <para>
/// Implementations must document their thread safety, and must repeat <see cref="CallerFilePathAttribute"/> and
/// <see cref="CallerLineNumberAttribute"/> on their <see cref="Allocate"/> parameters: the attributes are not
/// inherited, so calls through the concrete type would otherwise record no call site.
/// </para>
/// </remarks>
public unsafe interface INativeAllocator
{
    /// <summary>Allocates <paramref name="byteCount"/> bytes aligned to <paramref name="alignment"/>.</summary>
    /// <param name="byteCount">Number of bytes. Must be greater than zero.</param>
    /// <param name="alignment">Alignment in bytes. Must be a power of two.</param>
    /// <param name="callerFile">Filled in by the compiler. Do not pass.</param>
    /// <param name="callerLine">Filled in by the compiler. Do not pass.</param>
    /// <returns>A pointer to uninitialised memory. Never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteCount"/> is zero, or <paramref name="alignment"/> is not a power of two.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The memory could not be allocated.</exception>
    public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0);

    /// <summary>Frees memory returned by <see cref="Allocate"/> on this allocator.</summary>
    /// <param name="memory">The pointer to free. <see langword="null"/> is ignored.</param>
    public void Free(void* memory);
}
