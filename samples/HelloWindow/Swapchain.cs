using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>A swapchain with a view and a render-finished semaphore per image.</summary>
/// <remarks>
/// The render-finished semaphores are per image, not per frame in flight: a semaphore that a present waits on can only
/// be signalled again once that image has been acquired again, which per-frame semaphores can't guarantee.
/// Pointers passed to Vulkan point at locals or pinned arrays that outlive each call.
/// </remarks>
internal sealed unsafe class Swapchain
{
    private readonly Device _device;

    private Swapchain(Device device, SwapchainExtent extent, VkSurfaceFormatKHR format, int imageCount)
    {
        _device = device;
        Extent = extent;
        Format = format;
        Images = new VkImage[imageCount];
        Views = new VkImageView[imageCount];
        RenderFinished = new VkSemaphore[imageCount];
    }

    /// <summary>Gets the swapchain handle.</summary>
    public VkSwapchainKHR Handle { get; private set; }

    /// <summary>Gets the image size.</summary>
    public SwapchainExtent Extent { get; }

    /// <summary>Gets the images' usage (<see cref="SwapchainPlanner.ChooseImageUsage"/>).</summary>
    public VkImageUsageFlags Usage { get; private init; }

    /// <summary>Gets the image format and colour space.</summary>
    public VkSurfaceFormatKHR Format { get; }

    /// <summary>Gets the swapchain images (owned by the swapchain).</summary>
    public VkImage[] Images { get; }

    /// <summary>Gets one view per image.</summary>
    public VkImageView[] Views { get; }

    /// <summary>Gets one render-finished semaphore per image: signalled by the frame's submit, waited on by present.</summary>
    public VkSemaphore[] RenderFinished { get; }

    /// <summary>Creates a swapchain, retiring <paramref name="old"/> if given. The caller still destroys
    /// <paramref name="old"/>, whether or not this succeeds.</summary>
    /// <param name="vulkan">The device and surface.</param>
    /// <param name="extent">The image size (not minimised), from <see cref="SwapchainPlanner.ChooseExtent"/>.</param>
    /// <param name="capabilities">The surface capabilities the extent was chosen from.</param>
    /// <param name="old">The swapchain being replaced, or null.</param>
    /// <returns>The swapchain.</returns>
    /// <exception cref="SampleFailureException">The surface can't be rendered to.</exception>
    /// <exception cref="VkResultError">A Vulkan call failed.</exception>
    public static Swapchain Create(VulkanContext vulkan, SwapchainExtent extent, in VkSurfaceCapabilitiesKHR capabilities, Swapchain? old)
    {
        VkImageUsageFlags usage = SwapchainPlanner.ChooseImageUsage(capabilities.SupportedUsageFlags);
        Device device = vulkan.Device;
        VkSurfaceFormatKHR format = SwapchainPlanner.ChooseSurfaceFormat(SurfaceFormats(vulkan.PhysicalDevice, vulkan.Surface));
        VkSwapchainKHR handle = device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = vulkan.Surface,
                MinImageCount = SwapchainPlanner.ChooseImageCount(capabilities.MinImageCount, capabilities.MaxImageCount),
                ImageFormat = format.Format,
                ImageColorSpace = format.ColorSpace,
                ImageExtent = extent.ToVk(),
                ImageArrayLayers = 1,
                ImageUsage = usage,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = SwapchainPlanner.ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                PresentMode = SwapchainPlanner.PresentMode,
                Clipped = true,
                OldSwapchain = old?.Handle ?? VkSwapchainKHR.NullHandle,
            },
            null);

        Swapchain? swapchain = null;
        try
        {
            int count = device.GetSwapchainImagesKHR_Count(handle);
            swapchain = new Swapchain(device, extent, format, count) { Handle = handle, Usage = usage };
            NativeCheck.Vk(device.GetSwapchainImagesKHR_Write(handle, swapchain.Images), "vkGetSwapchainImagesKHR");
            for (int i = 0; i < count; i++)
            {
                swapchain.Views[i] = device.CreateImageView(
                    new VkImageViewCreateInfo
                    {
                        Image = swapchain.Images[i],
                        ViewType = VkImageViewType._2D,
                        Format = format.Format,
                        SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                    },
                    null);
                swapchain.RenderFinished[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            }

            return swapchain;
        }
        catch
        {
            if (swapchain is null)
            {
                device.DestroySwapchainKHR(handle, null);
            }
            else
            {
                swapchain.Destroy();
            }

            throw;
        }
    }

    /// <summary>Destroys the semaphores, views and swapchain. The device must be idle.</summary>
    public void Destroy()
    {
        for (int i = Images.Length - 1; i >= 0; i--)
        {
            if (!RenderFinished[i].IsNull())
            {
                _device.DestroySemaphore(RenderFinished[i], null);
                RenderFinished[i] = default;
            }

            if (!Views[i].IsNull())
            {
                _device.DestroyImageView(Views[i], null);
                Views[i] = default;
            }
        }

        if (!Handle.IsNull())
        {
            _device.DestroySwapchainKHR(Handle, null);
            Handle = VkSwapchainKHR.NullHandle;
        }
    }

    private static VkSurfaceFormatKHR[] SurfaceFormats(PhysicalDevice physicalDevice, VkSurfaceKHR surface)
    {
        // BRUTAL's _Write wrapper hides how many entries were written, so use the raw two-call pattern, and retry if
        // the list grew in between (VK_INCOMPLETE).
        while (true)
        {
            int count = 0;
            NativeCheck.Vk(
                physicalDevice.PInvoke.vkGetPhysicalDeviceSurfaceFormatsKHR.Invoke(physicalDevice.Handle, surface, &count, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new VkSurfaceFormatKHR[count];
            VkResult result;
            fixed (VkSurfaceFormatKHR* pointer = formats)
            {
                result = physicalDevice.PInvoke.vkGetPhysicalDeviceSurfaceFormatsKHR.Invoke(physicalDevice.Handle, surface, &count, pointer);
            }

            if (result != VkResult.Incomplete)
            {
                NativeCheck.Vk(result, "vkGetPhysicalDeviceSurfaceFormatsKHR");
                return formats[..count];
            }
        }
    }
}
