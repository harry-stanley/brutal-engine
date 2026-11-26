using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>Passes calls through, and throws <see cref="InsufficientMemoryException"/> for one allocation when asked.</summary>
internal sealed unsafe class FailingAllocator(INativeAllocator inner) : INativeAllocator
{
    /// <summary>Set to fail the next allocation; cleared when it fails.</summary>
    public bool FailNext { get; set; }

    public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InsufficientMemoryException("Injected allocation failure.");
        }

        return inner.Allocate(byteCount, alignment, callerFile, callerLine);
    }

    public void Free(void* memory) => inner.Free(memory);
}
