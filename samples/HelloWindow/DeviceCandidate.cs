using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>What <see cref="DeviceSelector"/> needs to know about one physical device.</summary>
/// <param name="Name">The device name.</param>
/// <param name="Type">The device type.</param>
/// <param name="ApiVersion">The Vulkan version the device supports (VK_MAKE_API_VERSION encoding).</param>
/// <param name="DriverId">The driver ID.</param>
/// <param name="SupportsSwapchain">Whether the device offers VK_KHR_swapchain.</param>
/// <param name="SupportsDynamicRendering">Whether the device supports the dynamicRendering feature.</param>
/// <param name="SupportsSynchronization2">Whether the device supports the synchronization2 feature.</param>
/// <param name="GraphicsPresentQueueFamily">A queue family with graphics and present support, or -1 if none.</param>
internal sealed record DeviceCandidate(
    string Name,
    VkPhysicalDeviceType Type,
    int ApiVersion,
    VkDriverId DriverId,
    bool SupportsSwapchain,
    bool SupportsDynamicRendering,
    bool SupportsSynchronization2,
    int GraphicsPresentQueueFamily);
