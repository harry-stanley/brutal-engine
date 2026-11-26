namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// What a backend's swapchain acquire or present reported, which the front end maps to <see cref="AcquireStatus"/> and
/// <see cref="FrameStatus"/> (docs/design/graphics.md, "The backend interface").
/// </summary>
/// <remarks>
/// <para>
/// Every native outcome of an acquire or present lands on one of these: a backend never throws for one. Any native failure
/// that is neither out of date nor surface or device loss (for example out of memory) is reported as
/// <see cref="DeviceLost"/> with its native code, because a frame call has no partial recovery.
/// </para>
/// <para>
/// The numeric values are fixed. They are no native API's values, so backends map them by name, never by casting.
/// </para>
/// </remarks>
public enum SwapchainStatus
{
    /// <summary>The image was acquired or presented. The default value.</summary>
    Success = 0,

    /// <summary>
    /// The image was acquired or presented, but the swapchain no longer matches the surface exactly; the front end
    /// recreates it at the next acquire.
    /// </summary>
    Suboptimal = 1,

    /// <summary>
    /// The swapchain no longer matches the surface and can't be used: no image was acquired, or the present didn't happen.
    /// The front end recreates it.
    /// </summary>
    OutOfDate = 2,

    /// <summary>The window's surface is gone; the swapchain can't be used again.</summary>
    SurfaceLost = 3,

    /// <summary>The device is lost, or the call failed in a way the frame can't recover from; the native code says which.</summary>
    DeviceLost = 4,
}
