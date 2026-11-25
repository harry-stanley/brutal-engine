using System;
using System.Globalization;

namespace BrutalEngine.Core;

/// <summary>
/// A generational handle: a slot index plus the generation of the object that occupied the slot
/// when the handle was issued.
/// </summary>
/// <remarks>
/// <para>
/// Handles are small, copyable, non-owning references to engine objects. A handle becomes stale
/// when its object is released: the slot's generation moves on, so the stale handle no longer
/// matches and is detected instead of silently referring to whatever reuses the slot.
/// Validity is always decided by the <see cref="HandleAllocator"/> that issued the handle.
/// </para>
/// <para>
/// Generation <c>0</c> is reserved: the only handle with generation <c>0</c> is <see cref="Null"/>
/// (<c>default(Handle)</c>). Allocators never issue it.
/// </para>
/// <para>This type is immutable and thread-safe. No member allocates, except <see cref="ToString"/>.</para>
/// </remarks>
public readonly struct Handle : IEquatable<Handle>
{
    private readonly ulong _value;

    /// <summary>Creates a handle from its index and generation.</summary>
    /// <param name="index">Slot index.</param>
    /// <param name="generation">Generation of the slot. Must not be <c>0</c>, which is reserved for <see cref="Null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generation"/> is <c>0</c>.</exception>
    public Handle(uint index, uint generation)
    {
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        _value = ((ulong)generation << 32) | index;
    }

    private Handle(ulong value) => _value = value;

    /// <summary>The null handle, equal to <c>default(Handle)</c>. It never refers to an object.</summary>
    public static Handle Null => default;

    /// <summary>The slot index.</summary>
    public uint Index => (uint)_value;

    /// <summary>The generation of the slot when this handle was issued. <c>0</c> only for <see cref="Null"/>.</summary>
    public uint Generation => (uint)(_value >> 32);

    /// <summary><see langword="true"/> if this is <see cref="Null"/>.</summary>
    public bool IsNull => _value == 0;

    /// <summary>
    /// The handle packed into 64 bits (generation in the high 32 bits, index in the low 32 bits),
    /// suitable for serialisation. Restore it with <see cref="FromRawValue"/>.
    /// </summary>
    public ulong RawValue => _value;

    /// <summary>Restores a handle from a value produced by <see cref="RawValue"/>.</summary>
    /// <param name="rawValue">The packed value.</param>
    /// <returns>The handle. <c>0</c> gives <see cref="Null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The generation part is <c>0</c> but the index part is not, which no handle can produce.
    /// </exception>
    public static Handle FromRawValue(ulong rawValue)
    {
        if ((rawValue >> 32) == 0 && rawValue != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawValue),
                rawValue,
                "A handle with generation 0 must be Null (raw value 0).");
        }

        return new Handle(rawValue);
    }

    /// <inheritdoc />
    public bool Equals(Handle other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Handle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>Returns <c>Handle(index:generation)</c>, or <c>Handle(null)</c> for <see cref="Null"/>.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => IsNull
        ? "Handle(null)"
        : string.Create(CultureInfo.InvariantCulture, $"Handle({Index}:{Generation})");

    /// <summary>Compares two handles for equality (same index and generation).</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(Handle left, Handle right) => left.Equals(right);

    /// <summary>Compares two handles for inequality.</summary>
    /// <param name="left">First handle.</param>
    /// <param name="right">Second handle.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(Handle left, Handle right) => !left.Equals(right);
}
