namespace BrutalEngine.Core.Memory;

/// <summary>A live allocation recorded by a <see cref="TrackingAllocator"/>.</summary>
public readonly struct NativeAllocationInfo
{
    internal NativeAllocationInfo(nint address, nuint byteCount, nuint alignment, string callerFile, int callerLine, long sequence)
    {
        Address = address;
        ByteCount = byteCount;
        Alignment = alignment;
        CallerFile = callerFile;
        CallerLine = callerLine;
        Sequence = sequence;
    }

    /// <summary>Address of the allocation.</summary>
    public nint Address { get; }

    /// <summary>Requested size in bytes.</summary>
    public nuint ByteCount { get; }

    /// <summary>Requested alignment in bytes.</summary>
    public nuint Alignment { get; }

    /// <summary>Source file of the call to <see cref="INativeAllocator.Allocate"/>, as recorded by the compiler.</summary>
    public string CallerFile { get; }

    /// <summary>Source line of the call to <see cref="INativeAllocator.Allocate"/>.</summary>
    public int CallerLine { get; }

    /// <summary>Position in allocation order on its allocator, starting at 1.</summary>
    public long Sequence { get; }
}
