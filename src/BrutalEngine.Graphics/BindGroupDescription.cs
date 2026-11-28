using System;

namespace BrutalEngine.Graphics;

/// <summary>Describes a bind group: the resources that fill a bind group layout's bindings.</summary>
/// <remarks>
/// A <see langword="ref"/> struct over a span of entries, so a <see cref="BindGroupLifetime.Frame"/> group is described
/// in the frame loop without a managed allocation, for example over a <see langword="stackalloc"/> span
/// (docs/design/graphics.md section 8). A group is immutable once created: to bind different resources, create another.
/// The device checks the description when the group is created: a <see cref="Lifetime"/> outside the enum throws
/// <see cref="ArgumentOutOfRangeException"/>, and an entry not made by a factory (its resource is the null handle) or a
/// repeated binding and array element throws <see cref="ArgumentException"/>; then it validates the entries against the
/// layout. It keeps no reference to the description or its span. Used on the device's owner thread. No member allocates.
/// </remarks>
public readonly ref struct BindGroupDescription
{
    /// <summary>The layout whose bindings the entries fill. It must be live; the group doesn't keep it alive.</summary>
    public required BindGroupLayoutHandle Layout { get; init; }

    /// <summary>
    /// The entries, one per binding and array element, made with the <see cref="BindGroupEntry"/> factories. Read only
    /// while the group is created.
    /// </summary>
    public required ReadOnlySpan<BindGroupEntry> Entries { get; init; }

    /// <summary>How long the group lives; <see cref="BindGroupLifetime.Persistent"/> by default.</summary>
    public BindGroupLifetime Lifetime { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none. Ignored for <see cref="BindGroupLifetime.Frame"/> groups.
    /// </summary>
    public string? DebugName { get; init; }
}
