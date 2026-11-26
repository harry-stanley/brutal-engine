namespace BrutalEngine.World;

/// <summary>
/// A component type's identity and schema, as its <see cref="IComponent.Info"/> declares them: stable id, schema
/// version, full metadata name and whether it is a tag.
/// </summary>
/// <remarks>
/// <para>
/// The component generator will emit this from the type's metadata name and a <c>[Component(SchemaVersion = n)]</c>
/// attribute; until it ships, components write it by hand. Hand-written values must follow the same rules: <see cref="SchemaVersion"/> at least <c>1</c>, a
/// non-empty <see cref="FullName"/>, <see cref="StableId"/> equal to
/// <see cref="StableComponentId.FromFullName"/> of <see cref="FullName"/>, and <see cref="IsTag"/> only for a type
/// with no instance fields. <c>default(ComponentInfo)</c> breaks them (its <see cref="FullName"/> is
/// <see langword="null"/>), so it never describes a component.
/// </para>
/// <para>
/// Equality compares every property, names ordinally. Immutable and safe to use from any thread. No member allocates,
/// except <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct ComponentInfo
{
    /// <summary>The type's stable id: <see cref="StableComponentId.FromFullName"/> of <see cref="FullName"/>.</summary>
    public required StableComponentId StableId { get; init; }

    /// <summary>
    /// The version of the type's data layout, at least <c>1</c>. A type keeps its <see cref="StableId"/> when its layout
    /// changes; serialisers use this to migrate data.
    /// </summary>
    public required uint SchemaVersion { get; init; }

    /// <summary>
    /// The type's metadata name, as <c>Type.FullName</c> gives it, for example <c>MyGame.Velocity</c> or
    /// <c>MyGame.Outer+Inner</c> for a nested type. Never <see langword="null"/> or empty in a valid value.
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// <see langword="true"/> for a tag: a component with no instance fields (size 1), which gets no data column, only a
    /// mask bit.
    /// </summary>
    public required bool IsTag { get; init; }
}
