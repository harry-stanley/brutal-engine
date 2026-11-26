namespace BrutalEngine.World;

/// <summary>
/// Marks an unmanaged struct as a component, data that entities in a world can carry, and supplies its
/// <see cref="ComponentInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// A component is a plain <c>unmanaged</c> struct with public fields and no managed references: assets and strings are
/// referenced by handle. A component with no instance fields (size 1) is a tag. Every generic world member is
/// constrained <c>where T : unmanaged, IComponent</c>, so using anything else is a compile error. Generic component
/// types aren't supported yet: the stable id is a hash of a non-generic full type name.
/// </para>
/// <para>
/// The component generator implements <see cref="Info"/>; until it ships, components write it by hand. The world reads
/// it through the static abstract member, with no reflection, so it works under NativeAOT.
/// </para>
/// </remarks>
public interface IComponent
{
    /// <summary>The type's identity and schema: its stable id, schema version, full metadata name and tag-ness.</summary>
    /// <remarks>
    /// Implementations return the same value on every call, from any thread, and should allocate nothing (a value
    /// built once and stored, or one built from constants). The world never takes the type's size or alignment from
    /// it: it computes both from the type itself.
    /// </remarks>
    public static abstract ComponentInfo Info { get; }
}
