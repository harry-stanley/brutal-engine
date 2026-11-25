/*
 * Spike S1/S3 (macOS): offscreen Vulkan 1.3 smoke test through BRUTAL's bindings.
 *
 * Instance with portability enumeration (+ debug utils messenger counting errors), prints the
 * physical devices, creates a device with VK_KHR_portability_subset (if exposed) and Vulkan 1.3
 * dynamicRendering + synchronization2, clears a 64x64 RGBA8 image with dynamic rendering, copies it
 * to a host-visible buffer and checks every pixel. Exits nonzero on any failure.
 *
 * Usage: dotnet VkSmoke.dll [--require-validation] [--set-driver-env]
 *   --require-validation  enable VK_LAYER_KHRONOS_validation, fail if it is missing, and run a canary
 *                         (a deliberately invalid vkCreateBuffer) to prove errors reach the messenger.
 *   --set-driver-env      setenv(VK_DRIVER_FILES=<app dir>/MoltenVK_icd.json) natively (libc) before the
 *                         loader is touched, i.e. what an engine would do instead of a launcher script.
 *                         (.NET's Environment.SetEnvironmentVariable does not call setenv on Unix.)
 */

#:property AllowUnsafeBlocks=true
#:property PublishAot=false
#:package Ahwoo.Brutal.Vulkan@2026.10.3
#:package Ahwoo.Brutal.Vulkan.Abstractions@2026.10.3
#:package Ahwoo.Brutal.Core@2026.10.4
#:package Ahwoo.Brutal.Strings@2026.10.2

using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

var requireValidation = args.Contains("--require-validation");
var setDriverEnv = args.Contains("--set-driver-env");
var sw = Stopwatch.StartNew();

try
{
    return Smoke.Run(requireValidation, setDriverEnv, sw);
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: unhandled {e.GetType().Name}: {e.Message}");
    Console.Error.WriteLine(e);
    return 2;
}

static class Smoke
{
    const int API_VERSION_1_3 = 1 << 22 | 3 << 12;
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
    const int W = 64, H = 64;

    public static int Errors;
    public static int Warnings;

    static string Ver(int v) => $"{(v >> 22) & 0x7F}.{(v >> 12) & 0x3FF}.{v & 0xFFF}";

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static unsafe Bool32 OnDebug(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* user
    )
    {
        var msg = data == null || data->Message == null ? "" : Marshal.PtrToStringUTF8((nint)data->Message);
        if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
        {
            Errors++;
            Console.WriteLine($"[vk ERROR] {msg}");
        }
        else if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) != 0)
        {
            Warnings++;
            Console.WriteLine($"[vk WARN] {msg}");
        }
        else
            Console.WriteLine($"[vk info] {msg}");
        return false;
    }

    // Where did a native symbol come from? (macOS/Linux only; proves which loader/driver binary is used.)
    [StructLayout(LayoutKind.Sequential)]
    struct DlInfo { public nint fname, fbase, sname, saddr; }

    [DllImport("libSystem.B.dylib", EntryPoint = "dladdr")]
    static extern int dladdr_mac(nint addr, out DlInfo info);

    [DllImport("libc", EntryPoint = "dladdr")]
    static extern int dladdr_linux(nint addr, out DlInfo info);

    [DllImport("libc", EntryPoint = "setenv")]
    static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    static string WhereIs(nint addr)
    {
        if (OperatingSystem.IsWindows() || addr == 0)
            return "(n/a)";
        try
        {
            var ok = OperatingSystem.IsMacOS() ? dladdr_mac(addr, out var i) : dladdr_linux(addr, out i);
            return ok != 0 && i.fname != 0 ? Marshal.PtrToStringUTF8(i.fname)! : "(dladdr failed)";
        }
        catch (Exception e)
        {
            return $"(dladdr threw {e.GetType().Name})";
        }
    }

    static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success)
            throw new Exception($"{what} returned {r}");
    }

    public static unsafe int Run(bool requireValidation, bool setDriverEnv, Stopwatch sw)
    {
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}; .NET {Environment.Version}");
        Console.WriteLine($"App dir: {AppContext.BaseDirectory}");
        Console.WriteLine($"CWD: {Environment.CurrentDirectory}");
        Console.WriteLine($"NATIVE_DLL_SEARCH_DIRECTORIES: {AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")}");
        foreach (var v in new[] { "VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_DRIVER_FILES", "VK_ADD_LAYER_PATH", "VK_LAYER_PATH", "VK_INSTANCE_LAYERS", "DYLD_LIBRARY_PATH" })
            Console.WriteLine($"  env {v}={Environment.GetEnvironmentVariable(v)}");

        if (setDriverEnv)
        {
            if (OperatingSystem.IsWindows())
                throw new Exception("--set-driver-env is for macOS/Linux");
            var icd = Path.Combine(AppContext.BaseDirectory, "MoltenVK_icd.json");
            if (setenv("VK_DRIVER_FILES", icd, 1) != 0)
                throw new Exception("setenv failed");
            Console.WriteLine($"  native setenv VK_DRIVER_FILES={icd}");
        }

        // Forces ImportResolver to load the loader.
        var gipa = Vulkan.GetInstanceProcAddr;
        Console.WriteLine($"Vulkan loader loaded from: {WhereIs((nint)gipa.Invoke)}");

        int loaderVersion;
        Vulkan.EnumerateInstanceVersion(&loaderVersion);
        Console.WriteLine($"Loader instance version: {Ver(loaderVersion)}");

        // Instance extensions and layers
        var extProps = new VkExtensionProperties[Vulkan.EnumerateInstanceExtensionProperties_Count(default)];
        Check(Vulkan.EnumerateInstanceExtensionProperties_Write(default, extProps), "vkEnumerateInstanceExtensionProperties");
        var instExts = new HashSet<string>();
        foreach (ref var e in extProps.AsSpan())
            fixed (byte* p = e.ExtensionName)
                instExts.Add(Marshal.PtrToStringUTF8((nint)p)!);
        Console.WriteLine($"Instance extensions ({instExts.Count}): {string.Join(", ", instExts.Order())}");

        var layerProps = new VkLayerProperties[Vulkan.EnumerateInstanceLayerProperties_Count()];
        Check(Vulkan.EnumerateInstanceLayerProperties_Write(layerProps), "vkEnumerateInstanceLayerProperties");
        var layers = new HashSet<string>();
        foreach (ref var l in layerProps.AsSpan())
            fixed (byte* p = l.LayerName)
            {
                var name = Marshal.PtrToStringUTF8((nint)p)!;
                layers.Add(name);
                Console.WriteLine($"Layer: {name} spec {Ver(l.SpecVersion)} impl {l.ImplementationVersion}");
            }

        var enabledExts = new HashSet<string>();
        var enabledLayers = new HashSet<string>();
        var flags = VkInstanceCreateFlags.None;
        if (instExts.Contains(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME))
        {
            enabledExts.Add(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            flags |= VkInstanceCreateFlags.EnumeratePortabilityBitKHR;
        }
        else if (OperatingSystem.IsMacOS())
            throw new Exception("VK_KHR_portability_enumeration missing on macOS");

        var haveDebugUtils = instExts.Contains(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
        if (haveDebugUtils)
            enabledExts.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
        else
            Console.WriteLine("WARNING: VK_EXT_debug_utils not available; error counting disabled");

        if (requireValidation)
        {
            if (!layers.Contains(ValidationLayer))
                throw new Exception($"{ValidationLayer} required but not found");
            enabledLayers.Add(ValidationLayer);
        }
        Console.WriteLine($"Enabling instance extensions: {string.Join(", ", enabledExts)}; layers: {string.Join(", ", enabledLayers)}");

        var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity =
                VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT,
            MessageType =
                VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&OnDebug),
        };

        var appName = "BrutalEngine.VkSmoke\0"u8;
        fixed (byte* pAppName = appName)
        {
            using var instance = Vulkan.CreateInstance(
                new Instance.CreateInfo
                {
                    AppInfo = new VkApplicationInfo { ApplicationName = pAppName, ApiVersion = API_VERSION_1_3 },
                    Flags = flags,
                    EnabledExtensions = enabledExts,
                    EnabledLayers = enabledLayers,
                    // Also catches messages from vkCreateInstance/vkDestroyInstance
                    Next = haveDebugUtils ? &messengerInfo : null,
                }
            );
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] Instance created");

            var messenger = haveDebugUtils ? instance.CreateDebugUtilsMessengerEXT(messengerInfo, null) : default;
            try
            {
                var rc = RunDevice(instance, requireValidation, sw);
                if (rc != 0)
                    return rc;
            }
            finally
            {
                if (haveDebugUtils)
                    instance.DestroyDebugUtilsMessengerEXT(messenger, null);
            }
        }

        Console.WriteLine($"Debug messenger: {Errors} error(s), {Warnings} warning(s)");
        if (Errors > 0)
        {
            Console.WriteLine("FAIL: Vulkan reported errors");
            return 3;
        }
        Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] PASS");
        return 0;
    }

    static unsafe int RunDevice(Instance instance, bool requireValidation, Stopwatch sw)
    {
        Console.WriteLine($"Physical devices: {instance.PhysicalDevices.Length}");
        if (instance.PhysicalDevices.Length == 0)
        {
            Console.WriteLine("FAIL: no physical devices");
            return 4;
        }

        PhysicalDevice? chosen = null;
        foreach (var pd in instance.PhysicalDevices)
        {
            var driver = new VkPhysicalDeviceDriverProperties();
            var props2 = new VkPhysicalDeviceProperties2 { Next = &driver };
            pd.GetProperties2(ref props2);
            var p = props2.Properties;
            Console.WriteLine($"  Device: {Marshal.PtrToStringUTF8((nint)p.DeviceName)}");
            Console.WriteLine($"    type {p.DeviceType}, apiVersion {Ver(p.ApiVersion)}, driverVersion 0x{p.DriverVersion:X} ({Ver(p.DriverVersion)}), vendor 0x{p.VendorID:X4} device 0x{p.DeviceID:X4}");
            Console.WriteLine($"    driver {driver.DriverID}: '{Marshal.PtrToStringUTF8((nint)driver.DriverName)}' info '{Marshal.PtrToStringUTF8((nint)driver.DriverInfo)}' conformance {driver.ConformanceVersion.Major}.{driver.ConformanceVersion.Minor}.{driver.ConformanceVersion.Subminor}.{driver.ConformanceVersion.Patch}");
            if (chosen == null && p.ApiVersion >= API_VERSION_1_3)
                chosen = pd;
        }
        if (chosen == null)
        {
            Console.WriteLine("FAIL: no Vulkan 1.3 device");
            return 5;
        }

        var f13 = new VkPhysicalDeviceVulkan13Features();
        var f2 = new VkPhysicalDeviceFeatures2 { Next = &f13 };
        chosen.GetFeatures2(ref f2);
        Console.WriteLine($"  Vulkan13Features: dynamicRendering={(bool)f13.DynamicRendering} synchronization2={(bool)f13.Synchronization2}");
        if (!f13.DynamicRendering || !f13.Synchronization2)
        {
            Console.WriteLine("FAIL: dynamicRendering/synchronization2 not supported");
            return 6;
        }

        var deviceExts = new HashSet<string>();
        if (chosen.SupportsExtension(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME))
            deviceExts.Add(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME);
        Console.WriteLine($"  Device extensions enabled: {string.Join(", ", deviceExts)} (device exposes {chosen.ExtensionNames.Count})");

        var family = chosen.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit);
        var enable13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
        var priority = 1.0f;
        using var device = chosen.CreateDevice(
            new Device.CreateInfo
            {
                Extensions = deviceExts,
                QueueCreateInfos =
                [
                    new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = 1, QueuePriorities = &priority },
                ],
                Next = &enable13,
            }
        );
        Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] Device created (queue family {family})");
        Console.WriteLine($"  vkCmdBeginRendering resolves into: {WhereIs((nint)device.PInvoke.vkCmdBeginRendering.Invoke)}");
        var queue = device.GetQueue(family, 0);

        if (requireValidation)
        {
            // Canary: usage 0 violates VUID-VkBufferCreateInfo-usage-requiredbitmask. Must be reported by the layer.
            var before = Errors;
            var canary = device.CreateBuffer(new VkBufferCreateInfo { Size = new ByteSize64(16), SharingMode = VkSharingMode.Exclusive }, null);
            device.DestroyBuffer(canary, null);
            var caught = Errors - before;
            Console.WriteLine($"Validation canary: {caught} error(s) reported for a deliberately invalid vkCreateBuffer");
            if (caught == 0)
            {
                Console.WriteLine("FAIL: validation layer enabled but canary error was not reported");
                return 8;
            }
            Errors = before; // expected errors do not count against the run
        }

        // Colour target
        var image = device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                Extent = new VkExtent3D { Width = W, Height = H, Depth = 1 },
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
        var imageReq = device.GetImageMemoryRequirements(image);
        var imageMem = device.AllocateMemory(
            new VkMemoryAllocateInfo
            {
                AllocationSize = imageReq.Size,
                MemoryTypeIndex = chosen.FindMemoryType(imageReq.MemoryTypeBits, VkMemoryPropertyFlags.DeviceLocalBit),
            },
            null
        );
        device.BindImageMemory(image, imageMem, new ByteSize64(0));

        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };
        var view = device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                SubresourceRange = range,
            },
            null
        );

        // Readback buffer
        const int bytes = W * H * 4;
        var buffer = device.CreateBuffer(
            new VkBufferCreateInfo
            {
                Size = new ByteSize64(bytes),
                Usage = VkBufferUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
            },
            null
        );
        var bufReq = device.GetBufferMemoryRequirements(buffer);
        var bufMem = device.AllocateMemory(
            new VkMemoryAllocateInfo
            {
                AllocationSize = bufReq.Size,
                MemoryTypeIndex = chosen.FindMemoryType(
                    bufReq.MemoryTypeBits,
                    VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit
                ),
            },
            null
        );
        device.BindBufferMemory(buffer, bufMem, new ByteSize64(0));

        var pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
        var cmd = device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
        );
        var fence = device.CreateFence(new VkFenceCreateInfo(), null);

        cmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        var toColor = new VkImageMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.NoneKHR,
            SrcAccessMask = VkAccessFlags2.NoneKHR,
            DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = range,
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toColor });

        VkClearColorValue clearColor = default;
        clearColor.Float32 = new float4(1.0f, 0.5f, 0.25f, 1.0f); // -> 255, 128, 64, 255 (UNORM rounding)
        var attachment = new VkRenderingAttachmentInfo
        {
            ImageView = view,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = clearColor,
        };
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = W, Height = H } },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &attachment,
            }
        );
        cmd.EndRendering();

        var toTransfer = new VkImageMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = VkPipelineStageFlags2.CopyBit,
            DstAccessMask = VkAccessFlags2.TransferReadBit,
            OldLayout = VkImageLayout.ColorAttachmentOptimal,
            NewLayout = VkImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = range,
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
                    ImageExtent = new VkExtent3D { Width = W, Height = H, Depth = 1 },
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
            Size = new ByteSize64(bytes),
        };
        cmd.PipelineBarrier2(new VkDependencyInfo { BufferMemoryBarrierCount = 1, BufferMemoryBarriers = &toHost });
        cmd.End();

        var cbInfo = new VkCommandBufferSubmitInfo { CommandBuffer = cmd };
        queue.Submit2([new VkSubmitInfo2 { CommandBufferInfoCount = 1, CommandBufferInfos = &cbInfo }], fence);
        Check(device.WaitForFences([fence], true, unchecked((nint)5_000_000_000)), "vkWaitForFences");
        Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] GPU work complete");

        void* mapped = null;
        Check(
            device.PInvoke.vkMapMemory.Invoke(device.Handle, bufMem, new ByteSize64(0), new ByteSize64(bytes), default, &mapped),
            "vkMapMemory"
        );
        var px = new ReadOnlySpan<byte>(mapped, bytes);
        int bad = 0;
        for (var i = 0; i < W * H; i++)
        {
            var r = px[i * 4];
            var g = px[i * 4 + 1];
            var b = px[i * 4 + 2];
            var a = px[i * 4 + 3];
            // 0.5*255 = 127.5 and 0.25*255 = 63.75: allow +-1 for implementation rounding
            if (r != 255 || Math.Abs(g - 128) > 1 || Math.Abs(b - 64) > 1 || a != 255)
            {
                if (bad < 5)
                    Console.WriteLine($"  bad pixel {i % W},{i / W}: {r},{g},{b},{a}");
                bad++;
            }
        }
        Console.WriteLine($"Readback: pixel[0] = {px[0]},{px[1]},{px[2]},{px[3]}; {W * H - bad}/{W * H} pixels match");
        device.UnmapMemory(bufMem);

        device.WaitIdle();
        device.DestroyFence(fence, null);
        device.DestroyCommandPool(pool, null);
        device.DestroyBuffer(buffer, null);
        device.FreeMemory(bufMem, null);
        device.DestroyImageView(view, null);
        device.DestroyImage(image, null);
        device.FreeMemory(imageMem, null);

        if (bad != 0)
        {
            Console.WriteLine("FAIL: pixel mismatch");
            return 7;
        }
        return 0;
    }
}
