using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>The payload of a <c>MouseWheel</c> event: how far the wheel scrolled, and where the pointer was.</summary>
/// <param name="X">The horizontal scroll amount, in SDL's float wheel units.</param>
/// <param name="Y">The vertical scroll amount, in SDL's float wheel units.</param>
/// <param name="MouseX">The pointer's horizontal position in the window, in window coordinates.</param>
/// <param name="MouseY">The pointer's vertical position in the window, in window coordinates.</param>
/// <remarks>
/// <para>
/// The scroll direction SDL reports with each wheel event (<c>SDL_MouseWheelDirection</c>, normal or flipped) is
/// already applied by the backend, so the payload has no direction field to check. Positions are SDL's float window
/// coordinates, the same space as the window's <c>Size</c> (docs/design/platform.md section 1).
/// </para>
/// <para>
/// 16 bytes with no references, so it fits the event's payload at offsets 16 to 31 (docs/design/platform.md,
/// "The event struct"). Equality compares every field; the <see langword="float"/> fields compare as
/// <see cref="float.Equals(float)"/> does. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct MouseWheelData(float X, float Y, float MouseX, float MouseY)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>MouseWheelData { X = 0, Y = -1.5, MouseX = 10.5, MouseY = 20 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"MouseWheelData {{ X = {X}, Y = {Y}, MouseX = {MouseX}, MouseY = {MouseY} }}");
}
