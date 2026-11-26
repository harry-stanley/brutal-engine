namespace BrutalEngine.Graphics;

/// <summary>The texel formats textures, views and targets can use.</summary>
/// <remarks>
/// The set every M1 consumer needs (sprites, render targets, the overlay, conformance), plus two depth formats
/// (docs/design/graphics.md section 5). Whether a device supports a format for a given use is negotiated per format,
/// through <see cref="FormatSupport"/>. Backends map each value to their native format by name, never by casting.
/// </remarks>
public enum TextureFormat
{
    /// <summary>No format. The default value; never valid in a description.</summary>
    Undefined = 0,

    /// <summary>One 8-bit unsigned normalized channel (red).</summary>
    R8Unorm = 1,

    /// <summary>Two 8-bit unsigned normalized channels (red, green).</summary>
    Rg8Unorm = 2,

    /// <summary>Four 8-bit unsigned normalized channels (red, green, blue, alpha).</summary>
    Rgba8Unorm = 3,

    /// <summary>Four 8-bit unsigned normalized channels (red, green, blue, alpha), with sRGB-encoded color.</summary>
    Rgba8UnormSrgb = 4,

    /// <summary>Four 8-bit unsigned normalized channels in blue, green, red, alpha order.</summary>
    Bgra8Unorm = 5,

    /// <summary>Four 8-bit unsigned normalized channels in blue, green, red, alpha order, with sRGB-encoded color.</summary>
    Bgra8UnormSrgb = 6,

    /// <summary>One 16-bit floating-point channel (red).</summary>
    R16Float = 7,

    /// <summary>Two 16-bit floating-point channels (red, green).</summary>
    Rg16Float = 8,

    /// <summary>Four 16-bit floating-point channels (red, green, blue, alpha).</summary>
    Rgba16Float = 9,

    /// <summary>One 32-bit floating-point channel (red).</summary>
    R32Float = 10,

    /// <summary>Two 32-bit floating-point channels (red, green).</summary>
    Rg32Float = 11,

    /// <summary>Four 32-bit floating-point channels (red, green, blue, alpha).</summary>
    Rgba32Float = 12,

    /// <summary>One 32-bit unsigned integer channel (red).</summary>
    R32Uint = 13,

    /// <summary>Four 8-bit unsigned integer channels (red, green, blue, alpha).</summary>
    Rgba8Uint = 14,

    /// <summary>A 16-bit unsigned normalized depth value. Guaranteed as a depth attachment by the Vulkan specification.</summary>
    Depth16Unorm = 15,

    /// <summary>A 32-bit floating-point depth value. Near-universal, but checked like any other format.</summary>
    Depth32Float = 16,
}
