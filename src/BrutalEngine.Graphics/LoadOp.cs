namespace BrutalEngine.Graphics;

/// <summary>What a render pass does with an attachment's contents when it begins (docs/design/graphics.md section 9).</summary>
/// <remarks>
/// The frame planner derives the attachment's usage from it (section 10): <see cref="Load"/> also reads the attachment.
/// Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum LoadOp
{
    /// <summary>Keep the attachment's earlier contents. The default value.</summary>
    Load = 0,

    /// <summary>Clear the attachment to the attachment's clear value.</summary>
    Clear = 1,

    /// <summary>
    /// The earlier contents aren't needed: the pass starts with undefined contents, so pixels the pass doesn't write are
    /// undefined afterwards.
    /// </summary>
    DontCare = 2,
}
