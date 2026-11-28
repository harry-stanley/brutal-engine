using System;

namespace BrutalEngine.Graphics;

/// <summary>Describes a bind group layout: the bindings a group of resources provides to shaders.</summary>
/// <remarks>
/// Declared in C# next to the shaders that use it, because M1 has no shader reflection data (docs/design/graphics.md
/// sections 7 and 8). An immutable value: safe to copy and to use from any thread. Equality compares
/// <see cref="Entries"/> by reference, as <see cref="ReadOnlyMemory{T}"/> does, and the debug name ordinally. The device
/// keeps no reference to the description or its <see cref="DebugName"/>. No member allocates, except
/// <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct BindGroupLayoutDescription
{
    /// <summary>The bindings, in any order; binding numbers are unique and each count is at least 1.</summary>
    public required ReadOnlyMemory<BindGroupLayoutEntry> Entries { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }
}
