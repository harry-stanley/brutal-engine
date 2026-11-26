namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.BeginRenderPass"/> record: the start of a render pass, whose attachments
/// follow it (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The record's trailing part is one <see cref="AttachmentCommand"/> per color attachment, in attachment order,
/// then one for the depth attachment when <see cref="HasDepth"/> is set: <see cref="ColorCount"/> elements, plus
/// one with depth. A backend begins dynamic rendering with them, with the viewport and scissor covering the whole
/// attachments. The matching <see cref="CommandKind.EndRenderPass"/> record ends the pass.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct BeginRenderPassCommand
{
    /// <summary>The number of color attachments: from 0 up to the device's <c>MaxColorTargets</c>.</summary>
    public int ColorCount { get; init; }

    /// <summary><see langword="true"/> when the last trailing element is the depth attachment.</summary>
    public bool HasDepth { get; init; }

    /// <summary>
    /// The pass's label, which names its debug group and its timestamps; <c>default</c> for an unlabelled pass. A
    /// backend reads the name with <c>BackendObjectTable.GetLabel</c>.
    /// </summary>
    public DebugLabel Label { get; init; }

    /// <summary>
    /// The timestamp query index the backend writes the pass's start time to, or -1 for none (an unlabelled pass, or
    /// <c>TimestampQueries</c> not enabled).
    /// </summary>
    public int TimestampQueryIndex { get; init; }
}
