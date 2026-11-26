namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.DrawIndexed"/> record: an indexed draw (docs/design/graphics.md, "The
/// frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// A wrapper around <see cref="DrawIndexedArguments"/>, so every kind's payload is named after it. The draw reads
/// the index buffer the last <see cref="CommandKind.SetIndexBuffer"/> record bound. The record has no trailing
/// part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct DrawIndexedCommand
{
    /// <summary>The draw's index, vertex and instance ranges.</summary>
    public DrawIndexedArguments Arguments { get; init; }
}
