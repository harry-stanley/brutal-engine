using System;

namespace BrutalEngine.Graphics;

/// <summary>Per-format facts the front end's validation and copy rules need: texel size, depth and sRGB.</summary>
/// <remarks>
/// Each method is a switch over the sixteen concrete formats (docs/design/graphics.md sections 5 and 6), so none
/// allocates. <see cref="TextureFormat.Undefined"/> and values outside the enum have no texels and throw
/// <see cref="ArgumentOutOfRangeException"/>. Stateless and safe to call from any thread.
/// </remarks>
internal static class TextureFormatInfo
{
    /// <summary>Returns the bytes one texel of <paramref name="format"/> occupies.</summary>
    /// <param name="format">A concrete format.</param>
    /// <returns>1, 2, 4, 8 or 16.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="TextureFormat.Undefined"/> or not a member of the enum.</exception>
    public static int TexelSize(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => 1,
        TextureFormat.Rg8Unorm or TextureFormat.R16Float or TextureFormat.Depth16Unorm => 2,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
            or TextureFormat.Rg16Float or TextureFormat.R32Float or TextureFormat.R32Uint or TextureFormat.Rgba8Uint
            or TextureFormat.Depth32Float => 4,
        TextureFormat.Rgba16Float or TextureFormat.Rg32Float => 8,
        TextureFormat.Rgba32Float => 16,
        _ => throw NotConcrete(format),
    };

    /// <summary>Returns whether <paramref name="format"/> holds depth values rather than color.</summary>
    /// <param name="format">A concrete format.</param>
    /// <returns><see langword="true"/> for <see cref="TextureFormat.Depth16Unorm"/> and <see cref="TextureFormat.Depth32Float"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="TextureFormat.Undefined"/> or not a member of the enum.</exception>
    public static bool IsDepth(TextureFormat format)
    {
        _ = TexelSize(format);
        return format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float;
    }

    /// <summary>Returns whether <paramref name="format"/> stores sRGB-encoded color.</summary>
    /// <param name="format">A concrete format.</param>
    /// <returns><see langword="true"/> for <see cref="TextureFormat.Rgba8UnormSrgb"/> and <see cref="TextureFormat.Bgra8UnormSrgb"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="TextureFormat.Undefined"/> or not a member of the enum.</exception>
    public static bool IsSrgb(TextureFormat format)
    {
        _ = TexelSize(format);
        return format is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;
    }

    private static ArgumentOutOfRangeException NotConcrete(TextureFormat format) =>
        new(nameof(format), format, "The format has no texels: it is Undefined or not a TextureFormat member.");
}
