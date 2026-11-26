namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.Draw"/> record: a non-indexed draw (docs/design/graphics.md, "The
/// frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// A wrapper around <see cref="DrawArguments"/>, so every kind's payload is named after it. The record has no
/// trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct DrawCommand
{
    /// <summary>The draw's vertex and instance ranges.</summary>
    public DrawArguments Arguments { get; init; }
}
