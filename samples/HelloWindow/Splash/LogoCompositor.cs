using System;

namespace HelloWindow.Splash;

/// <summary>Composites the decoded logo onto an opaque background colour on the CPU, once at load, so the GPU only has
/// to scale and place an opaque image (no blending, no shaders).</summary>
internal static class LogoCompositor
{
    /// <summary>Composites straight-alpha RGBA pixels onto <paramref name="background"/>.</summary>
    /// <param name="rgba">The logo's pixels: 4 bytes each, red, green, blue, alpha, with sRGB-encoded colour and straight
    /// (not premultiplied) alpha, as SDL decodes a PNG into <c>SDL_PIXELFORMAT_RGBA32</c>. Any number of whole pixels,
    /// for example one row of a surface.</param>
    /// <param name="destination">Receives the opaque result, the same length as <paramref name="rgba"/>, in
    /// <paramref name="order"/>, with alpha 255. Must not overlap <paramref name="rgba"/>.</param>
    /// <param name="background">The colour behind the logo.</param>
    /// <param name="order">The destination's byte order: the upload format's.</param>
    /// <remarks>Blends in the encoded (sRGB) space, as browsers and image editors display a PNG over a colour, so the
    /// logo looks as its authors saw it. Opaque pixels come out unchanged and transparent ones as the background.
    /// Doesn't allocate.</remarks>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> isn't whole pixels, or the lengths differ.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> isn't a defined value.</exception>
    public static void Composite(ReadOnlySpan<byte> rgba, Span<byte> destination, Rgb8 background, LogoPixelOrder order)
    {
        if (rgba.Length % 4 != 0)
        {
            throw new ArgumentException($"Expected whole 4-byte pixels, got {rgba.Length} bytes.", nameof(rgba));
        }

        if (destination.Length != rgba.Length)
        {
            throw new ArgumentException($"The destination is {destination.Length} bytes; the source is {rgba.Length}.", nameof(destination));
        }

        if (order is not (LogoPixelOrder.Rgba or LogoPixelOrder.Bgra))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Not a pixel order.");
        }

        // Byte offsets of red and blue in the destination; green and alpha don't move.
        int red = order == LogoPixelOrder.Rgba ? 0 : 2;
        int blue = 2 - red;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            destination[i + red] = Blend(rgba[i], background.R, alpha);
            destination[i + 1] = Blend(rgba[i + 1], background.G, alpha);
            destination[i + blue] = Blend(rgba[i + 2], background.B, alpha);
            destination[i + 3] = byte.MaxValue;
        }
    }

    // (colour * alpha + background * (255 - alpha)) / 255, rounded to nearest. Exact at alpha 255 (colour) and 0
    // (background), and at most 255 * 255 + 127, so it can't overflow.
    private static byte Blend(int colour, int background, int alpha) =>
        (byte)(((colour * alpha) + (background * (byte.MaxValue - alpha)) + 127) / byte.MaxValue);
}
