using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Graphics.Vulkan.Resources;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// Observes the factory's native calls: counts each one and copies what it was given while its pointers are valid, then
/// either forwards it to <see cref="Inner"/> (the real calls) or scripts it: a buffer and allocation with made-up
/// handles counting up from <see cref="FirstHandle"/>, and <see cref="MappedPointer"/> as the mapping.
/// </summary>
internal sealed unsafe class RecordingBuffers : BufferFactory.INative
{
    /// <summary>The first scripted handle; each scripted creation takes the next two.</summary>
    public const nint FirstHandle = 0x1000;

    private nint _nextHandle = FirstHandle;

    /// <summary>Gets the real calls to forward to, or <see langword="null"/> to script them.</summary>
    public BufferFactory.INative? Inner { get; init; }

    /// <summary>Gets the scripted result of <c>vmaCreateBuffer</c>.</summary>
    public VkResult Result { get; init; } = VkResult.Success;

    /// <summary>Gets the scripted mapping, written for every memory kind, so the factory's own rule decides.</summary>
    public nint MappedPointer { get; init; } = 0x7000;

    public int CreateCalls { get; private set; }

    public VkBufferCreateInfo LastInfo { get; private set; }

    public VmaAllocationCreateInfo LastPlacement { get; private set; }

    /// <summary>Gets the last created buffer and allocation.</summary>
    public (VkBuffer Buffer, VmaAllocation Allocation) LastCreated { get; private set; }

    public List<(VkBuffer Buffer, VmaAllocation Allocation)> Destroyed { get; } = [];

    /// <summary>Gets the names given to VMA, with their allocations.</summary>
    public List<(VmaAllocation Allocation, string Name)> AllocationNames { get; } = [];

    /// <summary>Gets the names given to Vulkan, with their object types and handles.</summary>
    public List<(VkObjectType Type, ulong Handle, string Name)> ObjectNames { get; } = [];

    public VkResult CreateBuffer(VkBufferCreateInfo* info, VmaAllocationCreateInfo* placement, VkBuffer* buffer, VmaAllocation* allocation, VmaAllocationInfo* allocated)
    {
        CreateCalls++;
        LastInfo = *info;
        LastPlacement = *placement;
        VkResult result;
        if (Inner is not null)
        {
            result = Inner.CreateBuffer(info, placement, buffer, allocation, allocated);
        }
        else
        {
            result = Result;
            if (result == VkResult.Success)
            {
                *buffer = new VkBuffer(_nextHandle++);
                *allocation = new VmaAllocation(_nextHandle++);
                allocated->PMappedData = (void*)MappedPointer;
            }
        }

        LastCreated = (*buffer, *allocation);
        return result;
    }

    public void DestroyBuffer(VkBuffer buffer, VmaAllocation allocation)
    {
        Destroyed.Add((buffer, allocation));
        Inner?.DestroyBuffer(buffer, allocation);
    }

    public void SetAllocationName(VmaAllocation allocation, byte* name)
    {
        AllocationNames.Add((allocation, Read(name)));
        Inner?.SetAllocationName(allocation, name);
    }

    public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info)
    {
        ObjectNames.Add((info->ObjectType, (ulong)info->ObjectHandle, Read(info->ObjectName)));
        return Inner?.SetName(info) ?? VkResult.Success;
    }

    /// <summary>Reads a NUL-terminated UTF-8 name.</summary>
    public static string Read(byte* name) => Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name));
}
