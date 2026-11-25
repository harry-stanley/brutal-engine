using System;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace HelloWindow;

/// <summary>Records, submits and presents one clear-colour frame at a time, with 2 frames in flight.</summary>
/// <remarks>
/// <para>
/// Each frame in flight owns a command buffer, an image-acquired semaphore and a fence. A frame waits for its fence
/// before reusing them, and resets the fence only once a submit is certain: an OUT_OF_DATE acquire then leaves the
/// fence signalled instead of waiting forever. Render-finished semaphores belong to the swapchain images
/// (<see cref="Swapchain.RenderFinished"/>).
/// </para>
/// <para>
/// Acquire and present go through BRUTAL's raw function table, because its wrappers throw on OUT_OF_DATE
/// (Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs, AcquireNextImageKHR and PresentKHRInternal), and
/// that is a routine resize event here. Every result goes through <see cref="FrameResultPolicy"/>. Pointers passed to
/// Vulkan point at locals that outlive each call. A frame allocates no managed memory.
/// </para>
/// </remarks>
internal sealed unsafe class FrameRenderer : IDisposable
{
    private const int FramesInFlight = 2;

    // BRUTAL binds the uint64 timeouts as nint (docs/spikes/README.md, S1). -1 is UINT64_MAX: wait forever.
    private const nint NoTimeout = -1;

    // 10 s: a frame that takes this long means a hung GPU, so fail rather than hang a CI job. Fits in nint on every
    // supported RID (all 64-bit).
    private const long FenceTimeoutNanoseconds = 10_000_000_000;

    private readonly Device _device;
    private readonly Queue _queue;
    private readonly VkCommandPool _pool;
    private readonly CommandBuffer[] _commands = new CommandBuffer[FramesInFlight];
    private readonly VkSemaphore[] _imageAcquired = new VkSemaphore[FramesInFlight];
    private readonly VkFence[] _inFlight = new VkFence[FramesInFlight];
    private long _frame;

    /// <summary>Creates the per-frame objects. Destroys what it created if a step fails.</summary>
    /// <param name="vulkan">The device and queue.</param>
    /// <exception cref="VkResultError">A Vulkan call failed.</exception>
    public FrameRenderer(VulkanContext vulkan)
    {
        _device = vulkan.Device;
        _queue = vulkan.Queue;
        try
        {
            _pool = _device.CreateCommandPool(
                new VkCommandPoolCreateInfo { QueueFamilyIndex = vulkan.QueueFamily, Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit },
                null);
            _device.AllocateCommandBuffers(new VkCommandBufferAllocateInfo { CommandPool = _pool, Level = VkCommandBufferLevel.Primary }, _commands);
            for (int i = 0; i < FramesInFlight; i++)
            {
                _imageAcquired[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);

                // Signalled, so each slot's first wait returns at once.
                _inFlight[i] = _device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Renders and presents one frame.</summary>
    /// <param name="swapchain">The current swapchain.</param>
    /// <param name="injectValidationError">Record a wrong old layout in the first barrier, which validation must report.</param>
    /// <returns><see cref="FrameAction.Render"/> if presented, <see cref="FrameAction.RenderThenRecreate"/> if presented
    /// but the swapchain should be recreated, or <see cref="FrameAction.Recreate"/> if nothing was presented.</returns>
    /// <exception cref="VkResultError">A Vulkan call failed, for example with a lost device.</exception>
    public FrameAction RenderFrame(Swapchain swapchain, bool injectValidationError)
    {
        int slot = (int)(_frame % FramesInFlight);
        VkFence fence = _inFlight[slot];
        NativeCheck.Vk(_device.WaitForFences([fence], true, unchecked((nint)FenceTimeoutNanoseconds)), "vkWaitForFences");

        int imageIndex;
        VkResult acquireResult = _device.PInvoke.vkAcquireNextImageKHR.Invoke(
            _device.Handle, swapchain.Handle, NoTimeout, _imageAcquired[slot], VkFence.NullHandle, &imageIndex);
        FrameDecision acquire = FrameResultPolicy.Map(acquireResult);
        if (acquire.Action == FrameAction.Fatal)
        {
            throw new VkResultError(acquireResult, acquire.Describe("vkAcquireNextImageKHR"));
        }

        if (acquire.Action == FrameAction.Recreate)
        {
            // Nothing was acquired, so the semaphore is unsignalled and the fence untouched: both can be reused as-is.
            return FrameAction.Recreate;
        }

        _device.ResetFences([fence]);
        CommandBuffer commands = _commands[slot];
        commands.Reset(VkCommandBufferResetFlags.None);
        Record(commands, swapchain, imageIndex, injectValidationError);

        VkSemaphore renderFinished = swapchain.RenderFinished[imageIndex];
        VkSemaphoreSubmitInfo wait = new() { Semaphore = _imageAcquired[slot], StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
        VkSemaphoreSubmitInfo signal = new() { Semaphore = renderFinished, StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
        VkCommandBufferSubmitInfo commandInfo = new() { CommandBuffer = commands };
        VkSubmitInfo2 submit = new()
        {
            WaitSemaphoreInfoCount = 1,
            WaitSemaphoreInfos = &wait,
            CommandBufferInfoCount = 1,
            CommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = 1,
            SignalSemaphoreInfos = &signal,
        };
        _queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fence);
        _frame++;

        VkSwapchainKHR handle = swapchain.Handle;
        VkPresentInfoKHR presentInfo = new()
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &handle,
            ImageIndices = &imageIndex,
        };

        // Raw call: BRUTAL's Queue serialises Submit2 and PresentKHR under one lock, and only this thread uses the queue.
        VkResult presentResult = _queue.PInvoke.vkQueuePresentKHR.Invoke(_queue.Handle, &presentInfo);
        FrameDecision present = FrameResultPolicy.Map(presentResult);
        if (present.Action == FrameAction.Fatal)
        {
            throw new VkResultError(presentResult, present.Describe("vkQueuePresentKHR"));
        }

        return present.Action == FrameAction.Render ? acquire.Action : present.Action;
    }

    /// <summary>Destroys the per-frame objects. The device must be idle.</summary>
    public void Dispose()
    {
        for (int i = FramesInFlight - 1; i >= 0; i--)
        {
            if (!_inFlight[i].IsNull())
            {
                _device.DestroyFence(_inFlight[i], null);
                _inFlight[i] = default;
            }

            if (!_imageAcquired[i].IsNull())
            {
                _device.DestroySemaphore(_imageAcquired[i], null);
                _imageAcquired[i] = default;
            }
        }

        // Destroying the pool frees its command buffers.
        if (!_pool.IsNull())
        {
            _device.DestroyCommandPool(_pool, null);
        }
    }

    private void Record(CommandBuffer commands, Swapchain swapchain, int imageIndex, bool injectValidationError)
    {
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        VkImageSubresourceRange colorRange = new() { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

        // UNDEFINED -> COLOR_ATTACHMENT (the clear overwrites everything). The source stage is the stage the acquire
        // semaphore is waited on in, so the transition runs only after the presentation engine has released the image.
        VkImageMemoryBarrier2 toAttachment = new()
        {
            SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags2.None,
            DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,

            // Negative test: claim a layout the image is not in, which validation must report at submit.
            OldLayout = injectValidationError ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined,
            NewLayout = VkImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = swapchain.Images[imageIndex],
            SubresourceRange = colorRange,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toAttachment });

        // A slow colour cycle, so a frozen window is easy to spot. Linear values: the sRGB image encodes them.
        float t = _frame / 90.0f;
        VkRenderingAttachmentInfo colorAttachment = new()
        {
            ImageView = swapchain.Views[imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue
            {
                Float32 = new float4(0.25f + (0.2f * MathF.Sin(t)), 0.25f + (0.2f * MathF.Sin(t + 2.1f)), 0.25f + (0.2f * MathF.Sin(t + 4.2f)), 1.0f),
            },
        };
        commands.BeginRendering(new VkRenderingInfo
        {
            RenderArea = new VkRect2D(swapchain.Extent.ToVk()),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
        });
        commands.EndRendering();

        // COLOR_ATTACHMENT -> PRESENT_SRC. The destination stage is the render-finished semaphore's signal stage, so
        // the signal waits for the transition; presentation makes the writes visible itself, so no destination access.
        VkImageMemoryBarrier2 toPresent = new()
        {
            SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags2.None,
            OldLayout = VkImageLayout.ColorAttachmentOptimal,
            NewLayout = VkImageLayout.PresentSrcKHR,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = swapchain.Images[imageIndex],
            SubresourceRange = colorRange,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toPresent });
        commands.End();
    }
}
