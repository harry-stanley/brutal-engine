using System;
using System.Globalization;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>A typed, generational handle to a bind group layout owned by a graphics device.</summary>
/// <remarks>
/// <para>
/// Only the device creates these handles. The null handle is <c>default(BindGroupLayoutHandle)</c> (<see cref="IsNull"/>),
/// which no device issues. A handle is non-owning: copying it doesn't keep the object alive, and destroying
/// the layout makes every copy stale. The device validates a handle on every call that takes it and throws for
/// a stale one; it never dereferences it. A handle of this kind never converts to a handle of another kind.
/// </para>
/// <para>
/// This type is an immutable plain value: safe to copy and to pass between threads, though the device accepts it only on
/// its owner thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly struct BindGroupLayoutHandle : IEquatable<BindGroupLayoutHandle>
{
    internal BindGroupLayoutHandle(Handle value) => Value = value;

    /// <summary><see langword="true"/> for the null handle, <c>default(BindGroupLayoutHandle)</c>.</summary>
    public bool IsNull => Value.IsNull;

    /// <summary>The Core handle issued by the device's bind group layout allocator.</summary>
    internal Handle Value { get; }

    /// <summary>Compares two handles of this kind by slot index and generation.</summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns><see langword="true"/> if both have the same index and generation.</returns>
    public bool Equals(BindGroupLayoutHandle other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BindGroupLayoutHandle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Returns <c>BindGroupLayout(index:generation)</c>, or <c>BindGroupLayout(null)</c> for the null handle.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => IsNull
        ? "BindGroupLayout(null)"
        : string.Create(CultureInfo.InvariantCulture, $"BindGroupLayout({Value.Index}:{Value.Generation})");

    /// <summary>Compares two handles for equality (same index and generation).</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(BindGroupLayoutHandle left, BindGroupLayoutHandle right) => left.Equals(right);

    /// <summary>Compares two handles for inequality.</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(BindGroupLayoutHandle left, BindGroupLayoutHandle right) => !left.Equals(right);
}
