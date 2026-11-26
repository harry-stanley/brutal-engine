using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A rectangle of texels in one mip level of a texture: the part a copy or a write covers.</summary>
/// <param name="X">The left edge, in texels from the mip level's left side: at least 0.</param>
/// <param name="Y">The top edge, in texels from the mip level's top: at least 0.</param>
/// <param name="Width">The width in texels: greater than 0.</param>
/// <param name="Height">The height in texels: greater than 0.</param>
/// <remarks>
/// <para>
/// The device checks a region before it records the copy or write that uses it (docs/design/graphics.md section 6):
/// the region must lie inside its mip level, whose extent is <c>max(1, size &gt;&gt; mip)</c> on each side. A region
/// outside throws <see cref="System.ArgumentOutOfRangeException"/>. The type itself accepts any values.
/// </para>
/// <para>
/// Equality compares every field. Immutable and safe to use from any thread. No member allocates, except
/// <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureRegion(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>TextureRegion { X = 0, Y = 16, Width = 64, Height = 32 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so (invalid) negative
    /// values always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureRegion {{ X = {X}, Y = {Y}, Width = {Width}, Height = {Height} }}");
}
