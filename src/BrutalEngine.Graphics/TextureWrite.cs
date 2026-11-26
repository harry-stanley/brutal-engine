using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Where <c>WriteTexture</c> puts CPU data: one region of one color texture subresource, and the data's row pitch.</summary>
/// <remarks>
/// <para>
/// The data goes through the per-frame upload ring and is copied at the start of the next submitted frame
/// (docs/design/graphics.md section 6); the front end repacks it into the ring, so any row pitch is accepted. The
/// device checks a write before it copies the data: the texture's usage and format (color only), the location and
/// region inside it, and the data long enough for the rows, <c>BytesPerRow * (Height - 1) + Width * texel size</c>
/// bytes. A violation throws <see cref="System.ArgumentException"/> (<see cref="System.ArgumentOutOfRangeException"/> for
/// ranges).
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares every property. The device keeps no
/// reference to the write. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureWrite
{
    /// <summary>The texture subresource written. Its aspect must be <see cref="TextureAspect.Color"/>: depth textures can't be written.</summary>
    public required TextureLocation Target { get; init; }

    /// <summary>The texels written, inside the target's mip level.</summary>
    public required TextureRegion Region { get; init; }

    /// <summary>
    /// The bytes from the start of one row of the data to the start of the next: 0, the default, for tightly packed rows
    /// (the region's width times the texel size), or at least that.
    /// </summary>
    public int BytesPerRow { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>TextureWrite { Target = TextureLocation { ... }, Region = TextureRegion { ... }, BytesPerRow = 0 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureWrite {{ Target = {Target}, Region = {Region}, BytesPerRow = {BytesPerRow} }}");
}
