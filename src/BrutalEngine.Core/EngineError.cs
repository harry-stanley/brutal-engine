using System;
using System.Globalization;

namespace BrutalEngine.Core;

/// <summary>Describes an expected runtime failure: a kind, a human-readable message and an optional native code.</summary>
/// <remarks>
/// <para>
/// Use <see cref="EngineError"/> as the failure value of result types for failures a caller is
/// expected to handle, such as a missing asset or an unavailable device. Programming errors (bad arguments,
/// stale handles, wrong thread) throw exceptions instead (see docs/03-architecture.md, section 3.5).
/// </para>
/// <para>
/// Creating an error doesn't allocate when the message is a string literal or another existing string.
/// This type is immutable and thread-safe.
/// </para>
/// </remarks>
public readonly struct EngineError : IEquatable<EngineError>
{
    private readonly string? _message;

    /// <summary>Creates an error.</summary>
    /// <param name="kind">Category of the failure.</param>
    /// <param name="message">Human-readable description. Should say what failed and, where known, why.</param>
    /// <param name="nativeCode">Status code reported by a native library, or <c>0</c> if none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public EngineError(ErrorKind kind, string message, int nativeCode = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        Kind = kind;
        _message = message;
        NativeCode = nativeCode;
    }

    /// <summary>Category of the failure.</summary>
    public ErrorKind Kind { get; }

    /// <summary>Human-readable description. Empty only for a default-initialised error.</summary>
    public string Message => _message ?? string.Empty;

    /// <summary>Status code reported by a native library, or <c>0</c> if none.</summary>
    public int NativeCode { get; }

    /// <summary>Creates an error for a failed native call.</summary>
    /// <param name="nativeCode">The library's status or result code.</param>
    /// <param name="message">What failed, including the library and function name.</param>
    /// <returns>An error of kind <see cref="ErrorKind.Native"/>.</returns>
    public static EngineError Native(int nativeCode, string message) => new(ErrorKind.Native, message, nativeCode);

    /// <inheritdoc />
    public bool Equals(EngineError other) =>
        Kind == other.Kind && NativeCode == other.NativeCode && string.Equals(Message, other.Message, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EngineError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, NativeCode, StringComparer.Ordinal.GetHashCode(Message));

    /// <summary>Returns <c>Kind: Message</c>, plus the native code when there is one.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => NativeCode == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Kind}: {Message}")
        : string.Create(CultureInfo.InvariantCulture, $"{Kind}: {Message} (native code {NativeCode})");

    /// <summary>Compares two errors for equality (kind, message and native code).</summary>
    /// <param name="left">First error.</param>
    /// <param name="right">Second error.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(EngineError left, EngineError right) => left.Equals(right);

    /// <summary>Compares two errors for inequality.</summary>
    /// <param name="left">First error.</param>
    /// <param name="right">Second error.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(EngineError left, EngineError right) => !left.Equals(right);
}
