namespace BrutalEngine.Graphics;

/// <summary>The kind of 8-bit BGRA or RGBA format a swapchain's images use. Standard dynamic range only.</summary>
/// <remarks>
/// The backend picks a surface format of the requested kind, and <see cref="SwapchainInfo.Format"/> reports the
/// <see cref="TextureFormat"/> in use (docs/design/graphics.md section 3). The numeric values are fixed.
/// </remarks>
public enum SwapchainFormat
{
    /// <summary>
    /// An sRGB format (<see cref="TextureFormat.Bgra8UnormSrgb"/> or <see cref="TextureFormat.Rgba8UnormSrgb"/>): shaders
    /// write linear color and the hardware encodes it. The default.
    /// </summary>
    Srgb = 0,

    /// <summary>
    /// A linear format (<see cref="TextureFormat.Bgra8Unorm"/> or <see cref="TextureFormat.Rgba8Unorm"/>): values are
    /// stored as written, for content that is already sRGB-encoded.
    /// </summary>
    Unorm = 1,
}
