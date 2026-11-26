using System;

namespace BrutalEngine.Graphics;

/// <summary>Optional GPU features a device can require, request and report as enabled.</summary>
/// <remarks>
/// Features are negotiated, never assumed: an adapter lacking a required feature is rejected, an optional feature is
/// enabled only when the adapter has it, and the device's capabilities list what is enabled. Using a
/// feature that isn't enabled throws <see cref="InvalidOperationException"/> (docs/design/graphics.md section 2).
/// </remarks>
[Flags]
public enum GraphicsFeatures : long
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>Anisotropic texture filtering, up to the adapter's maximum sampler anisotropy.</summary>
    SamplerAnisotropy = 1 << 0,

    /// <summary>GPU timestamp queries: the queue reports valid timestamp bits.</summary>
    TimestampQueries = 1 << 1,

    /// <summary>BC (block compression) texture formats. Declared now; the formats arrive with the texture importer.</summary>
    TextureCompressionBC = 1 << 2,

    /// <summary>ASTC (LDR) texture formats. Declared now; the formats arrive with the texture importer.</summary>
    TextureCompressionAstc = 1 << 3,
}
