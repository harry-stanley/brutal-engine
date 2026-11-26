namespace BrutalEngine.Graphics;

/// <summary>How a sampler combines texels: for minification, magnification, or between mip levels.</summary>
/// <remarks>
/// Nearest is the default, because pixel art is the common 2D case (docs/design/graphics.md section 5). Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum Filter
{
    /// <summary>The nearest texel or mip level, unblended. The default value.</summary>
    Nearest = 0,

    /// <summary>A linear blend of the neighbouring texels or mip levels.</summary>
    Linear = 1,
}
