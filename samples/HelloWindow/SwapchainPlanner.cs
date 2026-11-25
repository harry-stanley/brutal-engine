using System;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Pure swapchain decisions: extent, image count, surface format, composite alpha and present mode.</summary>
internal static class SwapchainPlanner
{
    /// <summary>FIFO: the only present mode every implementation must support (vsync, never tears).</summary>
    public const VkPresentModeKHR PresentMode = VkPresentModeKHR.FifoKHR;

    /// <summary>Chooses the swapchain extent.</summary>
    /// <param name="capabilities">The surface capabilities.</param>
    /// <param name="windowPixelWidth">The window width in pixels (SDL_GetWindowSizeInPixels).</param>
    /// <param name="windowPixelHeight">The window height in pixels.</param>
    /// <returns>The extent; <see cref="SwapchainExtent.IsMinimised"/> if there is nothing to render to.</returns>
    public static SwapchainExtent ChooseExtent(in VkSurfaceCapabilitiesKHR capabilities, int windowPixelWidth, int windowPixelHeight)
    {
        // A current extent of 0xFFFFFFFF (-1 as int) means the swapchain decides, e.g. headless and Wayland surfaces.
        VkExtent2D current = capabilities.CurrentExtent;
        if (current.Width != -1 || current.Height != -1)
        {
            return new SwapchainExtent(current.Width, current.Height);
        }

        // Check before clamping: clamping to minImageExtent would turn a minimised window into a 1x1 swapchain.
        if (windowPixelWidth <= 0 || windowPixelHeight <= 0)
        {
            return default;
        }

        VkExtent2D min = capabilities.MinImageExtent;
        VkExtent2D max = capabilities.MaxImageExtent;
        return new SwapchainExtent(
            Math.Min(Math.Max(windowPixelWidth, min.Width), max.Width),
            Math.Min(Math.Max(windowPixelHeight, min.Height), max.Height));
    }

    /// <summary>Chooses the swapchain image usage: colour attachment for the scene, plus transfer destination when the
    /// surface offers it, so the splash can be cleared and blitted or copied into the images.</summary>
    /// <param name="supported">The surface's supported usage (<c>VkSurfaceCapabilitiesKHR.supportedUsageFlags</c>).</param>
    /// <returns>The usage to request.</returns>
    /// <exception cref="SampleFailureException">The surface doesn't support colour-attachment images.</exception>
    public static VkImageUsageFlags ChooseImageUsage(VkImageUsageFlags supported)
    {
        if ((supported & VkImageUsageFlags.ColorAttachmentBit) == 0)
        {
            throw new SampleFailureException("The surface doesn't support colour-attachment images.");
        }

        return VkImageUsageFlags.ColorAttachmentBit | (supported & VkImageUsageFlags.TransferDstBit);
    }

    /// <summary>Chooses the number of swapchain images: one more than the minimum, within the maximum.</summary>
    /// <param name="minImageCount">The surface's minimum image count (at least 1).</param>
    /// <param name="maxImageCount">The surface's maximum image count, or 0 for no maximum.</param>
    /// <returns>The image count to request.</returns>
    public static int ChooseImageCount(int minImageCount, int maxImageCount)
    {
        // One spare image, so acquiring never has to wait for the presentation engine to release the only free one.
        int count = minImageCount + 1;
        return maxImageCount > 0 ? Math.Min(count, maxImageCount) : count;
    }

    /// <summary>Chooses <c>B8G8R8A8_SRGB</c> with <c>SRGB_NONLINEAR</c> if offered, otherwise the first format.</summary>
    /// <param name="formats">The surface formats, in the order the driver reported them.</param>
    /// <returns>The chosen format.</returns>
    /// <exception cref="ArgumentException"><paramref name="formats"/> is empty.</exception>
    public static VkSurfaceFormatKHR ChooseSurfaceFormat(ReadOnlySpan<VkSurfaceFormatKHR> formats)
    {
        if (formats.IsEmpty)
        {
            throw new ArgumentException("The surface reported no formats.", nameof(formats));
        }

        foreach (VkSurfaceFormatKHR format in formats)
        {
            if (format.Format == VkFormat.B8G8R8A8SRGB && format.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                return format;
            }
        }

        return formats[0];
    }

    /// <summary>Chooses opaque composite alpha if supported, otherwise the lowest supported mode.</summary>
    /// <param name="supported">The surface's supported composite alpha modes.</param>
    /// <returns>A single composite alpha mode.</returns>
    /// <exception cref="ArgumentException"><paramref name="supported"/> is empty.</exception>
    public static VkCompositeAlphaFlagsKHR ChooseCompositeAlpha(VkCompositeAlphaFlagsKHR supported)
    {
        if (supported == VkCompositeAlphaFlagsKHR.None)
        {
            throw new ArgumentException("The surface reported no composite alpha modes.", nameof(supported));
        }

        // Opaque is the lowest bit, so the lowest set bit is opaque whenever it's supported.
        uint bits = (uint)supported;
        return (VkCompositeAlphaFlagsKHR)(bits & (0u - bits));
    }
}
