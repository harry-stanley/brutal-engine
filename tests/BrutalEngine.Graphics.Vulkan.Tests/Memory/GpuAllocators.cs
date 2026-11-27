using System;
using System.Collections.Generic;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Memory;

/// <summary>Helpers for the <c>Gpu</c> memory tests: devices with or without the budget extension, and VMA buffers on them.</summary>
internal static unsafe class GpuAllocators
{
    public const string MemoryBudget = "VK_EXT_memory_budget";

    /// <summary>
    /// Creates a device on a suitable adapter with every optional feature it has and the extensions the table picks, minus
    /// <c>VK_EXT_memory_budget</c> when <paramref name="withoutBudget"/> is set, so the budget flag is tested both ways on
    /// any adapter.
    /// </summary>
    public static VulkanDeviceCore CreateDevice(VulkanInstance instance, GpuDevices.Adapter adapter, bool withoutBudget)
    {
        FeatureChain.NegotiatedFeatures features = FeatureChain.Negotiate(adapter.Candidate, adapter.Family, GraphicsFeatures.None, FeatureChain.Negotiable);
        HashSet<string> offered = new(adapter.Candidate.DeviceExtensions.ToArray(), StringComparer.Ordinal);
        DeviceExtensionSet.Selection extensions = DeviceExtensionSet.Choose(offered, instance.EnabledExtensions);
        if (withoutBudget)
        {
            extensions = extensions with { Extensions = Array.FindAll(extensions.Extensions, name => name != MemoryBudget), MemoryBudget = false };
        }

        Result<VulkanDeviceCore> result = VulkanDeviceCore.Create(instance, adapter.Device, adapter.Family, features, extensions, new VulkanCallGate());
        Assert.True(result.IsSuccess, $"{adapter.Candidate.Name}: {result}");
        return result.Value;
    }

    /// <summary>Creates a transfer buffer of <paramref name="size"/> bytes with VMA, asserting success.</summary>
    public static Buffer CreateBuffer(VmaHandle allocator, in VmaAllocationCreateInfo placement, int size)
    {
        VkBufferCreateInfo info = new()
        {
            Size = new ByteSize64((ulong)size),
            Usage = VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.TransferDstBit,
            SharingMode = VkSharingMode.Exclusive,
        };
        VkResult result = allocator.CreateBuffer(info, placement, out VkBuffer buffer, out VmaAllocation allocation, out VmaAllocationInfo allocationInfo);
        Assert.Equal(VkResult.Success, result);
        Assert.False(buffer.IsNull());
        Assert.False(allocation.IsNull());
        allocator.GetMemoryTypeProperties(allocationInfo.MemoryType, out VkMemoryPropertyFlags flags);
        return new Buffer(buffer, allocation, allocationInfo, flags);
    }

    /// <summary>Writes a pattern through the buffer's mapping and reads it back.</summary>
    public static void WriteAndReadBack(in Buffer buffer, int size)
    {
        byte* mapped = (byte*)buffer.Info.PMappedData.Pointer;
        Assert.True(mapped is not null, "a MAPPED allocation has a mapping");
        for (int i = 0; i < size; i++)
        {
            mapped[i] = (byte)(i * 7);
        }

        for (int i = 0; i < size; i++)
        {
            Assert.Equal((byte)(i * 7), mapped[i]);
        }
    }

    /// <summary>A VMA buffer, its allocation, where VMA placed it and the flags of that memory type.</summary>
    public readonly record struct Buffer(VkBuffer Handle, VmaAllocation Allocation, VmaAllocationInfo Info, VkMemoryPropertyFlags MemoryFlags);
}
