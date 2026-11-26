using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>
/// The rectangle of a render pass's attachments that draws may write, in pixels (docs/design/graphics.md section 9).
/// </summary>
/// <param name="X">The left edge, in pixels from the attachments' left side.</param>
/// <param name="Y">The top edge, in pixels from the attachments' top.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <remarks>
/// <para>
/// A render pass starts with the scissor covering its whole attachments; <c>RenderPassEncoder.SetScissor</c> changes it
/// for the draws after it. The rectangle lies inside the attachments. The type itself accepts any values; the encoder
/// that records it decides which it accepts.
/// </para>
/// <para>
/// A plain unmanaged value, so the command stream holds it as raw bytes. Equality compares every field. Immutable and
/// safe to use from any thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct ScissorRect(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example <c>ScissorRect { X = 0, Y = 0, Width = 640, Height = 480 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"ScissorRect {{ X = {X}, Y = {Y}, Width = {Width}, Height = {Height} }}");
}
