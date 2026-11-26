namespace BrutalEngine.Graphics;

/// <summary>Which part of a texture's texels a copy reads or writes: color, or depth.</summary>
/// <remarks>
/// A texture location's aspect must match its texture's format: <see cref="Color"/> for color formats,
/// <see cref="Depth"/> for <see cref="TextureFormat.Depth16Unorm"/> and <see cref="TextureFormat.Depth32Float"/>
/// (docs/design/graphics.md section 6). Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum TextureAspect
{
    /// <summary>The color texels of a color format. The default value.</summary>
    Color = 0,

    /// <summary>The depth values of a depth format.</summary>
    Depth = 1,
}
