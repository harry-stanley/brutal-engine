namespace BrutalEngine.Graphics;

/// <summary>How shaders see a texture view.</summary>
/// <remarks>
/// 3D and cube views are added when a consumer needs them (docs/design/graphics.md section 5). Backends map each
/// value to their native one by name, never by casting.
/// </remarks>
public enum TextureViewDimension
{
    /// <summary>A single 2D image: exactly one array layer. The default value.</summary>
    Texture2D = 0,

    /// <summary>An array of 2D images: one or more array layers.</summary>
    Texture2DArray = 1,
}
