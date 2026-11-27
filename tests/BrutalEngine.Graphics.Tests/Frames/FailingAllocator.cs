using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>
/// Passes calls through to another allocator, and throws <see cref="InsufficientMemoryException"/> for one chosen
/// allocation: the next one (<see cref="FailNext"/>) or the one after <see cref="FailAfter"/> more successes.
/// </summary>
internal sealed unsafe class FailingAllocator(INativeAllocator inner) : INativeAllocator
{
    /// <summary>Set to fail the next allocation; cleared when it fails.</summary>
    public bool FailNext { get; set; }

    /// <summary>The successful allocations left before one fails; negative (the default) for none.</summary>
    public int FailAfter { get; set; } = -1;

    /// <summary>The allocations that succeeded.</summary>
    public int Allocations { get; private set; }

    public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
    {
        if (FailAfter == 0 || FailNext)
        {
            FailNext = false;
            FailAfter = -1;
            throw new InsufficientMemoryException("Injected allocation failure.");
        }

        if (FailAfter > 0)
        {
            FailAfter--;
        }

        Allocations++;
        return inner.Allocate(byteCount, alignment, callerFile, callerLine);
    }

    public void Free(void* memory) => inner.Free(memory);
}
