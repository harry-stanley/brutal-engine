using System;

namespace BrutalEngine.Core;

/// <summary>
/// Thrown when an operation requires a live handle but receives one that has been freed, or that
/// was never issued by the allocator it was passed to.
/// </summary>
/// <remarks>A stale handle is a programming error. It is reported immediately, never ignored.</remarks>
public sealed class StaleHandleException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="handle"/>.</summary>
    /// <param name="handle">The stale handle.</param>
    public StaleHandleException(Handle handle)
        : base($"{handle} is stale: it has been freed or was not issued by this allocator.")
    {
        Handle = handle;
    }

    /// <summary>Creates the exception with a default message.</summary>
    public StaleHandleException()
        : base("The handle is stale: it has been freed or was not issued by this allocator.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    /// <param name="message">The message.</param>
    public StaleHandleException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public StaleHandleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The stale handle, or <see cref="Handle.Null"/> if none was supplied.</summary>
    public Handle Handle { get; }
}
