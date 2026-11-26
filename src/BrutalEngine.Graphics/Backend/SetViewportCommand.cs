namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.SetViewport"/> record: the viewport for the draws after it in the
/// render pass (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// A wrapper around <see cref="Graphics.Viewport"/>, so every kind's payload is named after it. A render pass
/// starts with the viewport covering its whole attachments. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct SetViewportCommand
{
    /// <summary>The viewport, in pixels, with its depth range.</summary>
    public Viewport Viewport { get; init; }
}
