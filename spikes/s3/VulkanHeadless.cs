/*
 * Spike S3: headless Vulkan 1.3 smoke test for CI software drivers (lavapipe / SwiftShader).
 *
 * - Instance with VK_LAYER_KHRONOS_validation (+ synchronization validation) and a
 *   VK_EXT_debug_utils messenger that counts errors and warnings.
 * - Lists every physical device (name, type, driver, API version).
 * - Device with Vulkan 1.3 dynamicRendering + synchronization2.
 * - 64x64 RGBA8 image cleared with dynamic rendering (LoadOp.Clear), copied to a host-visible
 *   buffer with sync2 barriers + vkQueueSubmit2, every pixel verified on the CPU.
 *
 * Exit codes: 0 ok, 1 pixel mismatch, 2 validation errors, 3 setup failure (exception).
 * Env:
 *   S3_REQUIRE_CPU=1       only accept a VK_PHYSICAL_DEVICE_TYPE_CPU device (CI: proves a software driver ran)
 *   S3_INJECT_ERROR=1      record a deliberately wrong image layout so validation MUST report an error
 *                          (negative test: proves the layer is really loaded and the messenger works)
 */

#:property AllowUnsafeBlocks=true
#:package Ahwoo.Brutal.Vulkan@2026.10.3
#:package Ahwoo.Brutal.Vulkan.Abstractions@2026.10.3
#:package Ahwoo.Brutal.Core@2026.10.4
#:package Ahwoo.Brutal.Strings@2026.10.2

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

const int API_VERSION_1_3 = 1 << 22 | 3 << 12;
const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
const int Size = 64;

var requireCpu = Environment.GetEnvironmentVariable("S3_REQUIRE_CPU") == "1";
var injectError = Environment.GetEnvironmentVariable("S3_INJECT_ERROR") == "1";
var total = Stopwatch.StartNew();

Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
foreach (var name in new[] { "VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_LAYER_PATH", "VK_LAYER_PATH", "VK_LOADER_LAYERS_ENABLE" })
    Console.WriteLine($"  env {name}={Environment.GetEnvironmentVariable(name)}");

try
{
    return Run(requireCpu, injectError, total);
}
catch (Exception e)
{
    Console.WriteLine($"FAIL: exception: {e}");
    return 3;
}

static unsafe int Run(bool requireCpu, bool injectError, Stopwatch total)
{
    // ---- Layers available? (failing loudly beats silently running without validation)
    var layerProps = new VkLayerProperties[Vulkan.EnumerateInstanceLayerProperties_Count()];
    Check(Vulkan.EnumerateInstanceLayerProperties_Write(layerProps), "vkEnumerateInstanceLayerProperties");
    var haveValidation = false;
    Console.WriteLine($"Instance layers ({layerProps.Length}):");
    for (var i = 0; i < layerProps.Length; i++)
    {
        fixed (byte* n = layerProps[i].LayerName)
        {
            var name = Utf8.GetString(n);
            Console.WriteLine($"  {name} spec {FormatVersion(layerProps[i].SpecVersion)} impl {layerProps[i].ImplementationVersion}");
            haveValidation |= name == ValidationLayer;
        }
    }
    if (!haveValidation)
    {
        Console.WriteLine($"FAIL: {ValidationLayer} is not available");
        return 3;
    }

    // ---- Instance + debug messenger (also chained into instance creation to catch create/destroy messages)
    var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
    {
        MessageSeverity =
            VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
        MessageType =
            VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
            | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
            | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
        PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Validation.Callback),
    };
    var enables = stackalloc VkValidationFeatureEnableEXT[] { VkValidationFeatureEnableEXT.SynchronizationValidationEXT };
    var validationFeatures = new VkValidationFeaturesEXT
    {
        Next = &messengerInfo,
        EnabledValidationFeatureCount = 1,
        EnabledValidationFeatures = enables,
    };

    using var instance = Vulkan.CreateInstance(
        new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = API_VERSION_1_3 },
            Flags = VkInstanceCreateFlags.None,
            EnabledExtensions = [VK.EXT_DEBUG_UTILS_EXTENSION_NAME, "VK_EXT_validation_features"],
            EnabledLayers = [ValidationLayer],
            Next = &validationFeatures,
        }
    );
    var messenger = instance.CreateDebugUtilsMessengerEXT(messengerInfo, null);
    Console.WriteLine($"Instance created with {ValidationLayer} + sync validation ({total.ElapsedMilliseconds} ms)");

    // ---- Physical devices
    Console.WriteLine($"Physical devices: {instance.PhysicalDevices.Length}");
    PhysicalDevice? chosen = null;
    foreach (var pd in instance.PhysicalDevices)
    {
        var driver = new VkPhysicalDeviceDriverProperties();
        var props2 = new VkPhysicalDeviceProperties2 { Next = &driver };
        pd.GetProperties2(ref props2);
        var p = props2.Properties;
        var cv = driver.ConformanceVersion;
        Console.WriteLine($"  device   {Utf8.GetString(p.DeviceName)}");
        Console.WriteLine($"    type     {p.DeviceType}");
        Console.WriteLine($"    api      {FormatVersion(p.ApiVersion)}");
        Console.WriteLine($"    driverID {driver.DriverID}");
        Console.WriteLine($"    driver   {Utf8.GetString(driver.DriverName)}");
        Console.WriteLine($"    info     {Utf8.GetString(driver.DriverInfo)}");
        Console.WriteLine($"    conform  {cv.Major}.{cv.Minor}.{cv.Subminor}.{cv.Patch}");

        var ok = p.ApiVersion >= API_VERSION_1_3 && (!requireCpu || p.DeviceType == VkPhysicalDeviceType.Cpu);
        if (ok && chosen is null)
            chosen = pd;
    }
    if (chosen is null)
    {
        Console.WriteLine($"FAIL: no Vulkan 1.3 device{(requireCpu ? " of type CPU" : "")} found");
        instance.DestroyDebugUtilsMessengerEXT(messenger, null);
        return 3;
    }
    var chosenProps = chosen.GetProperties();
    Console.WriteLine($"Using: {Utf8.GetString(chosenProps.DeviceName)}");

    // Feature check before enabling.
    var supported13 = new VkPhysicalDeviceVulkan13Features();
    var features2 = new VkPhysicalDeviceFeatures2 { Next = &supported13 };
    chosen.GetFeatures2(ref features2);
    Console.WriteLine($"  dynamicRendering={(bool)supported13.DynamicRendering} synchronization2={(bool)supported13.Synchronization2}");
    if (!supported13.DynamicRendering || !supported13.Synchronization2)
    {
        Console.WriteLine("FAIL: device lacks dynamicRendering or synchronization2");
        instance.DestroyDebugUtilsMessengerEXT(messenger, null);
        return 3;
    }

    var queueFamily = chosen.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit);
    if (queueFamily < 0)
        throw new InvalidOperationException("no graphics queue family");

    var priority = 1.0f;
    var features13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
    int result;
    using (var device = chosen.CreateDevice(
        new Device.CreateInfo
        {
            QueueCreateInfos =
            [
                new VkDeviceQueueCreateInfo
                {
                    QueueFamilyIndex = queueFamily,
                    QueueCount = 1,
                    QueuePriorities = &priority,
                },
            ],
            Next = &features13,
        }
    ))
    {
        result = Render(device, chosen, queueFamily, injectError);
        device.WaitIdle();
    }

    instance.DestroyDebugUtilsMessengerEXT(messenger, null);
    // Instance disposal happens at scope end; messages during vkDestroyInstance go to the chained messenger.
    Console.WriteLine($"Validation: {Validation.Errors} error(s), {Validation.Warnings} warning(s)");
    Console.WriteLine($"Total time {total.ElapsedMilliseconds} ms");

    if (result != 0)
        return result;
    if (Validation.Errors > 0)
    {
        Console.WriteLine("FAIL: validation errors reported");
        return 2;
    }
    Console.WriteLine("PASS");
    return 0;
}

static unsafe int Render(Device device, PhysicalDevice physicalDevice, int queueFamily, bool injectError)
{
    var queue = device.GetQueue(queueFamily, 0);
    const VkFormat format = VkFormat.R8G8B8A8UNorm;
    var colorRange = new VkImageSubresourceRange
    {
        AspectMask = VkImageAspectFlags.ColorBit,
        LevelCount = 1,
        LayerCount = 1,
    };

    // Colour attachment image, device memory.
    var image = device.CreateImage(
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
        null
    );
    var imageMemory = device.AllocateMemory(physicalDevice, image, VkMemoryPropertyFlags.None, null);
    device.BindImageMemory(image, imageMemory, new ByteSize64(0));
    var view = device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = format,
            SubresourceRange = colorRange,
        },
        null
    );

    // Readback buffer, host-visible + coherent.
    const int byteCount = Size * Size * 4;
    var buffer = device.CreateBuffer(
        new VkBufferCreateInfo
        {
            Size = new ByteSize64(byteCount),
            Usage = VkBufferUsageFlags.TransferDstBit,
            SharingMode = VkSharingMode.Exclusive,
        },
        null
    );
    var bufferMemory = device.AllocateMemory(
        physicalDevice,
        buffer,
        VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        null
    );
    device.BindBufferMemory(buffer, bufferMemory, new ByteSize64(0));
    var mapped = (byte*)device.MapMemory(bufferMemory).Pointer;
    new Span<byte>(mapped, byteCount).Fill(0xCD); // poison so a no-op copy can't pass

    var pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = queueFamily }, null);
    var cmd = device.AllocateCommandBuffer(
        new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
    );
    var fence = device.CreateFence(VkFenceCreateFlags.None, null);

    cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

    var toAttachment = new VkImageMemoryBarrier2
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
    cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toAttachment });

    // 0.2/0.4/0.8/1.0 map exactly to 51/102/204/255 in UNORM8.
    var colorAttachment = new VkRenderingAttachmentInfo
    {
        ImageView = view,
        ImageLayout = VkImageLayout.ColorAttachmentOptimal,
        LoadOp = VkAttachmentLoadOp.Clear,
        StoreOp = VkAttachmentStoreOp.Store,
        ClearValue = new VkClearColorValue { Float32 = new float4(0.2f, 0.4f, 0.8f, 1.0f) },
    };
    cmd.BeginRendering(
        new VkRenderingInfo
        {
            RenderArea = new VkRect2D(Size, Size),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
        }
    );
    cmd.EndRendering();

    var toTransfer = new VkImageMemoryBarrier2
    {
        SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
        SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
        DstStageMask = VkPipelineStageFlags2.CopyBit,
        DstAccessMask = VkAccessFlags2.TransferReadBit,
        // Negative test: lie about the old layout -> validation must flag a layout mismatch at submit.
        OldLayout = injectError ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.ColorAttachmentOptimal,
        NewLayout = VkImageLayout.TransferSrcOptimal,
        SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        Image = image,
        SubresourceRange = colorRange,
    };
    cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toTransfer });

    cmd.CopyImageToBuffer(
        image,
        VkImageLayout.TransferSrcOptimal,
        buffer,
        [
            new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D(Size, Size, 1),
            },
        ]
    );

    var toHost = new VkBufferMemoryBarrier2
    {
        SrcStageMask = VkPipelineStageFlags2.CopyBit,
        SrcAccessMask = VkAccessFlags2.TransferWriteBit,
        DstStageMask = VkPipelineStageFlags2.HostBit,
        DstAccessMask = VkAccessFlags2.HostReadBit,
        SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        Buffer = buffer,
        Offset = new ByteSize64(0),
        Size = new ByteSize64(byteCount),
    };
    cmd.PipelineBarrier2(new VkDependencyInfo { BufferMemoryBarrierCount = 1, BufferMemoryBarriers = &toHost });
    cmd.End();

    var cmdInfo = new VkCommandBufferSubmitInfo { CommandBuffer = cmd };
    var sw = Stopwatch.StartNew();
    queue.Submit2([new VkSubmitInfo2 { CommandBufferInfoCount = 1, CommandBufferInfos = &cmdInfo }], fence);
    var wait = device.WaitForFence(fence, unchecked((nint)10_000_000_000)); // 10 s (64-bit nint)
    Console.WriteLine($"vkWaitForFences -> {wait} ({sw.Elapsed.TotalMilliseconds:F1} ms)");
    var rc = 0;
    if (wait != VkResult.Success)
    {
        Console.WriteLine("FAIL: fence wait did not return VK_SUCCESS");
        rc = 3;
    }
    else
    {
        ReadOnlySpan<byte> expected = [51, 102, 204, 255];
        var mismatches = 0;
        var pixels = new ReadOnlySpan<byte>(mapped, byteCount);
        for (var i = 0; i < Size * Size; i++)
        {
            var px = pixels.Slice(i * 4, 4);
            if (!px.SequenceEqual(expected))
            {
                if (mismatches < 5)
                    Console.WriteLine($"  mismatch at ({i % Size},{i / Size}): {px[0]},{px[1]},{px[2]},{px[3]}");
                mismatches++;
            }
        }
        Console.WriteLine(
            $"Readback: pixel(0,0)={pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]} "
                + $"pixel(63,63)={pixels[byteCount - 4]},{pixels[byteCount - 3]},{pixels[byteCount - 2]},{pixels[byteCount - 1]}; "
                + $"{mismatches}/{Size * Size} mismatched"
        );
        if (mismatches > 0)
        {
            Console.WriteLine("FAIL: pixel mismatch");
            rc = 1;
        }
    }

    device.WaitIdle();
    device.UnmapMemory(bufferMemory);
    device.DestroyFence(fence, null);
    device.DestroyCommandPool(pool, null);
    device.DestroyBuffer(buffer, null);
    device.FreeMemory(bufferMemory, null);
    device.DestroyImageView(view, null);
    device.DestroyImage(image, null);
    device.FreeMemory(imageMemory, null);
    return rc;
}

static void Check(VkResult r, string what)
{
    if (r != VkResult.Success)
        throw new InvalidOperationException($"{what} returned {r}");
}

static string FormatVersion(int v) => $"{(v >> 22) & 0x7F}.{(v >> 12) & 0x3FF}.{v & 0xFFF}";

static class Validation
{
    public static int Errors;
    public static int Warnings;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe Bool32 Callback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* user
    )
    {
        var isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
        if (isError)
            Interlocked.Increment(ref Errors);
        else
            Interlocked.Increment(ref Warnings);
        var msg = data->Message == null ? "" : Utf8.GetString(data->Message);
        Console.WriteLine($"[validation {(isError ? "ERROR" : "WARN")} {type}] {msg}");
        return false;
    }
}
