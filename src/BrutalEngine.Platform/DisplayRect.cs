using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>A rectangle on the desktop, in the backend's desktop coordinates, which are window coordinates.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <remarks>
/// <para>
/// Window coordinates are the OS's unit: pixels on Windows and X11, points on macOS and Wayland (see
/// <see cref="IWindow.Size"/>). Displays are placed relative to each other on one desktop, so <see cref="X"/> and
/// <see cref="Y"/> can be negative, for a display left of or above the origin.
/// </para>
/// <para>
/// Equality compares every field. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>DisplayRect { X = -1920, Y = 0, Width = 1920, Height = 1080 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative
    /// coordinates always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DisplayRect {{ X = {X}, Y = {Y}, Width = {Width}, Height = {Height} }}");
}
