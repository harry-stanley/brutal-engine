// Spike S1+S2: SDL3 window + Vulkan 1.3 (dynamic rendering + synchronization2) clear loop on BRUTAL packages.
// Usage: Spike [frames=300] [--no-resize]
// Exit codes: 0 ok, 1 validation errors, 2 SDL init failed, 3 unhandled exception.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.SdlApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Spike;

static unsafe class Program
{
    const int FramesInFlight = 2;
    const int Api13 = 1 << 22 | 3 << 12;
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    static int s_vkErrors, s_vkWarnings;
    static bool s_leakTest; // --leak: deliberately leak one semaphore to prove validation is live

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static Bool32 DebugCallback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var msg = data != null && data->Message != null ? Marshal.PtrToStringUTF8((nint)data->Message) : "<null>";
        if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
        {
            Interlocked.Increment(ref s_vkErrors);
            Console.Error.WriteLine($"[VK ERROR/{type}] {msg}");
        }
        else if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) != 0)
        {
            Interlocked.Increment(ref s_vkWarnings);
            Console.Error.WriteLine($"[VK WARN/{type}] {msg}");
        }
        return false; // never abort the call
    }

    static int Main(string[] args)
    {
        var targetFrames = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 300;
        var resizeTest = !args.Contains("--no-resize");
        s_leakTest = args.Contains("--leak");
        Console.WriteLine($"Spike S1+S2: frames={targetFrames} resizeTest={resizeTest} aot={!RuntimeFeature.IsDynamicCodeSupported}");

        try
        {
            Sdl.Init.Initialize(SdlInitFlags.Video);
        }
        catch (SdlException e)
        {
            Console.Error.WriteLine($"SDL init failed: {e.Message}");
            return 2;
        }

        int rc;
        try
        {
            rc = Run(targetFrames, resizeTest);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FATAL: {e}");
            rc = 3;
        }
        finally
        {
            Sdl.Init.Quit();
        }

        Console.WriteLine($"Validation messages: errors={s_vkErrors} warnings={s_vkWarnings}");
        if (rc == 0 && s_vkErrors > 0)
            rc = 1;
        Console.WriteLine($"Exit code {rc}");
        return rc;
    }

    static int Run(int targetFrames, bool resizeTest)
    {
        // Load the Vulkan loader through BRUTAL before SDL touches Vulkan.
        _ = Vulkan.GetInstanceProcAddr;

        // Window is created first, so it is disposed last.
        using var window = Sdl.Video.CreateWindow(
            "BRUTAL S1+S2 spike", 800, 600,
            SdlWindowFlags.Vulkan | SdlWindowFlags.Resizable | SdlWindowFlags.HighPixelDensity);

        // ---- Instance -------------------------------------------------------------------------
        var extensions = new HashSet<string>(Sdl.Vulkan.GetInstanceExtensions());
        Console.WriteLine($"SDL instance extensions: {string.Join(", ", extensions)}");
        extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
        extensions.Add(VK.EXT_VALIDATION_FEATURES_EXTENSION_NAME);

        var layerCount = Vulkan.EnumerateInstanceLayerProperties_Count();
        var layerProps = new VkLayerProperties[layerCount];
        Check(Vulkan.EnumerateInstanceLayerProperties_Write(layerProps), "vkEnumerateInstanceLayerProperties");
        var haveValidation = false;
        for (var i = 0; i < layerProps.Length; i++)
        {
            var lp = layerProps[i];
            if (Marshal.PtrToStringUTF8((nint)lp.LayerName) == ValidationLayer)
                haveValidation = true;
        }
        if (!haveValidation)
            throw new InvalidOperationException($"{ValidationLayer} not found (is the Vulkan SDK installed?)");

        var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
            MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&DebugCallback),
        };
        // Synchronization validation on, so the per-frame sync is actually checked, not just API usage.
        var enables = stackalloc VkValidationFeatureEnableEXT[] { VkValidationFeatureEnableEXT.SynchronizationValidationEXT };
        var validationFeatures = new VkValidationFeaturesEXT
        {
            EnabledValidationFeatureCount = 1,
            EnabledValidationFeatures = enables,
            Next = &messengerInfo, // messenger in the pNext chain also covers vkCreate/DestroyInstance
        };

        using var instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = Api13 },
            Flags = VkInstanceCreateFlags.None,
            EnabledExtensions = extensions,
            EnabledLayers = [ValidationLayer],
            Next = &validationFeatures,
        });
        var messenger = instance.CreateDebugUtilsMessengerEXT(messengerInfo, null);
        try
        {
            var surface = new VkSurfaceKHR(Sdl.Vulkan.CreateSurface(window, instance.VkHandle));
            try
            {
                return RunWithSurface(window, instance, surface, targetFrames, resizeTest);
            }
            finally
            {
                instance.DestroySurfaceKHR(surface, null);
            }
        }
        finally
        {
            instance.DestroyDebugUtilsMessengerEXT(messenger, null);
        }
    }

    static int RunWithSurface(SdlWindow window, Instance instance, VkSurfaceKHR surface, int targetFrames, bool resizeTest)
    {
        // ---- Physical device ------------------------------------------------------------------
        PhysicalDevice? physicalDevice = null;
        var queueFamily = -1;
        var bestScore = -1;
        foreach (var pd in instance.PhysicalDevices)
        {
            var props = pd.GetProperties();
            var name = Marshal.PtrToStringUTF8((nint)props.DeviceName);
            if ((uint)props.ApiVersion < Api13 || !pd.SupportsExtension(VK.KHR_SWAPCHAIN_EXTENSION_NAME))
            {
                Console.WriteLine($"  skip {name}: needs Vulkan 1.3 + VK_KHR_swapchain");
                continue;
            }

            var f13 = new VkPhysicalDeviceVulkan13Features();
            var f2 = new VkPhysicalDeviceFeatures2 { Next = &f13 };
            pd.GetFeatures2(ref f2);
            if (!f13.DynamicRendering || !f13.Synchronization2)
            {
                Console.WriteLine($"  skip {name}: no dynamicRendering/synchronization2");
                continue;
            }

            var families = new VkQueueFamilyProperties[pd.GetQueueFamilyProperties_Count()];
            pd.GetQueueFamilyProperties_Write(families);
            var family = -1;
            for (var i = 0; i < families.Length && family < 0; i++)
                if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) != 0 && pd.GetSurfaceSupportKHR(i, surface))
                    family = i;
            if (family < 0)
                continue;

            var score = props.DeviceType switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 3,
                VkPhysicalDeviceType.IntegratedGpu => 2,
                _ => 1,
            };
            if (score > bestScore)
                (physicalDevice, queueFamily, bestScore) = (pd, family, score);
        }
        if (physicalDevice is null)
            throw new InvalidOperationException("No suitable Vulkan 1.3 GPU with a graphics+present queue");

        var chosenProps = physicalDevice.GetProperties();
        Console.WriteLine($"GPU: {Marshal.PtrToStringUTF8((nint)chosenProps.DeviceName)} ({chosenProps.DeviceType}), " +
                          $"API {chosenProps.ApiVersion >> 22}.{(chosenProps.ApiVersion >> 12) & 0x3FF}.{chosenProps.ApiVersion & 0xFFF}, queue family {queueFamily}");

        // ---- Device ---------------------------------------------------------------------------
        var priority = 1.0f;
        var enable13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
        using var device = physicalDevice.CreateDevice(new Device.CreateInfo
        {
            Extensions = [VK.KHR_SWAPCHAIN_EXTENSION_NAME],
            QueueCreateInfos =
            [
                new VkDeviceQueueCreateInfo { QueueFamilyIndex = queueFamily, QueueCount = 1, QueuePriorities = &priority },
            ],
            Next = &enable13,
        });
        var queue = device.GetQueue(queueFamily, 0);

        // ---- Per-frame objects ----------------------------------------------------------------
        var commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = queueFamily,
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
        }, null);
        var cmds = new CommandBuffer[FramesInFlight];
        device.AllocateCommandBuffers(new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary }, cmds);

        var acquireSems = new VkSemaphore[FramesInFlight];
        var inFlight = new VkFence[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            acquireSems[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            inFlight[i] = device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
        }

        Swapchain? sc = null;
        var recreations = 0;
        var outOfDate = 0;
        var suboptimal = 0;
        var presented = 0;
        try
        {
            sc = Swapchain.Create(device, physicalDevice, surface, window, null)
                 ?? throw new InvalidOperationException("Initial swapchain has zero extent");
            Console.WriteLine($"Swapchain: {sc.Extent.Width}x{sc.Extent.Height} {sc.Format}/{sc.ColorSpace} images={sc.Images.Length} present=FIFO");

            var frame = 0L;
            var needRecreate = false;
            var running = true;
            var resizeStep = 0;
            var sw = Stopwatch.StartNew();

            while (running && presented < targetFrames)
            {
                while (Sdl.Events.PollEvent(out var ev))
                {
                    if (ev.type is SdlEventType.Quit or SdlEventType.WindowCloseRequested)
                        running = false;
                    else if (ev.type is SdlEventType.WindowPixelSizeChanged or SdlEventType.WindowResized)
                        needRecreate = true;
                }
                if (!running)
                    break;

                // Unattended resize test: forces the OUT_OF_DATE / SUBOPTIMAL / recreate path.
                if (resizeTest && resizeStep == 0 && presented >= targetFrames / 3)
                {
                    if (!Sdl.Video.SetWindowSize(window, 1024, 720))
                        throw SdlException.FromError("SDL_SetWindowSize failed");
                    resizeStep = 1;
                }
                else if (resizeTest && resizeStep == 1 && presented >= 2 * targetFrames / 3)
                {
                    if (!Sdl.Video.SetWindowSize(window, 640, 480))
                        throw SdlException.FromError("SDL_SetWindowSize failed");
                    resizeStep = 2;
                }

                if (!Sdl.Video.GetWindowSizeInPixels(window, out var pw, out var ph))
                    throw SdlException.FromError("SDL_GetWindowSizeInPixels failed");
                if (pw == 0 || ph == 0)
                {
                    Thread.Sleep(10); // minimised: don't render, don't spin
                    continue;
                }

                if (needRecreate)
                {
                    device.WaitIdle();
                    var next = Swapchain.Create(device, physicalDevice, surface, window, sc);
                    if (next is null)
                        continue; // zero extent, try again later
                    sc.Destroy(device);
                    sc = next;
                    recreations++;
                    needRecreate = false;
                    Console.WriteLine($"  recreated swapchain at frame {presented}: {sc.Extent.Width}x{sc.Extent.Height}, images={sc.Images.Length}");
                }

                var fi = (int)(frame % FramesInFlight);
                var fence = inFlight[fi];
                Check(device.WaitForFences([fence], true, -1), "vkWaitForFences");

                // Fence is NOT reset until we know we will submit, so an OUT_OF_DATE acquire can't deadlock.
                VkResult acquire;
                int imageIndex;
                try
                {
                    acquire = device.AcquireNextImageKHR(sc.Handle, -1, acquireSems[fi], VkFence.NullHandle, out imageIndex);
                }
                catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
                {
                    outOfDate++;
                    needRecreate = true;
                    continue;
                }
                if (acquire == VkResult.SuboptimalKHR)
                {
                    suboptimal++;
                    needRecreate = true; // still render + present this image, its semaphore will signal
                }
                else if (acquire != VkResult.Success)
                    throw new VkResultError(acquire, $"vkAcquireNextImageKHR returned {acquire}");

                device.ResetFences([fence]);

                var cmd = cmds[fi];
                cmd.Reset(VkCommandBufferResetFlags.None);
                cmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

                var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
                // Undefined -> COLOR_ATTACHMENT. Src stage = COLOR_ATTACHMENT_OUTPUT chains with the acquire
                // semaphore's wait stage, so the transition happens after the presentation engine releases the image.
                var toColor = new VkImageMemoryBarrier2
                {
                    SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    SrcAccessMask = VkAccessFlags2.None,
                    DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.ColorAttachmentOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = sc.Images[imageIndex],
                    SubresourceRange = range,
                };
                cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toColor });

                var t = frame / 60.0f;
                var colour = new float4(
                    0.5f + 0.5f * MathF.Sin(t * 2.0f),
                    0.5f + 0.5f * MathF.Sin(t * 2.0f + 2.094f),
                    0.5f + 0.5f * MathF.Sin(t * 2.0f + 4.188f),
                    1.0f);
                var colorAttachment = new VkRenderingAttachmentInfo
                {
                    ImageView = sc.Views[imageIndex],
                    ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                    LoadOp = VkAttachmentLoadOp.Clear,
                    StoreOp = VkAttachmentStoreOp.Store,
                    ClearValue = new VkClearColorValue { Float32 = colour },
                };
                cmd.BeginRendering(new VkRenderingInfo
                {
                    RenderArea = new VkRect2D(sc.Extent),
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    ColorAttachments = &colorAttachment,
                });
                cmd.EndRendering();

                // COLOR_ATTACHMENT -> PRESENT_SRC. Dst stage COLOR_ATTACHMENT_OUTPUT chains with the render-finished
                // semaphore signal stage below; presentation does its own visibility, so dst access is NONE.
                var toPresent = new VkImageMemoryBarrier2
                {
                    SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
                    DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    DstAccessMask = VkAccessFlags2.None,
                    OldLayout = VkImageLayout.ColorAttachmentOptimal,
                    NewLayout = VkImageLayout.PresentSrcKHR,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = sc.Images[imageIndex],
                    SubresourceRange = range,
                };
                cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toPresent });
                cmd.End();

                // Render-finished semaphores are per swapchain IMAGE (not per frame): a present-wait semaphore
                // can only be reused once that image is re-acquired.
                var renderFinished = sc.RenderFinished[imageIndex];
                var waitInfo = new VkSemaphoreSubmitInfo { Semaphore = acquireSems[fi], StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
                var signalInfo = new VkSemaphoreSubmitInfo { Semaphore = renderFinished, StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
                var cmdInfo = new VkCommandBufferSubmitInfo { CommandBuffer = cmd };
                var submit = new VkSubmitInfo2
                {
                    WaitSemaphoreInfoCount = 1,
                    WaitSemaphoreInfos = &waitInfo,
                    CommandBufferInfoCount = 1,
                    CommandBufferInfos = &cmdInfo,
                    SignalSemaphoreInfoCount = 1,
                    SignalSemaphoreInfos = &signalInfo,
                };
                queue.Submit2(new Span<VkSubmitInfo2>(&submit, 1), fence);

                var swapchainHandle = sc.Handle;
                var present = new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &renderFinished,
                    SwapchainCount = 1,
                    Swapchains = &swapchainHandle,
                    ImageIndices = &imageIndex,
                };
                VkResult presentResult;
                try
                {
                    presentResult = queue.PresentKHR(present);
                }
                catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
                {
                    presentResult = VkResult.ErrorOutOfDateKHR;
                }

                if (presentResult == VkResult.ErrorOutOfDateKHR)
                {
                    outOfDate++;
                    needRecreate = true;
                }
                else if (presentResult == VkResult.SuboptimalKHR)
                {
                    suboptimal++;
                    needRecreate = true;
                    presented++;
                }
                else if (presentResult == VkResult.Success)
                    presented++;
                else
                    throw new VkResultError(presentResult, $"vkQueuePresentKHR returned {presentResult}");

                frame++;
            }

            sw.Stop();
            Console.WriteLine($"Rendered {frame} frames, presented {presented} in {sw.Elapsed.TotalSeconds:F2}s " +
                              $"({presented / sw.Elapsed.TotalSeconds:F1} fps); recreations={recreations} outOfDate={outOfDate} suboptimal={suboptimal}");
        }
        finally
        {
            // ---- Teardown in reverse creation order, after the GPU is idle ----------------------
            device.WaitIdle();
            sc?.Destroy(device);
            for (var i = FramesInFlight - 1; i >= 0; i--)
            {
                device.DestroyFence(inFlight[i], null);
                if (!(s_leakTest && i == 0))
                    device.DestroySemaphore(acquireSems[i], null);
            }
            device.DestroyCommandPool(commandPool, null); // frees the command buffers
            Console.WriteLine("Teardown: device objects destroyed");
        }

        return presented >= targetFrames ? 0 : 4;
    }

    static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success)
            throw new VkResultError(r, $"{what} returned {r}");
    }
}

sealed unsafe class Swapchain
{
    public VkSwapchainKHR Handle;
    public VkImage[] Images = [];
    public VkImageView[] Views = [];
    public VkSemaphore[] RenderFinished = [];
    public VkExtent2D Extent;
    public VkFormat Format;
    public VkColorSpaceKHR ColorSpace;

    /// Returns null when the surface currently has a zero extent (minimised).
    public static Swapchain? Create(Device device, PhysicalDevice pd, VkSurfaceKHR surface, SdlWindow window, Swapchain? old)
    {
        var caps = pd.GetSurfaceCapabilitiesKHR(surface);

        VkExtent2D extent;
        if (caps.CurrentExtent.Width != -1) // 0xFFFFFFFF = surface lets the swapchain choose
            extent = caps.CurrentExtent;
        else
        {
            if (!Sdl.Video.GetWindowSizeInPixels(window, out var w, out var h))
                throw SdlException.FromError("SDL_GetWindowSizeInPixels failed");
            extent = new VkExtent2D(
                Math.Clamp(w, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp(h, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }
        if (extent.Width == 0 || extent.Height == 0)
            return null;

        if ((caps.SupportedUsageFlags & VkImageUsageFlags.ColorAttachmentBit) == 0)
            throw new InvalidOperationException("Surface does not support COLOR_ATTACHMENT usage");

        var formats = new VkSurfaceFormatKHR[pd.GetSurfaceFormatsKHR_Count(surface)];
        var fr = pd.GetSurfaceFormatsKHR_Write(surface, formats);
        if (fr != VkResult.Success && fr != VkResult.Incomplete)
            throw new VkResultError(fr, $"vkGetPhysicalDeviceSurfaceFormatsKHR returned {fr}");
        if (formats.Length == 0)
            throw new InvalidOperationException("Surface reports no formats");
        var chosen = formats[0];
        foreach (var f in formats)
            if (f.Format == VkFormat.B8G8R8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                chosen = f;
                break;
            }

        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);

        var alpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR;
        if ((caps.SupportedCompositeAlpha & alpha) == 0)
            foreach (var a in new[] { VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR, VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR, VkCompositeAlphaFlagsKHR.InheritBitKHR })
                if ((caps.SupportedCompositeAlpha & a) != 0)
                {
                    alpha = a;
                    break;
                }

        var sc = new Swapchain { Extent = extent, Format = chosen.Format, ColorSpace = chosen.ColorSpace };
        sc.Handle = device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
        {
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = alpha,
            PresentMode = VkPresentModeKHR.FifoKHR, // the only mode guaranteed by the spec
            Clipped = true,
            OldSwapchain = old?.Handle ?? VkSwapchainKHR.NullHandle,
        }, null);

        sc.Images = new VkImage[device.GetSwapchainImagesKHR_Count(sc.Handle)];
        var ir = device.GetSwapchainImagesKHR_Write(sc.Handle, sc.Images);
        if (ir != VkResult.Success)
            throw new VkResultError(ir, $"vkGetSwapchainImagesKHR returned {ir}");

        sc.Views = new VkImageView[sc.Images.Length];
        sc.RenderFinished = new VkSemaphore[sc.Images.Length];
        for (var i = 0; i < sc.Images.Length; i++)
        {
            sc.Views[i] = device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = sc.Images[i],
                ViewType = VkImageViewType._2D,
                Format = chosen.Format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            }, null);
            sc.RenderFinished[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
        return sc;
    }

    public void Destroy(Device device)
    {
        for (var i = Images.Length - 1; i >= 0; i--)
        {
            device.DestroySemaphore(RenderFinished[i], null);
            device.DestroyImageView(Views[i], null);
        }
        device.DestroySwapchainKHR(Handle, null);
        Handle = VkSwapchainKHR.NullHandle;
    }
}
