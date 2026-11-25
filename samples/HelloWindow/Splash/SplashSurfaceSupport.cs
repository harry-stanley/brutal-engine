using Brutal.VulkanApi;

namespace HelloWindow.Splash;

/// <summary>What the surface and device offer for the splash's transfer.</summary>
/// <param name="SwapchainFormat">The swapchain's format and colour space.</param>
/// <param name="SupportedUsage">The surface's supported image usage (<c>VkSurfaceCapabilitiesKHR.supportedUsageFlags</c>).</param>
/// <param name="OptimalTilingFeatures">The optimal-tiling features of the swapchain format
/// (<c>vkGetPhysicalDeviceFormatProperties</c>). The logo is uploaded in the same format, so they apply to both images.</param>
internal readonly record struct SplashSurfaceSupport(
    VkSurfaceFormatKHR SwapchainFormat,
    VkImageUsageFlags SupportedUsage,
    VkFormatFeatureFlags OptimalTilingFeatures);
