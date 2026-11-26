namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.CopyTextureToTexture"/> record: a copy of a region from one texture
/// subresource to another (docs/design/graphics.md, "The frame plan").
/// </summary>
/// <remarks>
/// <para>
/// Each side has its own texture id, mip level, array layer and aspect, as the two <see cref="TextureLocation"/>s
/// of a <see cref="TextureCopy"/> do. The region is the source's, and lands at <see cref="DestinationX"/>,
/// <see cref="DestinationY"/> in the destination. The front end resolved the textures to backend ids and checked
/// the copy (section 6: the same format, regions inside both, no overlap within one subresource) before it encoded
/// it. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct CopyTextureToTextureCommand
{
    /// <summary>The source texture's id: non-zero, the id the backend returned when it created the texture.</summary>
    public ulong Source { get; init; }

    /// <summary>The source mip level, inside the source texture.</summary>
    public int SourceMipLevel { get; init; }

    /// <summary>The source array layer, inside the source texture.</summary>
    public int SourceArrayLayer { get; init; }

    /// <summary>The source aspect, one the source texture's format has.</summary>
    public TextureAspect SourceAspect { get; init; }

    /// <summary>
    /// The destination texture's id: non-zero, the id the backend returned when it created the texture.
    /// </summary>
    public ulong Destination { get; init; }

    /// <summary>The destination mip level, inside the destination texture.</summary>
    public int DestinationMipLevel { get; init; }

    /// <summary>The destination array layer, inside the destination texture.</summary>
    public int DestinationArrayLayer { get; init; }

    /// <summary>The destination aspect, one the destination texture's format has.</summary>
    public TextureAspect DestinationAspect { get; init; }

    /// <summary>The texels copied, inside <see cref="SourceMipLevel"/> of the source.</summary>
    public TextureRegion SourceRegion { get; init; }

    /// <summary>The left edge of the copy in the destination mip level, in texels.</summary>
    public int DestinationX { get; init; }

    /// <summary>The top edge of the copy in the destination mip level, in texels.</summary>
    public int DestinationY { get; init; }
}
