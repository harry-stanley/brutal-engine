using System;

namespace BrutalEngine.Core.Memory;

/// <summary>Thrown by <see cref="TrackingAllocator.ThrowIfLeaks"/> when unmanaged memory is still allocated.</summary>
public sealed class NativeMemoryLeakException : InvalidOperationException
{
    /// <summary>Creates the exception with a leak report.</summary>
    /// <param name="report">The leak report, including where each leaked block was allocated.</param>
    /// <param name="leakedAllocations">Number of leaked allocations.</param>
    /// <param name="leakedBytes">Total leaked bytes.</param>
    public NativeMemoryLeakException(string report, long leakedAllocations, long leakedBytes)
        : base(report)
    {
        LeakedAllocations = leakedAllocations;
        LeakedBytes = leakedBytes;
    }

    /// <summary>Creates the exception with a default message.</summary>
    public NativeMemoryLeakException()
        : base("Unmanaged memory was leaked.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    /// <param name="message">The message.</param>
    public NativeMemoryLeakException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public NativeMemoryLeakException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Number of leaked allocations.</summary>
    public long LeakedAllocations { get; }

    /// <summary>Total leaked bytes.</summary>
    public long LeakedBytes { get; }
}
