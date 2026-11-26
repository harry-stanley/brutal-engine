using System;

namespace BrutalEngine.Graphics;

/// <summary>How a texture will be used: one flag per kind of binding, target or copy, combined with <c>|</c>.</summary>
/// <remarks>
/// A texture description needs at least one flag. Whether the device supports a usage for a format is negotiated per
/// format, through <see cref="FormatSupport"/> (docs/design/graphics.md section 5). Backends map each flag to their
/// native usage by name, never by casting.
/// </remarks>
[Flags]
public enum TextureUsage
{
    /// <summary>No usage. The default value; never valid in a description.</summary>
    None = 0,

    /// <summary>Sampled by shaders.</summary>
    Sampled = 1 << 0,

    /// <summary>Bound as a storage texture, read or written by shaders.</summary>
    Storage = 1 << 1,

    /// <summary>Rendered to as a color target.</summary>
    ColorTarget = 1 << 2,

    /// <summary>Rendered to as a depth-stencil target.</summary>
    DepthStencilTarget = 1 << 3,

    /// <summary>The source of a copy.</summary>
    CopySource = 1 << 4,

    /// <summary>The destination of a copy or a write.</summary>
    CopyDestination = 1 << 5,
}
