using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>
/// A copy between one region of one texture subresource and rows in a buffer, in either direction: the texture-to-buffer
/// and buffer-to-texture copies both take it.
/// </summary>
/// <remarks>
/// <para>
/// The buffer holds the region's rows <see cref="BytesPerRow"/> apart, from <see cref="BufferOffset"/>; rows per image
/// are always the region's height, because a copy covers one layer. The device checks a copy before it records it
/// (docs/design/graphics.md section 6): the usages; the texture has one sample; the location and region inside the texture;
/// the aspect against the format (depth is copied to buffers, never from them); <see cref="BytesPerRow"/> at least the region's width times
/// the texel size and a multiple of <see cref="RowPitchAlignment"/>; <see cref="BufferOffset"/> a multiple of the texel
/// size and of 4; and the buffer large enough for the rows. A violation throws <see cref="System.ArgumentException"/>
/// (<see cref="System.ArgumentOutOfRangeException"/> for ranges).
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares every property. The device keeps no
/// reference to the copy. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureBufferCopy
{
    /// <summary>
    /// The alignment, in bytes, that <see cref="BytesPerRow"/> must be a multiple of: 256, WebGPU's rule, so a D3D12
    /// backend needs no row repacking.
    /// </summary>
    public const int RowPitchAlignment = 256;

    /// <summary>The texture subresource and aspect copied from or to.</summary>
    public required TextureLocation Texture { get; init; }

    /// <summary>The texels copied, inside the location's mip level.</summary>
    public required TextureRegion Region { get; init; }

    /// <summary>The buffer copied to or from.</summary>
    public required BufferHandle Buffer { get; init; }

    /// <summary>
    /// The byte offset of the first row in the buffer: at least 0, and a multiple of the texel size and of 4. Defaults
    /// to 0.
    /// </summary>
    public long BufferOffset { get; init; }

    /// <summary>
    /// The bytes from the start of one row to the start of the next in the buffer: at least the region's width times the
    /// texel size, and a multiple of <see cref="RowPitchAlignment"/>.
    /// </summary>
    public required int BytesPerRow { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>TextureBufferCopy { Texture = TextureLocation { ... }, Region = TextureRegion { ... }, Buffer = Buffer(2:1), BufferOffset = 0, BytesPerRow = 256 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureBufferCopy {{ Texture = {Texture}, Region = {Region}, Buffer = {Buffer}, BufferOffset = {BufferOffset}, BytesPerRow = {BytesPerRow} }}");
}
