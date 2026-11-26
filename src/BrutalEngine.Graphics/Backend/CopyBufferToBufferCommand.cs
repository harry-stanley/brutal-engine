namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.CopyBufferToBuffer"/> record: a copy of a byte range from one buffer to
/// another, or within one (docs/design/graphics.md, "The frame plan").
/// </summary>
/// <remarks>
/// <para>
/// The front end resolved the buffers to backend ids and checked the copy (section 6: usages, alignment, ranges
/// inside the buffers, no overlap within one buffer) before it encoded it, so a backend passes the fields to its
/// native copy unchanged. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct CopyBufferToBufferCommand
{
    /// <summary>The source buffer's id: non-zero, the id the backend returned when it created the buffer.</summary>
    public ulong Source { get; init; }

    /// <summary>The byte offset of the range in the source buffer: at least 0 and a multiple of 4.</summary>
    public long SourceOffset { get; init; }

    /// <summary>
    /// The destination buffer's id: non-zero, the id the backend returned when it created the buffer.
    /// </summary>
    public ulong Destination { get; init; }

    /// <summary>The byte offset of the range in the destination buffer: at least 0 and a multiple of 4.</summary>
    public long DestinationOffset { get; init; }

    /// <summary>The number of bytes copied: greater than 0 and a multiple of 4.</summary>
    public long Size { get; init; }
}
