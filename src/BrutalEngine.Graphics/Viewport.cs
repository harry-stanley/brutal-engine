using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>
/// The rectangle of a render pass's attachments that draws map to, in pixels, with the depth range they map to
/// (docs/design/graphics.md section 9).
/// </summary>
/// <param name="X">The left edge, in pixels from the attachments' left side.</param>
/// <param name="Y">The top edge, in pixels from the attachments' top.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="MinDepth">The depth that normalised depth 0 maps to. Defaults to 0.</param>
/// <param name="MaxDepth">The depth that normalised depth 1 maps to. Defaults to 1.</param>
/// <remarks>
/// <para>
/// A render pass starts with the viewport covering its whole attachments; <c>RenderPassEncoder.SetViewport</c> changes
/// it for the draws after it. Coordinates are in pixels with the origin at the top left. The type itself accepts any
/// values; the encoder that records it decides which it accepts.
/// </para>
/// <para>
/// A plain unmanaged value, so the command stream holds it as raw bytes. Equality compares every field (floating-point
/// equality, so a <see cref="float.NaN"/> field never compares equal). Immutable and safe to use from any thread. No
/// member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct Viewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>Viewport { X = 0, Y = 0, Width = 1280, Height = 720, MinDepth = 0, MaxDepth = 1 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is: an ASCII minus sign, a
    /// period as the decimal separator, and <c>NaN</c> and <c>Infinity</c> for those values. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Viewport {{ X = {X}, Y = {Y}, Width = {Width}, Height = {Height}, MinDepth = {MinDepth}, MaxDepth = {MaxDepth} }}");
}
