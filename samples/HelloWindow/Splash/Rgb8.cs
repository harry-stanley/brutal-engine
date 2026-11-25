namespace HelloWindow.Splash;

/// <summary>An opaque colour with 8-bit sRGB-encoded channels, as stored in an 8-bit image.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
internal readonly record struct Rgb8(byte R, byte G, byte B)
{
    /// <summary>Gets black: exact in both UNORM and sRGB images, so a cleared black area and a logo composited onto
    /// black meet without a seam.</summary>
    public static Rgb8 Black => default;
}
