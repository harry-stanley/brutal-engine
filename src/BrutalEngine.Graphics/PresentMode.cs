namespace BrutalEngine.Graphics;

/// <summary>How a swapchain hands finished images to the display: with or without waiting for vertical sync.</summary>
/// <remarks>
/// <para>
/// Only <see cref="Fifo"/> is supported everywhere. A swapchain asked for a mode the surface doesn't offer falls back to
/// <see cref="Fifo"/>, logs it once, and reports the mode it really uses in <see cref="SwapchainInfo.PresentMode"/>
/// (docs/design/graphics.md section 3).
/// </para>
/// <para>
/// The numeric values are fixed. They are not <c>VkPresentModeKHR</c>'s values, so backends map them by name, never by
/// casting.
/// </para>
/// </remarks>
public enum PresentMode
{
    /// <summary>
    /// Vertical sync: images queue up and the display takes one per refresh, so nothing tears. Supported by every
    /// implementation, and the default.
    /// </summary>
    Fifo = 0,

    /// <summary>
    /// Vertical sync, except that an image that misses its refresh is shown at once, which can tear. Smooths over an
    /// occasional late frame.
    /// </summary>
    FifoRelaxed = 1,

    /// <summary>
    /// No tearing, with the newest image replacing any image still waiting: lower latency than <see cref="Fifo"/>, and
    /// some frames are never shown.
    /// </summary>
    Mailbox = 2,

    /// <summary>No vertical sync: images are shown as soon as they are presented, which can tear.</summary>
    Immediate = 3,
}
