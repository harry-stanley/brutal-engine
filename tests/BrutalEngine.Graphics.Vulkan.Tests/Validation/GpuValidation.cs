using System;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>Helpers for the <c>Gpu</c> validation tests: validated instances and deliberate validation errors.</summary>
internal static unsafe class GpuValidation
{
    /// <summary>The factory's precondition: a backend exists, so BRUTAL's Vulkan class is bound to the system loader.</summary>
    public static void BindLoader()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        Result<VulkanBackend> backend = VulkanBackend.Create(new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger });
        Assert.True(backend.IsSuccess, backend.ToString());
    }

    /// <summary>Creates a validated instance that logs to <paramref name="category"/>.</summary>
    public static VulkanInstance Create(LogCategory category, ReadOnlyMemory<ValidationAllowance> allowList = default, ValidationSink.HandleTracker? handles = null)
    {
        BindLoader();
        Result<VulkanInstance> result = InstanceFactory.Create(
            new InstanceFactory.Options { ApplicationName = "ValidationTests", Validation = true, ValidationLog = category, ValidationAllowList = allowList, ValidationHandles = handles },
            new VulkanCallGate());
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    /// <summary>
    /// A known invalid call: a messenger with no severity bits (VUID-VkDebugUtilsMessengerCreateInfoEXT-messageSeverity-parameter;
    /// older layers name it ...-requiredbitmask).
    /// The layer reports it to the instance's messengers and the loader still creates the messenger, which is destroyed again.
    /// </summary>
    public static void CreateMessengerWithoutSeverity(VulkanInstance instance)
    {
        VkDebugUtilsMessengerCreateInfoEXT info = ValidationCallback.CreateInfo(instance.Sink!);
        info.MessageSeverity = default;
        VkDebugUtilsMessengerEXT messenger = default;
        Instance brutal = instance.Brutal;
        VkResult result = brutal.PInvoke.vkCreateDebugUtilsMessengerEXT.Invoke(brutal.Handle, &info, null, &messenger);
        Assert.Equal(VkResult.Success, result);
        brutal.PInvoke.vkDestroyDebugUtilsMessengerEXT.Invoke(brutal.Handle, messenger, null);
    }

    /// <summary>
    /// A synchronization hazard: two fills of one buffer recorded without a barrier between them, which only
    /// synchronization validation reports (SYNC-HAZARD-WRITE-AFTER-WRITE), at record time.
    /// </summary>
    public static void RecordWriteAfterWrite(VulkanInstance instance)
    {
        PhysicalDevice physical = instance.Brutal.PhysicalDevices[0];
        int family = physical.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit);
        Assert.True(family >= 0);
        float priority = 1.0f;
        using Device device = physical.CreateDevice(new Device.CreateInfo
        {
            QueueCreateInfos = [new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = 1, QueuePriorities = &priority }],
        });

        const int Size = 256;
        VkBuffer buffer = device.CreateBuffer(
            new VkBufferCreateInfo { Size = new ByteSize64(Size), Usage = VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive },
            null);
        VkMemoryRequirements requirements = device.GetBufferMemoryRequirements(buffer);
        int type = System.Numerics.BitOperations.TrailingZeroCount((uint)requirements.MemoryTypeBits);
        VkDeviceMemory memory = device.AllocateMemory(new VkMemoryAllocateInfo { AllocationSize = requirements.Size, MemoryTypeIndex = type }, null);
        VkCommandPool pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
        try
        {
            device.BindBufferMemory(buffer, memory, new ByteSize64(0));
            VkCommandBufferAllocateInfo allocate = new() { CommandPool = pool, Level = VkCommandBufferLevel.Primary };

            // BRUTAL makes commandBufferCount internal (its wrapper sets it); write the field at its explicit offset, 28
            // (Brutal.Vulkan/Generated/Structs/VkCommandBufferAllocateInfo.gen.cs:51-52). Sound: inside the local struct.
            *(int*)((byte*)&allocate + 28) = 1;
            VkCommandBuffer commands = default;
            Assert.Equal(VkResult.Success, device.PInvoke.vkAllocateCommandBuffers.Invoke(device.Handle, &allocate, &commands));
            VkCommandBufferBeginInfo begin = new() { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            Assert.Equal(VkResult.Success, device.PInvoke.vkBeginCommandBuffer.Invoke(commands, &begin));
            device.PInvoke.vkCmdFillBuffer.Invoke(commands, buffer, new ByteSize64(0), new ByteSize64(Size), 1);
            device.PInvoke.vkCmdFillBuffer.Invoke(commands, buffer, new ByteSize64(0), new ByteSize64(Size), 2);
            Assert.Equal(VkResult.Success, device.PInvoke.vkEndCommandBuffer.Invoke(commands));
        }
        finally
        {
            device.DestroyCommandPool(pool, null);
            device.DestroyBuffer(buffer, null);
            device.FreeMemory(memory, null);
        }
    }
}
