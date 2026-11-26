namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.ClearSwapchainImage"/> record: an acquired swapchain image that no pass
/// wrote, which the backend clears to black before present (docs/design/graphics.md, "The frame plan" and
/// section 3).
/// </summary>
/// <remarks>
/// <para>
/// The clear means the presentation engine never shows uninitialised memory. The image is named by its position in
/// <see cref="FramePlan.Acquired"/>, not by id. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct ClearSwapchainImageCommand
{
    /// <summary>The image's index into <see cref="FramePlan.Acquired"/>: at least 0 and less than its length.</summary>
    public int AcquiredIndex { get; init; }
}
