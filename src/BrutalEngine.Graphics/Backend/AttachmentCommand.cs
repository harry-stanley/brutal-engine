namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// One attachment of a render pass: an element of a <see cref="CommandKind.BeginRenderPass"/> record's trailing
/// part (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The same struct serves color and depth attachments; the record's <see cref="BeginRenderPassCommand"/> says
/// which is which. A color attachment clears to <see cref="ClearColor"/> (section 9's <c>ColorAttachment.Clear</c>)
/// and the depth attachment to <see cref="ClearDepth"/> (<c>DepthAttachment.Clear</c>); the other clear field is
/// unused. The clear values matter only when <see cref="Load"/> is <see cref="LoadOp.Clear"/>.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct AttachmentCommand
{
    /// <summary>
    /// The attachment's texture view id: non-zero, the id the backend returned when it created the view.
    /// </summary>
    public ulong View { get; init; }

    /// <summary>What the pass does with the attachment's contents when it begins.</summary>
    public LoadOp Load { get; init; }

    /// <summary>What the pass does with the attachment's contents when it ends.</summary>
    public StoreOp Store { get; init; }

    /// <summary>The color a color attachment clears to, in linear space. Unused for the depth attachment.</summary>
    public ColorValue ClearColor { get; init; }

    /// <summary>The depth the depth attachment clears to, from 0 to 1. Unused for color attachments.</summary>
    public float ClearDepth { get; init; }
}
