using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>A display mode: the resolution and refresh rate a display can run at.</summary>
/// <remarks>
/// <para>
/// Mirrors the backend's mode (SDL's <c>SDL_DisplayMode</c>) without its pixel format. <see cref="Width"/> and
/// <see cref="Height"/> are in window coordinates, before pixel density: a 1920 by 1080 mode with a
/// <see cref="PixelDensity"/> of 2 has 3840 by 2160 pixels.
/// </para>
/// <para>
/// Modes passed back to the platform (<c>IWindow.SetExclusiveFullscreen</c>) must be ones it returned: the backend
/// matches every field exactly against the display's current list. <c>default(DisplayMode)</c> has display
/// <c>0</c>, which is invalid.
/// </para>
/// <para>
/// Equality compares every field; the two <see langword="float"/> fields compare as <see cref="float.Equals(float)"/>
/// does, which is exact because the values come from the backend unchanged. Init-only properties rather than
/// positional parameters, so fields can be added without breaking construction. Immutable and safe to use from any
/// thread. No member allocates, except <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct DisplayMode
{
    /// <summary>The display this mode belongs to.</summary>
    public DisplayId Display { get; init; }

    /// <summary>The width in window coordinates, before pixel density.</summary>
    public int Width { get; init; }

    /// <summary>The height in window coordinates, before pixel density.</summary>
    public int Height { get; init; }

    /// <summary>
    /// The pixels per window coordinate: the mode has <see cref="Width"/> times this by <see cref="Height"/> times
    /// this pixels.
    /// </summary>
    public float PixelDensity { get; init; }

    /// <summary>The refresh rate in Hz, or <c>0</c> if the backend doesn't know it.</summary>
    public float RefreshRate { get; init; }

    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>DisplayMode { Display = DisplayId { Value = 1 }, Width = 1920, Height = 1080, PixelDensity = 1, RefreshRate = 59.94 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DisplayMode {{ Display = {Display}, Width = {Width}, Height = {Height}, PixelDensity = {PixelDensity}, RefreshRate = {RefreshRate} }}");
}
