namespace BrutalEngine.Graphics;

/// <summary>The outcome of acquiring a swapchain image for the frame being recorded.</summary>
/// <remarks>
/// <para>
/// Acquisition and recreation are observable results, not exceptions (docs/design/graphics.md section 3). Only
/// <see cref="Acquired"/> and <see cref="Recreated"/> come with an image. Whatever the status, the frame still
/// completes: the host may record offscreen work or submit it empty.
/// </para>
/// <para>The numeric values are fixed.</para>
/// </remarks>
public enum AcquireStatus
{
    /// <summary>An image was acquired and is valid for this frame.</summary>
    Acquired = 0,

    /// <summary>
    /// An image was acquired after recreating the swapchain (after a resize, an out-of-date or suboptimal present, or
    /// device recovery), so its size or format may have changed: check <see cref="SwapchainInfo"/> and resize the targets
    /// that depend on it.
    /// </summary>
    Recreated = 1,

    /// <summary>
    /// The window is minimised or has a zero side: no image, so render nothing to it (the host waits for events).
    /// </summary>
    Minimised = 2,

    /// <summary>The swapchain was still out of date after one recreation: no image this frame; try again next frame.</summary>
    Skipped = 3,

    /// <summary>The window's surface is gone (rare): no image; destroy the swapchain and create it again.</summary>
    SurfaceLost = 4,

    /// <summary>The device was lost: no image (docs/design/graphics.md section 12).</summary>
    DeviceLost = 5,
}
