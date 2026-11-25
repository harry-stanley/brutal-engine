using System;
using System.Globalization;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>A typed, generational handle to a pipeline layout owned by a graphics device.</summary>
/// <remarks>
/// <para>
/// Only the device creates these handles. The null handle is <c>default(PipelineLayoutHandle)</c> (<see cref="IsNull"/>),
/// which no device issues. A handle is non-owning: copying it doesn't keep the object alive, and destroying
/// the layout makes every copy stale. The device validates a handle on every call that takes it and throws for
/// a stale one; it never dereferences it. A handle of this kind never converts to a handle of another kind.
/// </para>
/// <para>
/// This type is an immutable plain value: safe to copy and to pass between threads, though the device accepts it only on
/// its owner thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly struct PipelineLayoutHandle : IEquatable<PipelineLayoutHandle>
{
    internal PipelineLayoutHandle(Handle value) => Value = value;

    /// <summary><see langword="true"/> for the null handle, <c>default(PipelineLayoutHandle)</c>.</summary>
    public bool IsNull => Value.IsNull;

    /// <summary>The Core handle issued by the device's pipeline layout allocator.</summary>
    internal Handle Value { get; }

    /// <summary>Compares two handles of this kind by slot index and generation.</summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns><see langword="true"/> if both have the same index and generation.</returns>
    public bool Equals(PipelineLayoutHandle other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PipelineLayoutHandle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Returns <c>PipelineLayout(index:generation)</c>, or <c>PipelineLayout(null)</c> for the null handle.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => IsNull
        ? "PipelineLayout(null)"
        : string.Create(CultureInfo.InvariantCulture, $"PipelineLayout({Value.Index}:{Value.Generation})");

    /// <summary>Compares two handles for equality (same index and generation).</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(PipelineLayoutHandle left, PipelineLayoutHandle right) => left.Equals(right);

    /// <summary>Compares two handles for inequality.</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(PipelineLayoutHandle left, PipelineLayoutHandle right) => !left.Equals(right);
}
