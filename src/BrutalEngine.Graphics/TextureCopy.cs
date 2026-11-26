using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>
/// A copy of one region from one texture subresource to another of the same format and size: no scaling and no format
/// conversion in M1.
/// </summary>
/// <remarks>
/// <para>
/// The destination region is <c>(DestinationX, DestinationY, SourceRegion.Width, SourceRegion.Height)</c>. The device
/// checks a copy before it records it (docs/design/graphics.md section 6): the source has
/// <see cref="TextureUsage.CopySource"/> and the destination <see cref="TextureUsage.CopyDestination"/>; both have the
/// same format and one sample; both locations and regions lie inside their textures; and the regions don't overlap when
/// both are the same subresource. Depth textures can be copied this way. A violation throws
/// <see cref="System.ArgumentException"/> (<see cref="System.ArgumentOutOfRangeException"/> for ranges).
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares every property. The device keeps no
/// reference to the copy. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureCopy
{
    /// <summary>The texture subresource copied from.</summary>
    public required TextureLocation Source { get; init; }

    /// <summary>The texels copied, inside the source's mip level. Its size is also the destination region's.</summary>
    public required TextureRegion SourceRegion { get; init; }

    /// <summary>The texture subresource copied to; may be the source texture, at another mip level, layer or region.</summary>
    public required TextureLocation Destination { get; init; }

    /// <summary>The destination region's left edge, in texels: at least 0. Defaults to 0.</summary>
    public int DestinationX { get; init; }

    /// <summary>The destination region's top edge, in texels: at least 0. Defaults to 0.</summary>
    public int DestinationY { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>TextureCopy { Source = TextureLocation { ... }, SourceRegion = TextureRegion { ... }, Destination = TextureLocation { ... }, DestinationX = 16, DestinationY = 0 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureCopy {{ Source = {Source}, SourceRegion = {SourceRegion}, Destination = {Destination}, DestinationX = {DestinationX}, DestinationY = {DestinationY} }}");
}
