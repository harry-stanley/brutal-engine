using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using BrutalEngine.Graphics.Vulkan;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Creates the engine's Vulkan backend, which settles the Vulkan loader for the process, and checks its result and its
/// one logged decision, then runs the VMA step on every Vulkan 1.3 device (docs/design/graphics-vulkan.md section 14).
/// Run under the JIT and as a NativeAOT executable, it proves <c>VulkanBackend.Create</c>, the loader setup behind it,
/// and VMA's native library in both.
/// </summary>
/// <remarks>
/// <para>
/// Takes no options except <c>--vulkan-loader &lt;path&gt;</c>, which becomes <c>VulkanBackendOptions.LoaderPath</c>.
/// Needs a loader and a Vulkan 1.3 driver, but no validation layer.
/// </para>
/// <para>
/// The VMA step: BRUTAL's <c>[LibraryImport("vma")]</c> has no resolver, so .NET's default probing finds the native,
/// in <c>runtimes/&lt;rid&gt;/native</c> under the JIT and next to the executable under NativeAOT. The step creates an
/// allocator, a small persistently mapped <c>HostUpload</c> buffer, writes and reads it through the mapping, and
/// destroys both, logging the memory type VMA chose. The backend's allocator factory and placement table are internal
/// to the backend, so the step makes the same raw calls with the same description and the table's <c>HostUpload</c>
/// row, written out here; the backend's tests cover the factory itself.
/// </para>
/// </remarks>
internal static class GraphicsVulkanScenario
{
    /// <summary>The scenario's name on the command line.</summary>
    public const string Name = "graphics-vulkan";

    /// <summary>Parses the scenario's options.</summary>
    /// <param name="args">The arguments after the scenario name.</param>
    /// <param name="loaderPath">The explicit loader, or null.</param>
    /// <param name="error">Why parsing failed, or empty.</param>
    /// <returns><see langword="true"/> if the options are valid.</returns>
    public static bool TryParse(ReadOnlySpan<string> args, out string? loaderPath, out string error)
    {
        loaderPath = null;
        error = string.Empty;
        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 2 && args[0] == "--vulkan-loader" && Path.IsPathFullyQualified(args[1]))
        {
            loaderPath = args[1];
            return true;
        }

        error = $"{Name} takes no options except --vulkan-loader <absolute path>, got '{string.Join(' ', args.ToArray())}'.";
        return false;
    }

    /// <summary>Runs the scenario.</summary>
    /// <param name="loaderPath">The explicit loader, or null for the backend's own choice.</param>
    /// <returns>The outcome: <see cref="ExitCode.Pass"/>, or <see cref="ExitCode.SetupFailure"/>.</returns>
    public static ExitCode Run(string? loaderPath)
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        Result<VulkanBackend> created = VulkanBackend.Create(
            new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger, LoaderPath = loaderPath });

        var records = new MemoryLogRecord[sink.CapacityEntries];
        byte[] messages = new byte[sink.CapacityBytes];
        int count = sink.CopyTo(records, messages);
        for (int i = 0; i < count; i++)
        {
            Console.WriteLine($"log: {records[i].Category}: {Encoding.UTF8.GetString(records[i].Message.Span)}");
        }

        if (!created.TryGetValue(out VulkanBackend? backend))
        {
            Console.WriteLine($"VulkanBackend.Create failed: {created.Error}");
            return ExitCode.SetupFailure;
        }

        Console.WriteLine($"Backend: {backend.Name}, LoaderPath: {backend.LoaderPath ?? "null (system loader)"}");

        // An explicit loader must come back unchanged. Without one, Windows and Linux use the system loader (null);
        // macOS (unverified) may report a bundled one, which the smoke harness doesn't ship.
        if (backend.Name != "Vulkan" || (loaderPath is not null && backend.LoaderPath != loaderPath))
        {
            Console.WriteLine($"Unexpected backend: expected Vulkan with LoaderPath {loaderPath ?? "null"}.");
            return ExitCode.SetupFailure;
        }

        if (count != 1
            || records[0].Level != LogLevel.Information
            || records[0].Category != "Graphics.Vulkan"
            || !records[0].Message.Span.StartsWith("Vulkan loader: "u8))
        {
            Console.WriteLine($"Expected exactly one Information entry \"Vulkan loader: ...\" in Graphics.Vulkan, got {count} entries.");
            return ExitCode.SetupFailure;
        }

        return RunVma();
    }

    // Every Vulkan 1.3 device gets the step, so no device index is assumed; at least one must exist.
    private static unsafe ExitCode RunVma()
    {
        using Instance instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
            EnabledExtensions = [],
            EnabledLayers = [],
        });
        int tested = 0;
        foreach (PhysicalDevice adapter in instance.PhysicalDevices)
        {
            VkPhysicalDeviceProperties2 properties = new();
            adapter.GetProperties2(ref properties);
            string name = Marshal.PtrToStringUTF8((nint)properties.Properties.DeviceName) ?? string.Empty;
            if (properties.Properties.ApiVersion < VK.API_VERSION_1_3)
            {
                Console.WriteLine($"VMA: {name}: skipped, not Vulkan 1.3");
                continue;
            }

            // Any queue family serves: VMA needs a device, not a queue, and family 0 exists on every device.
            float priority = 1.0f;
            using Device device = adapter.CreateDevice(new Device.CreateInfo
            {
                Extensions = [],
                QueueCreateInfos = [new VkDeviceQueueCreateInfo { QueueFamilyIndex = 0, QueueCount = 1, QueuePriorities = &priority }],
            });
            if (!RunVmaOnDevice(instance, adapter, device, name))
            {
                return ExitCode.SetupFailure;
            }

            tested++;
        }

        if (tested == 0)
        {
            Console.WriteLine("VMA: no Vulkan 1.3 device found.");
            return ExitCode.SetupFailure;
        }

        return ExitCode.Pass;
    }

    private static unsafe bool RunVmaOnDevice(Instance instance, PhysicalDevice adapter, Device device, string name)
    {
        // The description the backend's factory builds (docs/design/graphics-vulkan.md section 6); this device enables no
        // VK_EXT_memory_budget, so there is no budget flag. Sound: the functions are a local, valid until VMA returns.
        VmaVulkanFunctions functions = new()
        {
            VkGetInstanceProcAddr = Vulkan.GetInstanceProcAddr,
            VkGetDeviceProcAddr = device.PInvoke.vkGetDeviceProcAddr,
        };
        VmaAllocatorCreateInfo info = new()
        {
            PhysicalDevice = adapter.Handle,
            Device = device.Handle,
            Instance = instance.Handle,
            VulkanApiVersion = VK.API_VERSION_1_3,
            PVulkanFunctions = &functions,
        };
        VmaAllocatorHandle allocator = default;
        VkResult result = Vma.PInvoke.vmaCreateAllocator(&info, &allocator);
        if (result != VkResult.Success)
        {
            Console.WriteLine($"VMA: {name}: vmaCreateAllocator returned {result}.");
            return false;
        }

        try
        {
            return RunBuffer(allocator, name);
        }
        finally
        {
            Vma.PInvoke.vmaDestroyAllocator(allocator);
        }
    }

    private static unsafe bool RunBuffer(VmaAllocatorHandle allocator, string name)
    {
        const int Size = 256;
        const VkMemoryPropertyFlags Required = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit;
        VkBufferCreateInfo bufferInfo = new()
        {
            Size = new ByteSize64(Size),
            Usage = VkBufferUsageFlags.TransferSrcBit,
            SharingMode = VkSharingMode.Exclusive,
        };

        // The placement table's HostUpload row (section 6).
        VmaAllocationCreateInfo placement = new()
        {
            Usage = VmaMemoryUsage.Auto,
            Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.WithinBudgetBit,
            RequiredFlags = Required,
        };
        VkBuffer buffer = default;
        VmaAllocation allocation = default;
        VmaAllocationInfo allocated = default;
        VkResult result = Vma.PInvoke.vmaCreateBuffer(allocator, &bufferInfo, &placement, &buffer, &allocation, &allocated);
        if (result != VkResult.Success)
        {
            Console.WriteLine($"VMA: {name}: vmaCreateBuffer returned {result}.");
            return false;
        }

        try
        {
            VkMemoryPropertyFlags flags = default;
            Vma.PInvoke.vmaGetMemoryTypeProperties(allocator, allocated.MemoryType, &flags);
            byte* mapped = (byte*)allocated.PMappedData.Pointer;
            Console.WriteLine($"VMA: {name}: HostUpload buffer of {Size} bytes in memory type {allocated.MemoryType} (flags 0x{(uint)flags:x}), {(mapped is null ? "not mapped" : "mapped")}");
            if ((flags & Required) != Required || mapped is null)
            {
                Console.WriteLine($"VMA: {name}: expected mapped host-visible, host-coherent memory.");
                return false;
            }

            for (int i = 0; i < Size; i++)
            {
                mapped[i] = (byte)(i ^ 0x5A);
            }

            for (int i = 0; i < Size; i++)
            {
                if (mapped[i] != (byte)(i ^ 0x5A))
                {
                    Console.WriteLine($"VMA: {name}: byte {i} read back as {mapped[i]}.");
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Vma.PInvoke.vmaDestroyBuffer(allocator, buffer, allocation);
        }
    }
}
