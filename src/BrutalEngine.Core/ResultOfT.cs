using System;
using System.Diagnostics.CodeAnalysis;

namespace BrutalEngine.Core;

/// <summary>The outcome of an operation that returns a value: the value on success, or an <see cref="EngineError"/>.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// <para>
/// <c>default(Result&lt;T&gt;)</c> is a <b>failure</b> with a default <see cref="EngineError"/>
/// (<see cref="ErrorKind.Unspecified"/>), so a result that was never assigned can't pass for success.
/// </para>
/// <para>
/// The type itself never allocates. It is immutable, and thread-safe to the extent that
/// <typeparamref name="T"/> is.
/// </para>
/// </remarks>
public readonly struct Result<T>
{
    private readonly T _value;
    private readonly EngineError _error;
    private readonly bool _isSuccess;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value.</param>
    public Result(T value)
    {
        _value = value;
        _error = default;
        _isSuccess = true;
    }

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error.</param>
    public Result(EngineError error)
    {
        _value = default!;
        _error = error;
        _isSuccess = false;
    }

    /// <summary><see langword="true"/> if the operation succeeded.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary><see langword="true"/> if the operation failed.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>The value of a successful result.</summary>
    /// <exception cref="ResultFailedException">The result is a failure.</exception>
    public T Value => _isSuccess ? _value : throw new ResultFailedException(_error);

    /// <summary>The error of a failed result.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public EngineError Error => _isSuccess
        ? throw new InvalidOperationException("A successful result has no error.")
        : _error;

    /// <summary>Gets the value if the result succeeded.</summary>
    /// <param name="value">The value, or default on failure.</param>
    /// <returns><see langword="true"/> if the result succeeded.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return _isSuccess;
    }

    /// <summary>Gets the error if the result failed.</summary>
    /// <param name="error">The error, or default on success.</param>
    /// <returns><see langword="true"/> if the result failed.</returns>
    public bool TryGetError(out EngineError error)
    {
        error = _error;
        return !_isSuccess;
    }

    /// <summary>Returns the value on success, otherwise <paramref name="fallback"/>.</summary>
    /// <param name="fallback">Value to return on failure.</param>
    /// <returns>The value or the fallback.</returns>
    public T GetValueOrDefault(T fallback) => _isSuccess ? _value : fallback;

    /// <summary>Converts the outcome to a <see cref="Result"/>, dropping the value.</summary>
    /// <returns>Success, or a failure with the same error.</returns>
    public Result ToResult() => _isSuccess ? Result.Success : Result.Failure(_error);

    /// <summary>Converts an error into a failed result, so methods can <c>return error;</c>.</summary>
    /// <param name="error">The error.</param>
    public static implicit operator Result<T>(EngineError error) => new(error);

    /// <summary>Converts a value into a successful result, so methods can <c>return value;</c>.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>Returns <c>Success(value)</c> or <c>Failure(...)</c>.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => _isSuccess ? $"Success({_value})" : $"Failure({_error})";
}
