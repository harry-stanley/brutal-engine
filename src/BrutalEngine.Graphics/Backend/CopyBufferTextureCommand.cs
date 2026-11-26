namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.CopyBufferToTexture"/> or <see cref="CommandKind.CopyTextureToBuffer"/>
/// record: a copy between rows in a buffer and one region of one texture subresource (docs/design/graphics.md, "The
/// frame plan").
/// </summary>
/// <remarks>
/// <para>
/// The record's kind gives the direction; the fields are the same for both, as in <see cref="TextureBufferCopy"/>.
/// The front end resolved the buffer and texture to backend ids and checked the copy (section 6) before it encoded
/// it, so a backend passes the fields to its native copy unchanged. Rows per image are the region's height. The
/// record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct CopyBufferTextureCommand
{
    /// <summary>The buffer's id: non-zero, the id the backend returned when it created the buffer.</summary>
    public ulong Buffer { get; init; }

    /// <summary>
    /// The byte offset of the first row in the buffer: at least 0, and a multiple of the texel size and of 4.
    /// </summary>
    public long BufferOffset { get; init; }

    /// <summary>
    /// The distance in bytes between the starts of consecutive rows in the buffer: a multiple of
    /// <see cref="TextureBufferCopy.RowPitchAlignment"/>, and at least the region's width times the texel size.
    /// </summary>
    public int BytesPerRow { get; init; }

    /// <summary>The texture's id: non-zero, the id the backend returned when it created the texture.</summary>
    public ulong Texture { get; init; }

    /// <summary>The mip level copied from or to, inside the texture.</summary>
    public int MipLevel { get; init; }

    /// <summary>The array layer copied from or to, inside the texture.</summary>
    public int ArrayLayer { get; init; }

    /// <summary>The aspect copied, one the texture's format has: depth is copied to buffers, never from them.</summary>
    public TextureAspect Aspect { get; init; }

    /// <summary>The texels copied, inside <see cref="MipLevel"/>.</summary>
    public TextureRegion Region { get; init; }
}
