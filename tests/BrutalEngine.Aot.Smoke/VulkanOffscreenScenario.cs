using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Headless Vulkan 1.3 check: clear a 64x64 RGBA8 image with dynamic rendering, copy it to a host-visible
/// buffer with synchronization2 barriers, and verify every pixel on the CPU, with the Khronos validation layer
/// (including synchronization validation) enabled throughout.
/// </summary>
/// <remarks>
/// Proven on GitHub runners by spikes S1 and S3 (docs/spikes/README.md). Fails rather than silently running without
/// validation when the layer is missing. Portability drivers such as MoltenVK are opted in to when the loader
/// supports portability enumeration, and get <c>VK_KHR_portability_subset</c> enabled on the device.
/// </remarks>
internal static unsafe class VulkanOffscreenScenario
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
    private const int Size = 64;
    private const int ByteCount = Size * Size * 4;

    // 0.2 / 0.4 / 0.8 / 1.0 map exactly to 51 / 102 / 204 / 255 in UNORM8.
    private static readonly float4 _clearColor = new(0.2f, 0.4f, 0.8f, 1.0f);
    private static ReadOnlySpan<byte> ExpectedPixel => [51, 102, 204, 255];

    /// <summary>Runs the scenario.</summary>
    /// <param name="options">The command-line options.</param>
    /// <returns>The outcome.</returns>
    public static ExitCode Run(SmokeOptions options)
    {
        var total = Stopwatch.StartNew();

        // Must happen before the first use of BRUTAL's Vulkan class, which resolves the loader once.
        if (options.VulkanLoader is not null)
        {
            nint preloaded = VulkanLoader.Preload(options.VulkanLoader);
            if (!VulkanLoader.IsUsedByBrutal(preloaded))
            {
                Console.WriteLine($"BRUTAL resolved a different Vulkan loader than {options.VulkanLoader}.");
                return ExitCode.SetupFailure;
            }

            Console.WriteLine($"Vulkan loader: {options.VulkanLoader} (preloaded, used by BRUTAL)");
        }

        int loaderVersion;
        Vulkan.EnumerateInstanceVersion(&loaderVersion);
        Console.WriteLine($"Vulkan loader version {FormatVersion(loaderVersion)}");

        if (!HasValidationLayer())
        {
            Console.WriteLine($"{ValidationLayer} is not available. Install the Vulkan validation layers.");
            return ExitCode.SetupFailure;
        }

        VkDebugUtilsMessengerCreateInfoEXT messengerInfo = ValidationMessenger.CreateInfo();
        VkValidationFeatureEnableEXT* enables = stackalloc VkValidationFeatureEnableEXT[] { VkValidationFeatureEnableEXT.SynchronizationValidationEXT };

        // The messenger is also chained into instance creation, so messages from vkCreateInstance and
        // vkDestroyInstance are reported too.
        VkValidationFeaturesEXT validationFeatures = new()
        {
            Next = &messengerInfo,
            EnabledValidationFeatureCount = 1,
            EnabledValidationFeatures = enables,
        };

        HashSet<string> extensions = [VK.EXT_DEBUG_UTILS_EXTENSION_NAME, "VK_EXT_validation_features"];
        VkInstanceCreateFlags flags = VkInstanceCreateFlags.None;

        // MoltenVK is a portability driver: the loader lists it only if the instance opts in. Opting in changes
        // nothing where no portability driver is installed (lavapipe, desktop drivers).
        if (InstanceExtensions().Contains(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME))
        {
            extensions.Add(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            flags |= VkInstanceCreateFlags.EnumeratePortabilityBitKHR;
        }

        Console.WriteLine($"Instance extensions: {string.Join(", ", extensions)}");
        using Instance instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
            Flags = flags,
            EnabledExtensions = extensions,
            EnabledLayers = [ValidationLayer],
            Next = &validationFeatures,
        });

        VkDebugUtilsMessengerEXT messenger = instance.CreateDebugUtilsMessengerEXT(messengerInfo, null);
        ExitCode result;
        try
        {
            result = RunOnDevice(instance, options);
        }
        finally
        {
            instance.DestroyDebugUtilsMessengerEXT(messenger, null);
        }

        Console.WriteLine($"Validation: {ValidationMessenger.Errors} error(s), {ValidationMessenger.Warnings} warning(s). Total {total.ElapsedMilliseconds} ms.");
        if (result != ExitCode.Pass)
        {
            return result;
        }

        return ValidationMessenger.Errors + ValidationMessenger.Warnings > 0 ? ExitCode.ValidationMessages : ExitCode.Pass;
    }

    private static bool HasValidationLayer()
    {
        var layers = new VkLayerProperties[Vulkan.EnumerateInstanceLayerProperties_Count()];
        VkResult result = Vulkan.EnumerateInstanceLayerProperties_Write(layers);
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"vkEnumerateInstanceLayerProperties returned {result}.");
        }

        for (int i = 0; i < layers.Length; i++)
        {
            fixed (byte* name = layers[i].LayerName)
            {
                if (ReadName(name) == ValidationLayer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> InstanceExtensions()
    {
        // No layer name: the extensions of the loader, the drivers and the implicit layers.
        var properties = new VkExtensionProperties[Vulkan.EnumerateInstanceExtensionProperties_Count(default)];
        VkResult result = Vulkan.EnumerateInstanceExtensionProperties_Write(default, properties);
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"vkEnumerateInstanceExtensionProperties returned {result}.");
        }

        HashSet<string> names = [];
        for (int i = 0; i < properties.Length; i++)
        {
            fixed (byte* name = properties[i].ExtensionName)
            {
                _ = names.Add(ReadName(name));
            }
        }

        return names;
    }

    private static ExitCode RunOnDevice(Instance instance, SmokeOptions options)
    {
        PhysicalDevice? chosen = null;
        foreach (PhysicalDevice candidate in instance.PhysicalDevices)
        {
            VkPhysicalDeviceDriverProperties driver = new();
            VkPhysicalDeviceProperties2 properties2 = new() { Next = &driver };
            candidate.GetProperties2(ref properties2);
            VkPhysicalDeviceProperties properties = properties2.Properties;

            Console.WriteLine(
                $"Device: {ReadName(properties.DeviceName)} ({properties.DeviceType}), API {FormatVersion(properties.ApiVersion)}, "
                + $"driver {ReadName(driver.DriverName)} ({driver.DriverID}) {ReadName(driver.DriverInfo)}");

            bool suitable = properties.ApiVersion >= VK.API_VERSION_1_3
                && (!options.RequireCpu || properties.DeviceType == VkPhysicalDeviceType.Cpu)
                && (options.RequireDriver is not { } required || driver.DriverID == required);
            chosen ??= suitable ? candidate : null;
        }

        if (chosen is null)
        {
            Console.WriteLine(
                $"No Vulkan 1.3 device{(options.RequireCpu ? " of type CPU" : string.Empty)}"
                + $"{(options.RequireDriver is { } id ? $" with driver {id}" : string.Empty)} found.");
            return ExitCode.SetupFailure;
        }

        VkPhysicalDeviceVulkan13Features supported = new();
        VkPhysicalDeviceFeatures2 features2 = new() { Next = &supported };
        chosen.GetFeatures2(ref features2);
        if (!supported.DynamicRendering || !supported.Synchronization2)
        {
            Console.WriteLine("The device lacks dynamicRendering or synchronization2.");
            return ExitCode.SetupFailure;
        }

        int queueFamily = chosen.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit);
        if (queueFamily < 0)
        {
            Console.WriteLine("The device has no graphics queue family.");
            return ExitCode.SetupFailure;
        }

        // A portability driver that exposes VK_KHR_portability_subset requires it to be enabled
        // (VUID-VkDeviceCreateInfo-pProperties-04451).
        HashSet<string> deviceExtensions = [];
        if (chosen.SupportsExtension(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME))
        {
            _ = deviceExtensions.Add(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME);
        }

        Console.WriteLine($"Device extensions: {(deviceExtensions.Count == 0 ? "(none)" : string.Join(", ", deviceExtensions))}");

        float priority = 1.0f;
        VkPhysicalDeviceVulkan13Features enabled = new() { DynamicRendering = true, Synchronization2 = true };
        using Device device = chosen.CreateDevice(new Device.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos =
            [
                new VkDeviceQueueCreateInfo { QueueFamilyIndex = queueFamily, QueueCount = 1, QueuePriorities = &priority },
            ],
            Next = &enabled,
        });

        try
        {
            return ClearAndReadBack(device, chosen, queueFamily, options.InjectValidationError);
        }
        finally
        {
            device.WaitIdle();
        }
    }

    private static ExitCode ClearAndReadBack(Device device, PhysicalDevice physicalDevice, int queueFamily, bool injectValidationError)
    {
        const VkFormat format = VkFormat.R8G8B8A8UNorm;
        VkImageSubresourceRange colorRange = new() { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

        VkImage image = default;
        VkDeviceMemory imageMemory = default;
        VkImageView view = default;
        VkBuffer buffer = default;
        VkDeviceMemory bufferMemory = default;
        bool mapped = false;
        VkCommandPool pool = default;
        VkFence fence = default;
        try
        {
            image = device.CreateImage(
                new VkImageCreateInfo
                {
                    ImageType = VkImageType._2D,
                    Format = format,
                    Extent = new VkExtent3D(Size, Size, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = VkSampleCountFlags._1Bit,
                    Tiling = VkImageTiling.Optimal,
                    Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
                    SharingMode = VkSharingMode.Exclusive,
                    InitialLayout = VkImageLayout.Undefined,
                },
                null);
            imageMemory = device.AllocateMemory(physicalDevice, image, VkMemoryPropertyFlags.None, null);
            device.BindImageMemory(image, imageMemory, new ByteSize64(0));
            view = device.CreateImageView(
                new VkImageViewCreateInfo { Image = image, ViewType = VkImageViewType._2D, Format = format, SubresourceRange = colorRange },
                null);

            buffer = device.CreateBuffer(
                new VkBufferCreateInfo { Size = new ByteSize64(ByteCount), Usage = VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive },
                null);
            bufferMemory = device.AllocateMemory(
                physicalDevice,
                buffer,
                VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                null);
            device.BindBufferMemory(buffer, bufferMemory, new ByteSize64(0));
            byte* pixels = (byte*)device.MapMemory(bufferMemory).Pointer;
            mapped = true;

            // Poison the buffer so a copy that silently does nothing can't pass.
            new Span<byte>(pixels, ByteCount).Fill(0xCD);

            pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = queueFamily }, null);
            CommandBuffer commands = device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
            fence = device.CreateFence(VkFenceCreateFlags.None, null);

            Record(commands, image, view, buffer, colorRange, injectValidationError);

            VkCommandBufferSubmitInfo commandInfo = new() { CommandBuffer = commands };
            device.GetQueue(queueFamily, 0).Submit2(
                [new VkSubmitInfo2 { CommandBufferInfoCount = 1, CommandBufferInfos = &commandInfo }],
                fence);

            // BRUTAL binds the uint64 timeout as nint (docs/spikes/README.md, S1). 10 s fits on 64-bit.
            VkResult wait = device.WaitForFence(fence, unchecked((nint)10_000_000_000));
            if (wait != VkResult.Success)
            {
                Console.WriteLine($"vkWaitForFences returned {wait}.");
                return ExitCode.SetupFailure;
            }

            return VerifyPixels(new ReadOnlySpan<byte>(pixels, ByteCount));
        }
        finally
        {
            // Reverse creation order. Each handle is destroyed only if it was created.
            if (!fence.IsNull())
            {
                device.DestroyFence(fence, null);
            }

            if (!pool.IsNull())
            {
                device.DestroyCommandPool(pool, null);
            }

            if (mapped)
            {
                device.UnmapMemory(bufferMemory);
            }

            if (!buffer.IsNull())
            {
                device.DestroyBuffer(buffer, null);
            }

            if (!bufferMemory.IsNull())
            {
                device.FreeMemory(bufferMemory, null);
            }

            if (!view.IsNull())
            {
                device.DestroyImageView(view, null);
            }

            if (!image.IsNull())
            {
                device.DestroyImage(image, null);
            }

            if (!imageMemory.IsNull())
            {
                device.FreeMemory(imageMemory, null);
            }
        }
    }

    private static void Record(
        CommandBuffer commands,
        VkImage image,
        VkImageView view,
        VkBuffer buffer,
        VkImageSubresourceRange colorRange,
        bool injectValidationError)
    {
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        VkImageMemoryBarrier2 toAttachment = new()
        {
            SrcStageMask = VkPipelineStageFlags2.None,
            SrcAccessMask = VkAccessFlags2.None,
            DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = colorRange,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toAttachment });

        VkRenderingAttachmentInfo colorAttachment = new()
        {
            ImageView = view,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = _clearColor },
        };
        commands.BeginRendering(new VkRenderingInfo
        {
            RenderArea = new VkRect2D(Size, Size),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
        });
        commands.EndRendering();

        VkImageMemoryBarrier2 toTransfer = new()
        {
            SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = VkPipelineStageFlags2.CopyBit,
            DstAccessMask = VkAccessFlags2.TransferReadBit,

            // Negative test: claim the wrong old layout, which the validation layer must report at submit.
            OldLayout = injectValidationError ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.ColorAttachmentOptimal,
            NewLayout = VkImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = colorRange,
        };
        commands.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toTransfer });

        commands.CopyImageToBuffer(
            image,
            VkImageLayout.TransferSrcOptimal,
            buffer,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(Size, Size, 1),
                },
            ]);

        VkBufferMemoryBarrier2 toHost = new()
        {
            SrcStageMask = VkPipelineStageFlags2.CopyBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.HostBit,
            DstAccessMask = VkAccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Buffer = buffer,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(ByteCount),
        };
        commands.PipelineBarrier2(new VkDependencyInfo { BufferMemoryBarrierCount = 1, BufferMemoryBarriers = &toHost });
        commands.End();
    }

    private static ExitCode VerifyPixels(ReadOnlySpan<byte> pixels)
    {
        int mismatches = 0;
        for (int i = 0; i < Size * Size; i++)
        {
            ReadOnlySpan<byte> pixel = pixels.Slice(i * 4, 4);
            if (!pixel.SequenceEqual(ExpectedPixel))
            {
                if (mismatches < 5)
                {
                    Console.WriteLine($"Pixel ({i % Size},{i / Size}) is {pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}.");
                }

                mismatches++;
            }
        }

        Console.WriteLine($"Read back {Size * Size} pixels, {mismatches} wrong.");
        return mismatches == 0 ? ExitCode.Pass : ExitCode.PixelMismatch;
    }

    // Vulkan name fields are NUL-terminated fixed-size UTF-8 arrays (the spec guarantees the terminator).
    private static string ReadName(byte* name) => Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;

    private static string FormatVersion(int version) => $"{(version >> 22) & 0x7F}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
}
