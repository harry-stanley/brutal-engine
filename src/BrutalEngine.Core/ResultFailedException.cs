using System;

namespace BrutalEngine.Core;

/// <summary>
/// Thrown when code reads the value of a failed <see cref="Result{T}"/>, or calls
/// <see cref="Result.ThrowIfFailed"/> on a failed <see cref="Result"/>.
/// </summary>
public sealed class ResultFailedException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">The error carried by the failed result.</param>
    public ResultFailedException(EngineError error)
        : base($"The operation failed: {error}")
    {
        Error = error;
    }

    /// <summary>Creates the exception with a default message.</summary>
    public ResultFailedException()
        : base("The operation failed.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    /// <param name="message">The message.</param>
    public ResultFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ResultFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The error of the failed result. Default if none was supplied.</summary>
    public EngineError Error { get; }
}
