namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.SetIndexBuffer"/> record: the index buffer the indexed draws after it
/// read (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct SetIndexBufferCommand
{
    /// <summary>The size of each index in the buffer.</summary>
    public IndexFormat Format { get; init; }

    /// <summary>The buffer's id: non-zero, the id the backend returned when it created the buffer.</summary>
    public ulong Buffer { get; init; }

    /// <summary>
    /// The byte offset in the buffer where the indices start: at least 0, and a multiple of the index size.
    /// </summary>
    public long Offset { get; init; }
}
