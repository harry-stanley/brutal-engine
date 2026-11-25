using System;

namespace BrutalEngine.Core;

/// <summary>The outcome of an operation that returns no value: success, or an <see cref="EngineError"/>.</summary>
/// <remarks>
/// <para>
/// <c>default(Result)</c> is a <b>failure</b> with a default <see cref="EngineError"/>
/// (<see cref="ErrorKind.Unspecified"/>), so a result that was never assigned can't pass for success.
/// </para>
/// <para>This type is immutable and thread-safe, and never allocates.</para>
/// </remarks>
public readonly struct Result
{
    private readonly EngineError _error;
    private readonly bool _isSuccess;

    private Result(bool isSuccess, EngineError error)
    {
        _isSuccess = isSuccess;
        _error = error;
    }

    /// <summary>A successful result.</summary>
    public static Result Success => new(true, default);

    /// <summary><see langword="true"/> if the operation succeeded.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary><see langword="true"/> if the operation failed.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>The error of a failed result.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public EngineError Error => _isSuccess
        ? throw new InvalidOperationException("A successful result has no error.")
        : _error;

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error.</param>
    /// <returns>A failed result carrying <paramref name="error"/>.</returns>
    public static Result Failure(EngineError error) => new(false, error);

    /// <summary>Gets the error if the result failed.</summary>
    /// <param name="error">The error, or default on success.</param>
    /// <returns><see langword="true"/> if the result failed.</returns>
    public bool TryGetError(out EngineError error)
    {
        error = _error;
        return !_isSuccess;
    }

    /// <summary>Throws <see cref="ResultFailedException"/> if the result failed.</summary>
    /// <exception cref="ResultFailedException">The result is a failure.</exception>
    public void ThrowIfFailed()
    {
        if (!_isSuccess)
        {
            throw new ResultFailedException(_error);
        }
    }

    /// <summary>Converts an error into a failed result, so methods can <c>return error;</c>.</summary>
    /// <param name="error">The error.</param>
    public static implicit operator Result(EngineError error) => Failure(error);

    /// <summary>Returns <c>Success</c> or <c>Failure(...)</c>.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => _isSuccess ? "Success" : $"Failure({_error})";
}
