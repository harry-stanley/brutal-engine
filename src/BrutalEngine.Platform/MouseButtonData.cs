using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>
/// The payload of a <c>MouseButtonDown</c> or <c>MouseButtonUp</c> event: which button, the click count, and where the
/// pointer was.
/// </summary>
/// <param name="Button">The button pressed or released.</param>
/// <param name="Clicks">SDL's click count: 1 for a single click, 2 for a double click, and so on.</param>
/// <param name="X">The pointer's horizontal position in the window, in window coordinates.</param>
/// <param name="Y">The pointer's vertical position in the window, in window coordinates.</param>
/// <remarks>
/// <para>
/// Positions are SDL's float window coordinates, the same space as the window's <c>Size</c> (docs/design/platform.md
/// section 1).
/// </para>
/// <para>
/// 12 bytes with no references, because <see cref="MouseButton"/> is byte-backed, so it fits the event's payload at
/// offsets 16 to 27 (docs/design/platform.md, "The event struct"). Equality compares every field; the
/// <see langword="float"/> fields compare as <see cref="float.Equals(float)"/> does. Immutable and safe to use from any
/// thread. No member allocates, except <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct MouseButtonData(MouseButton Button, byte Clicks, float X, float Y)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>MouseButtonData { Button = Left, Clicks = 2, X = 10.5, Y = 20 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"MouseButtonData {{ Button = {Button}, Clicks = {Clicks}, X = {X}, Y = {Y} }}");
}
