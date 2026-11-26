namespace BrutalEngine.Graphics;

/// <summary>What a render pass does with an attachment's contents when it ends (docs/design/graphics.md section 9).</summary>
/// <remarks>Backends map each value to their native one by name, never by casting.</remarks>
public enum StoreOp
{
    /// <summary>Keep what the pass wrote, for later passes, copies or presentation. The default value.</summary>
    Store = 0,

    /// <summary>The pass's results aren't needed afterwards: the attachment's contents are undefined after the pass.</summary>
    DontCare = 1,
}
