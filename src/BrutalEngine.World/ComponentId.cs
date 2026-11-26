namespace BrutalEngine.World;

/// <summary>A component type's dense id in the registry that issued it.</summary>
/// <remarks>
/// <para>
/// A registry numbers its types in registration order, from <c>0</c>, so ids index arrays and archetype masks
/// directly. An id is meaningful only in the registry that issued it: two worlds that register types in different
/// orders give the same type different ids, and ids are never persisted (serialised data uses
/// <see cref="StableComponentId"/>). Only a registry creates ids with a chosen index; outside this assembly the
/// only id that can be made is <c>default(ComponentId)</c>, whose <see cref="Index"/> is <c>0</c>.
/// </para>
/// <para>
/// Equality compares <see cref="Index"/>. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct ComponentId
{
    /// <summary>Creates an id; only registries call this.</summary>
    /// <param name="index">The dense index, from <c>0</c>.</param>
    internal ComponentId(int index) => Index = index;

    /// <summary>The dense index, from <c>0</c>, in registration order.</summary>
    public int Index { get; }
}
