namespace BrutalEngine.World;

/// <summary>Everything a registry knows about a registered component type: its id, info, size and alignment.</summary>
/// <param name="Id">The type's dense id in the registry that describes it.</param>
/// <param name="Info">The type's <see cref="IComponent.Info"/>.</param>
/// <param name="Size">The type's size in bytes, computed by the registry from the type itself.</param>
/// <param name="Alignment">
/// The type's alignment in bytes, computed by the registry from the type itself (its offset after a single byte).
/// </param>
/// <remarks>
/// <para>
/// Registries create these when they register a type. Like <see cref="ComponentId"/>, a value is meaningful only in
/// the registry that created it. <see cref="Size"/> and <see cref="Alignment"/> never come from
/// <see cref="Info"/>, so generated code can't get them wrong.
/// </para>
/// <para>
/// Equality compares every property. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct ComponentType(ComponentId Id, ComponentInfo Info, int Size, int Alignment);
