using System;

namespace BrutalEngine.Graphics;

/// <summary>How a buffer will be used: one flag per kind of binding or copy, combined with <c>|</c>.</summary>
/// <remarks>
/// A buffer description needs at least one flag, and a <see cref="MemoryIntent.Readback"/> buffer exactly
/// <see cref="CopyDestination"/> (docs/design/graphics.md section 5). Backends map each flag to their native usage by
/// name, never by casting.
/// </remarks>
[Flags]
public enum BufferUsage
{
    /// <summary>No usage. The default value; never valid in a description.</summary>
    None = 0,

    /// <summary>Bound as a vertex buffer.</summary>
    Vertex = 1 << 0,

    /// <summary>Bound as an index buffer.</summary>
    Index = 1 << 1,

    /// <summary>Bound as a uniform buffer.</summary>
    Uniform = 1 << 2,

    /// <summary>Bound as a storage buffer, read or written by shaders.</summary>
    Storage = 1 << 3,

    /// <summary>The source of indirect draw or dispatch arguments.</summary>
    Indirect = 1 << 4,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 5,

    /// <summary>The destination of a copy or a write.</summary>
    CopyDestination = 1 << 6,
}
