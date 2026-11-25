using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>A swapchain size in pixels. Zero in either dimension means the window is minimised.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
internal readonly record struct SwapchainExtent(int Width, int Height)
{
    /// <summary>Gets a value indicating whether there is nothing to render to (a minimised window).</summary>
    public bool IsMinimised => Width == 0 || Height == 0;

    /// <summary>Converts to the Vulkan struct.</summary>
    /// <returns>The extent as a <see cref="VkExtent2D"/>.</returns>
    public VkExtent2D ToVk() => new(Width, Height);

    /// <inheritdoc/>
    public override string ToString() => $"{Width}x{Height}";
}
