using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// What <see cref="SwapchainPlanner.Plan"/> decided for a swapchain: the values that go into
/// <c>VkSwapchainCreateInfoKHR</c> and into the <see cref="SwapchainInfo"/> the device reports
/// (docs/design/graphics-vulkan.md section 4).
/// </summary>
/// <param name="Width">The images' width in pixels; always positive.</param>
/// <param name="Height">The images' height in pixels; always positive.</param>
/// <param name="ImageCount">The minimum number of images to ask for.</param>
/// <param name="CompositeAlpha">A single composite alpha bit the surface supports.</param>
/// <param name="Usage">Colour attachment, plus transfer destination when the surface offers it.</param>
/// <param name="Format">The surface format to create the images with.</param>
/// <param name="ColorSpace">The format's colour space; always <c>SRGB_NONLINEAR</c>.</param>
/// <param name="TextureFormat">The engine format matching <paramref name="Format"/>, for <see cref="SwapchainInfo.Format"/>.</param>
/// <param name="PresentMode">The present mode used, for <see cref="SwapchainInfo.PresentMode"/>.</param>
/// <param name="VkPresentMode">The Vulkan value of <paramref name="PresentMode"/>.</param>
/// <param name="PresentModeFellBack">
/// Whether the requested present mode wasn't offered, so <paramref name="PresentMode"/> is FIFO instead; the swapchain's
/// creation logs it once.
/// </param>
/// <remarks>Immutable and safe to use from any thread. No member allocates, except <c>ToString</c>.</remarks>
internal readonly record struct SwapchainPlan(
    int Width,
    int Height,
    int ImageCount,
    VkCompositeAlphaFlagsKHR CompositeAlpha,
    VkImageUsageFlags Usage,
    VkFormat Format,
    VkColorSpaceKHR ColorSpace,
    TextureFormat TextureFormat,
    PresentMode PresentMode,
    VkPresentModeKHR VkPresentMode,
    bool PresentModeFellBack)
{
    /// <summary>
    /// Gets a value indicating whether <see cref="Usage"/> includes transfer destination, for
    /// <see cref="SwapchainInfo.SupportsCopyDestination"/>.
    /// </summary>
    public bool SupportsCopyDestination => (Usage & VkImageUsageFlags.TransferDstBit) != 0;
}
