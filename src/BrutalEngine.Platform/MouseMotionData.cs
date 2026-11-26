using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>The payload of a <c>MouseMotion</c> event: where the pointer is, how far it moved, and the buttons held.</summary>
/// <param name="X">The pointer's horizontal position in the window, in window coordinates.</param>
/// <param name="Y">The pointer's vertical position in the window, in window coordinates.</param>
/// <param name="DeltaX">The horizontal motion since the previous motion event, in window coordinates.</param>
/// <param name="DeltaY">The vertical motion since the previous motion event, in window coordinates.</param>
/// <param name="Buttons">The mouse buttons held during the motion.</param>
/// <remarks>
/// <para>
/// Positions and deltas are SDL's float window coordinates, the same space as the window's <c>Size</c>: convert them
/// to pixels or content units as docs/design/platform.md section 1 describes. In relative mouse mode the cursor is
/// hidden and confined, and the deltas are the values to read.
/// </para>
/// <para>
/// 20 bytes with no references, so it fits the event's payload at offsets 16 to 35 (docs/design/platform.md,
/// "The event struct"). Equality compares every field; the <see langword="float"/> fields compare as
/// <see cref="float.Equals(float)"/> does. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct MouseMotionData(float X, float Y, float DeltaX, float DeltaY, MouseButtons Buttons)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>MouseMotionData { X = 10.5, Y = 20, DeltaX = -1.5, DeltaY = 0, Buttons = Left, Right }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"MouseMotionData {{ X = {X}, Y = {Y}, DeltaX = {DeltaX}, DeltaY = {DeltaY}, Buttons = {Buttons} }}");
}
