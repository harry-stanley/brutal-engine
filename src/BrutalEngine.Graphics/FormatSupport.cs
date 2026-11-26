using System;

namespace BrutalEngine.Graphics;

/// <summary>What a device can do with a texture format, as reported per format by the device.</summary>
/// <remarks>
/// Format support is queried per format, never assumed: creating a texture whose format, usage or sample count the
/// device doesn't support fails with <c>ErrorKind.Unsupported</c> (docs/design/graphics.md sections 2 and 5).
/// </remarks>
[Flags]
public enum FormatSupport
{
    /// <summary>The format can't be used at all.</summary>
    None = 0,

    /// <summary>Textures of the format can be sampled in shaders.</summary>
    Sampled = 1,

    /// <summary>Sampled textures of the format can use linear filtering.</summary>
    Filterable = 2,

    /// <summary>The format can be a render pass color target.</summary>
    ColorTarget = 4,

    /// <summary>Color targets of the format support blending.</summary>
    Blendable = 8,

    /// <summary>The format can be a render pass depth or stencil target.</summary>
    DepthStencilTarget = 16,

    /// <summary>Textures of the format can be bound as storage textures.</summary>
    Storage = 32,

    /// <summary>Textures of the format can be the source of a copy.</summary>
    CopySource = 64,

    /// <summary>Textures of the format can be the destination of a copy.</summary>
    CopyDestination = 128,

    /// <summary>
    /// Targets of the format can be multisampled; for a color format, at the counts in
    /// <see cref="GraphicsLimits.ColorSampleCounts"/>. Depth and stencil sample counts are reported separately.
    /// </summary>
    Multisample = 256,
}
