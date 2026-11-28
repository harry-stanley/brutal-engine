namespace BrutalEngine.Graphics;

/// <summary>The kind of resource a <see cref="BindGroupEntry"/> holds, which decides the properties it can read.</summary>
/// <remarks>
/// Set by the factory that made the entry (docs/design/graphics.md section 8). A transient allocation is a
/// <see cref="Buffer"/> entry, bound through its ring alias.
/// </remarks>
public enum BindGroupEntryKind
{
    /// <summary>A buffer range: <see cref="BindGroupEntry.Buffer"/>. The default value.</summary>
    Buffer = 0,

    /// <summary>A texture view: <see cref="BindGroupEntry.View"/>.</summary>
    Texture = 1,

    /// <summary>A sampler: <see cref="BindGroupEntry.Sampler"/>.</summary>
    Sampler = 2,
}
