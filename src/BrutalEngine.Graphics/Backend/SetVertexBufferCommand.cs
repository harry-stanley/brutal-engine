namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.SetVertexBuffer"/> record: a vertex buffer bound at a slot for the draws
/// after it (docs/design/graphics.md, "The frame plan" and section 9).
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
public readonly struct SetVertexBufferCommand
{
    /// <summary>The vertex buffer slot, as the pipeline's vertex layout numbers it: at least 0.</summary>
    public int Slot { get; init; }

    /// <summary>The buffer's id: non-zero, the id the backend returned when it created the buffer.</summary>
    public ulong Buffer { get; init; }

    /// <summary>The byte offset in the buffer where the slot's vertices start: at least 0.</summary>
    public long Offset { get; init; }
}
