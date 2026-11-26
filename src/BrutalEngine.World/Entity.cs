using System;
using System.Globalization;
using BrutalEngine.Core;

namespace BrutalEngine.World;

/// <summary>
/// A generational reference to an entity: a slot index in its world's entity table plus the generation of the
/// slot when the entity was issued.
/// </summary>
/// <remarks>
/// <para>
/// An entity wraps a Core <see cref="Handle"/> (32-bit index, 32-bit generation), so it is 8 unmanaged bytes and can
/// be stored inside components. It carries no world reference, and validity is decided by the world that issued it:
/// once the entity is despawned the slot's generation moves on, and the stale value is detected instead of
/// addressing whatever reuses the slot.
/// </para>
/// <para>
/// Generation <c>0</c> is reserved: the only entity with generation <c>0</c> is <see cref="Null"/>
/// (<c>default(Entity)</c>), which no world issues.
/// </para>
/// <para>This type is immutable and thread-safe. No member allocates, except <see cref="ToString"/>.</para>
/// </remarks>
public readonly struct Entity : IEquatable<Entity>
{
    private readonly Handle _handle;

    /// <summary>Creates an entity value from its index and generation, for example when deserialising.</summary>
    /// <param name="index">Slot index in the entity table.</param>
    /// <param name="generation">Generation of the slot. Must not be <c>0</c>, which is reserved for <see cref="Null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generation"/> is <c>0</c>.</exception>
    public Entity(uint index, uint generation) => _handle = new Handle(index, generation);

    /// <summary>The null entity, equal to <c>default(Entity)</c>. It never refers to an entity.</summary>
    public static Entity Null => default;

    /// <summary>The slot index in the entity table.</summary>
    public uint Index => _handle.Index;

    /// <summary>The generation of the slot when this entity was issued. <c>0</c> only for <see cref="Null"/>.</summary>
    public uint Generation => _handle.Generation;

    /// <summary><see langword="true"/> if this is <see cref="Null"/>.</summary>
    public bool IsNull => _handle.IsNull;

    /// <summary>
    /// The entity packed into 64 bits (generation in the high 32 bits, index in the low 32 bits), suitable for
    /// serialisation. Restore it with <see cref="FromRawValue"/>.
    /// </summary>
    public ulong RawValue => _handle.RawValue;

    /// <summary>Restores an entity from a value produced by <see cref="RawValue"/>.</summary>
    /// <param name="rawValue">The packed value.</param>
    /// <returns>The entity. <c>0</c> gives <see cref="Null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The generation part is <c>0</c> but the index part is not: a non-canonical value that no entity produces.
    /// </exception>
    public static Entity FromRawValue(ulong rawValue)
    {
        if (rawValue == 0)
        {
            return Null;
        }

        uint generation = (uint)(rawValue >> 32);
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawValue),
                rawValue,
                "An entity with generation 0 must be Entity.Null (raw value 0).");
        }

        return new Entity((uint)rawValue, generation);
    }

    /// <summary>Compares two entities by index and generation.</summary>
    /// <param name="other">The entity to compare with.</param>
    /// <returns><see langword="true"/> if both have the same index and generation.</returns>
    public bool Equals(Entity other) => _handle == other._handle;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _handle.GetHashCode();

    /// <summary>Returns <c>Entity(indexvgeneration)</c>, for example <c>Entity(12v3)</c>, or <c>Entity(null)</c> for <see cref="Null"/>.</summary>
    /// <returns>A diagnostic string. Allocates.</returns>
    public override string ToString() => IsNull
        ? "Entity(null)"
        : string.Create(CultureInfo.InvariantCulture, $"Entity({Index}v{Generation})");

    /// <summary>Compares two entities for equality (same index and generation).</summary>
    /// <param name="left">First entity.</param>
    /// <param name="right">Second entity.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    /// <summary>Compares two entities for inequality.</summary>
    /// <param name="left">First entity.</param>
    /// <param name="right">Second entity.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
