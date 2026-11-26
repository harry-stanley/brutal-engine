using System;
using System.Globalization;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>A typed, generational handle to an interned debug label (a pass or debug group name) owned by a graphics device.</summary>
/// <remarks>
/// <para>
/// Only the device creates labels, interning each name once. The null label is <c>default(DebugLabel)</c>
/// (<see cref="IsNull"/>), which no device issues and which means unlabelled: no timestamp and no debug group name.
/// A label is non-owning and is never destroyed on its own: it stays valid for the device's lifetime, including
/// across device-loss recovery. The device validates a label on every call that takes it and throws for an invalid
/// one; it never dereferences it. A label never converts to a handle of another kind.
/// </para>
/// <para>
/// This type is an immutable plain value: safe to copy and to pass between threads, though the device accepts it only on
/// its owner thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly struct DebugLabel : IEquatable<DebugLabel>
{
    internal DebugLabel(Handle value) => Value = value;

    /// <summary><see langword="true"/> for the null handle, <c>default(DebugLabel)</c>.</summary>
    public bool IsNull => Value.IsNull;

    /// <summary>The Core handle issued by the device's debug label allocator.</summary>
    internal Handle Value { get; }

    /// <summary>Compares two handles of this kind by slot index and generation.</summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns><see langword="true"/> if both have the same index and generation.</returns>
    public bool Equals(DebugLabel other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DebugLabel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Returns <c>DebugLabel(index:generation)</c>, or <c>DebugLabel(null)</c> for the null handle.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => IsNull
        ? "DebugLabel(null)"
        : string.Create(CultureInfo.InvariantCulture, $"DebugLabel({Value.Index}:{Value.Generation})");

    /// <summary>Compares two handles for equality (same index and generation).</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(DebugLabel left, DebugLabel right) => left.Equals(right);

    /// <summary>Compares two handles for inequality.</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(DebugLabel left, DebugLabel right) => !left.Equals(right);
}
