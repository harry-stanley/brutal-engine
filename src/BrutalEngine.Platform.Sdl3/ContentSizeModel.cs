using System;
using System.Globalization;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The conversions between content units, SDL window coordinates and pixels (docs/design/platform.md section 1,
/// "Window coordinates, pixels and content units").
/// </summary>
/// <remarks>
/// <para>
/// Window coordinates = content × display scale / pixel density, and content = pixels / display scale. Each side is
/// computed in <see langword="double"/> and rounded to the nearest integer, midpoints away from zero, so 2.5 becomes 3.
/// </para>
/// <para>Stateless and thread-safe. Allocates only when it throws.</para>
/// </remarks>
internal static class ContentSizeModel
{
    /// <summary>Converts a size in content units to SDL window coordinates, the unit window sizes are set in.</summary>
    /// <param name="content">The size in content units; neither side is negative.</param>
    /// <param name="displayScale">
    /// Pixels per content unit: the window's <c>SDL_GetWindowDisplayScale</c>. For the estimate before a window exists,
    /// pass the display's <c>SDL_GetDisplayContentScale</c> with a <paramref name="pixelDensity"/> of 1, because that
    /// scale is already in window coordinates.
    /// </param>
    /// <param name="pixelDensity">Pixels per window coordinate: the window's <c>SDL_GetWindowPixelDensity</c>.</param>
    /// <returns>
    /// The size in window coordinates, rounded per side. A positive side is at least 1, because SDL rejects a zero
    /// side; a zero side stays 0.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A side of <paramref name="content"/> is negative; <paramref name="displayScale"/> or
    /// <paramref name="pixelDensity"/> isn't a finite number above 0; or a converted side doesn't fit in an
    /// <see langword="int"/>.
    /// </exception>
    public static WindowSize ToWindowCoordinates(WindowSize content, float displayScale, float pixelDensity)
    {
        RequireSides(content, nameof(content));
        RequireScale(displayScale, nameof(displayScale));
        RequireScale(pixelDensity, nameof(pixelDensity));
        return new WindowSize(
            AtLeastOne(Convert(content.Width, displayScale, pixelDensity, nameof(content)), content.Width),
            AtLeastOne(Convert(content.Height, displayScale, pixelDensity, nameof(content)), content.Height));
    }

    /// <summary>Converts a drawable size in pixels to content units, the size <c>IWindow.ContentSize</c> reports.</summary>
    /// <param name="pixels">The size in pixels, from <c>SDL_GetWindowSizeInPixels</c>; 0 by 0 when minimised.</param>
    /// <param name="displayScale">Pixels per content unit: the window's <c>SDL_GetWindowDisplayScale</c>.</param>
    /// <returns>The size in content units, rounded per side.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A side of <paramref name="pixels"/> is negative, <paramref name="displayScale"/> isn't a finite number above 0,
    /// or a converted side doesn't fit in an <see langword="int"/>.
    /// </exception>
    public static WindowSize ContentFromPixels(WindowSize pixels, float displayScale)
    {
        RequireSides(pixels, nameof(pixels));
        RequireScale(displayScale, nameof(displayScale));
        return new WindowSize(
            Convert(pixels.Width, 1f, displayScale, nameof(pixels)),
            Convert(pixels.Height, 1f, displayScale, nameof(pixels)));
    }

    private static void RequireSides(WindowSize size, string paramName)
    {
        if (size.Width < 0 || size.Height < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, size, $"Both sides of {paramName} must be 0 or more, got {size.Width} by {size.Height}.");
        }
    }

    private static void RequireScale(float scale, string paramName)
    {
        // IsFinite rejects both infinities and NaN, which the comparison alone would let through.
        if (!float.IsFinite(scale) || scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                paramName, scale, string.Create(CultureInfo.InvariantCulture, $"{paramName} must be a finite number above 0, got {scale}."));
        }
    }

    private static int Convert(int side, float multiplier, float divisor, string paramName)
    {
        // A side below 2^29 times a float is exact in a double (29 + 24 significant bits fit in 53), so for every real
        // window the division is the only rounding step, and a midpoint such as 7.5 arrives exactly.
        double rounded = Math.Round(side * (double)multiplier / divisor, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                paramName, side, string.Create(CultureInfo.InvariantCulture, $"A side of {side} converts to {rounded}, which doesn't fit in an int."));
        }

        return (int)rounded;
    }

    private static int AtLeastOne(int converted, int side) => side > 0 && converted == 0 ? 1 : converted;
}
