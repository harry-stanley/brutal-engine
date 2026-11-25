using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace HelloWindow.Splash;

/// <summary>The splash logo on the GPU: a device-local image, composited onto <see cref="Background"/> and left in
/// <c>TRANSFER_SRC_OPTIMAL</c>, ready to be blitted or copied into swapchain images.</summary>
/// <remarks>
/// <para>
/// <see cref="Upload"/> composites the logo on the CPU straight into a host-visible, host-coherent staging buffer
/// (Vulkan guarantees such a memory type exists, so no flush is needed), records one copy into the image, submits it
/// and waits on a fence. The staging buffer, command pool and fence are destroyed before it returns. Its final barrier
/// makes the upload visible to every later transfer read on the queue, so frames need no extra synchronisation.
/// </para>
/// <para>
/// Every pointer passed to Vulkan (the barrier, region and submit structures) points at a local that outlives the call
/// it is passed to, and Vulkan doesn't keep it.
/// </para>
/// <para>Dispose only when the device is idle. Single-threaded, like the rest of the sample.</para>
/// </remarks>
internal sealed unsafe class SplashImage : IDisposable
{
    /// <summary>The colour behind the logo, and of the rest of every splash frame (see <see cref="Rgb8.Black"/>).</summary>
    public static Rgb8 Background => Rgb8.Black;

    // 10 s, as in FrameRenderer: an upload that takes this long means a hung GPU.
    private const long FenceTimeoutNanoseconds = 10_000_000_000;

    private readonly Device _device;
    private VkImage _image;
    private VkDeviceMemory _memory;

    private SplashImage(Device device, PixelSize size, VkFormat format)
    {
        _device = device;
        Size = size;
        Format = format;
    }

    /// <summary>Gets the image, in <c>TRANSFER_SRC_OPTIMAL</c> layout.</summary>
    public VkImage Image => _image;

    /// <summary>Gets the image's size: the logo's.</summary>
    public PixelSize Size { get; }

    /// <summary>Gets the image's format: the swapchain's (<see cref="SplashPlan.LogoFormat"/>).</summary>
    public VkFormat Format { get; }

    /// <summary>Uploads the logo in the plan's format. Destroys what it created if a step fails.</summary>
    /// <param name="vulkan">The device and queue.</param>
    /// <param name="logo">The decoded logo.</param>
    /// <param name="plan">A supported plan from <see cref="SplashCapabilities.Choose"/>.</param>
    /// <returns>The uploaded image.</returns>
    /// <exception cref="ArgumentException"><paramref name="plan"/> is unsupported.</exception>
    /// <exception cref="SampleFailureException">No suitable memory type.</exception>
    /// <exception cref="VkResultError">A Vulkan call failed.</exception>
    public static SplashImage Upload(VulkanContext vulkan, SplashLogo logo, SplashPlan plan)
    {
        if (!plan.IsSupported)
        {
            throw new ArgumentException($"The splash plan is unsupported: {plan.FailureMessage}", nameof(plan));
        }

        Device device = vulkan.Device;
        VkPhysicalDeviceMemoryProperties memoryProperties = vulkan.PhysicalDevice.GetMemoryProperties();
        var image = new SplashImage(device, logo.Size, plan.LogoFormat);
        try
        {
            image.CreateImage(memoryProperties);
            image.CopyFromStaging(vulkan, memoryProperties, logo, plan.PixelOrder);
            return image;
        }
        catch
        {
            // Nothing is in flight here: the upload either completed its fence wait or was never submitted.
            image.Dispose();
            throw;
        }
    }

    /// <summary>Destroys the image and frees its memory. The device must be idle.</summary>
    public void Dispose()
    {
        if (!_image.IsNull())
        {
            _device.DestroyImage(_image, null);
            _image = default;
        }

        if (!_memory.IsNull())
        {
            _device.FreeMemory(_memory, null);
            _memory = default;
        }
    }

    private static int MemoryType(in VkPhysicalDeviceMemoryProperties properties, in VkMemoryRequirements requirements, VkMemoryPropertyFlags required, VkMemoryPropertyFlags preferred, string what)
    {
        int type = SplashMemory.ChooseType(properties, requirements.MemoryTypeBits, required, preferred);
        return type >= 0 ? type : throw new SampleFailureException($"No Vulkan memory type with {required} for the splash {what}.");
    }

    private void CreateImage(in VkPhysicalDeviceMemoryProperties memoryProperties)
    {
        _image = _device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = Format,
                Extent = new VkExtent3D { Width = Size.Width, Height = Size.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,

                // Written by the upload, then only read as the blit or copy source.
                Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null);
        VkMemoryRequirements requirements = _device.GetImageMemoryRequirements(_image);
        _memory = _device.AllocateMemory(
            new VkMemoryAllocateInfo
            {
                AllocationSize = requirements.Size,
                MemoryTypeIndex = MemoryType(memoryProperties, requirements, VkMemoryPropertyFlags.None, VkMemoryPropertyFlags.DeviceLocalBit, "image"),
            },
            null);
        _device.BindImageMemory(_image, _memory, default);
    }

    private void CopyFromStaging(VulkanContext vulkan, in VkPhysicalDeviceMemoryProperties memoryProperties, SplashLogo logo, LogoPixelOrder order)
    {
        int byteCount = logo.Rgba.Length;
        VkBuffer staging = default;
        VkDeviceMemory stagingMemory = default;
        VkCommandPool pool = default;
        VkFence fence = default;
        bool pending = false;
        try
        {
            staging = _device.CreateBuffer(
                new VkBufferCreateInfo { Size = (ByteSize64)byteCount, Usage = VkBufferUsageFlags.TransferSrcBit, SharingMode = VkSharingMode.Exclusive },
                null);
            VkMemoryRequirements requirements = _device.GetBufferMemoryRequirements(staging);
            const VkMemoryPropertyFlags HostVisible = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit;
            stagingMemory = _device.AllocateMemory(
                new VkMemoryAllocateInfo { AllocationSize = requirements.Size, MemoryTypeIndex = MemoryType(memoryProperties, requirements, HostVisible, HostVisible, "staging buffer") },
                null);
            _device.BindBufferMemory(staging, stagingMemory, default);
            Fill(stagingMemory, logo, order);

            pool = _device.CreateCommandPool(
                new VkCommandPoolCreateInfo { QueueFamilyIndex = vulkan.QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit },
                null);
            CommandBuffer commands = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
            RecordUpload(commands, staging);

            fence = _device.CreateFence(new VkFenceCreateInfo(), null);
            VkCommandBufferSubmitInfo commandInfo = new() { CommandBuffer = commands };
            VkSubmitInfo2 submit = new() { CommandBufferInfoCount = 1, CommandBufferInfos = &commandInfo };

            // Sound: submit points at commandInfo, a local that outlives the call; Vulkan copies the submit info.
            vulkan.Queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fence);
            pending = true;
            NativeCheck.Vk(_device.WaitForFences([fence], true, unchecked((nint)FenceTimeoutNanoseconds)), "vkWaitForFences (splash upload)");
            pending = false;
        }
        finally
        {
            // If the fence wait failed (a timeout or a lost device), wait for the whole device before destroying what
            // the upload uses. Its result is ignored: the wait's own failure is already being thrown, and destroying
            // objects is valid even after a device loss.
            if (pending)
            {
                _ = vulkan.WaitIdle();
            }

            if (!fence.IsNull())
            {
                _device.DestroyFence(fence, null);
            }

            if (!pool.IsNull())
            {
                _device.DestroyCommandPool(pool, null);
            }

            if (!staging.IsNull())
            {
                _device.DestroyBuffer(staging, null);
            }

            if (!stagingMemory.IsNull())
            {
                _device.FreeMemory(stagingMemory, null);
            }
        }
    }

    private void Fill(VkDeviceMemory memory, SplashLogo logo, LogoPixelOrder order)
    {
        int byteCount = logo.Rgba.Length;
        void* mapped = null;

        // Raw call: BRUTAL's MapMemory wrapper is internal (Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs,
        // MapMemory). Sound: maps the whole buffer, which is at least byteCount bytes, and the span below covers exactly
        // byteCount bytes of it; the memory is host-coherent, so the writes need no flush, and it is unmapped before use.
        NativeCheck.Vk(
            _device.PInvoke.vkMapMemory.Invoke(_device.Handle, memory, default, (ByteSize64)byteCount, VkMemoryMapFlags.None, &mapped),
            "vkMapMemory (splash staging)");
        try
        {
            LogoCompositor.Composite(logo.Rgba.Span, new Span<byte>(mapped, byteCount), Background, order);
        }
        finally
        {
            _device.UnmapMemory(memory);
        }
    }

    private void RecordUpload(CommandBuffer commands, VkBuffer staging)
    {
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        VkImageSubresourceRange range = new() { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

        // UNDEFINED -> TRANSFER_DST: nothing to wait for, the image is new.
        VkImageMemoryBarrier2 toTransferDst = new()
        {
            SrcStageMask = VkPipelineStageFlags2.None,
            SrcAccessMask = VkAccessFlags2.None,
            DstStageMask = VkPipelineStageFlags2.CopyBit,
            DstAccessMask = VkAccessFlags2.TransferWriteBit,
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = _image,
            SubresourceRange = range,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toTransferDst });

        VkBufferImageCopy region = new()
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = Size.Width, Height = Size.Height, Depth = 1 },
        };
        commands.CopyBufferToImage(staging, _image, VkImageLayout.TransferDstOptimal, [region]);

        // TRANSFER_DST -> TRANSFER_SRC, visible to every later blit or copy on this queue (a barrier's second scope
        // covers later submissions too).
        VkImageMemoryBarrier2 toTransferSrc = new()
        {
            SrcStageMask = VkPipelineStageFlags2.CopyBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.CopyBit,
            DstAccessMask = VkAccessFlags2.TransferReadBit,
            OldLayout = VkImageLayout.TransferDstOptimal,
            NewLayout = VkImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = _image,
            SubresourceRange = range,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toTransferSrc });
        commands.End();
    }
}
