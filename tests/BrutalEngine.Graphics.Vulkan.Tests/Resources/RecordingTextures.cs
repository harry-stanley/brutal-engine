using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Resources;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// Observes the texture factory's native calls: counts each one and copies what it was given while its pointers are
/// valid, then either forwards it to <see cref="Inner"/> (the real calls) or scripts it: generous format limits (or
/// <see cref="Limits"/>), and an image and allocation with made-up handles counting up from 0x1000.
/// </summary>
internal sealed unsafe class RecordingTextures : TextureFactory.INative
{
    private nint _nextHandle = 0x1000;

    /// <summary>Gets the real calls to forward to, or <see langword="null"/> to script them.</summary>
    public TextureFactory.INative? Inner { get; init; }

    /// <summary>Gets the scripted result of the format query.</summary>
    public VkResult QueryResult { get; init; } = VkResult.Success;

    /// <summary>Gets the scripted limits the format query returns.</summary>
    public VkImageFormatProperties Limits { get; init; } = new()
    {
        MaxExtent = new VkExtent3D { Width = 16384, Height = 16384, Depth = 1 },
        MaxMipLevels = 15,
        MaxArrayLayers = 2048,
        SampleCounts = VkSampleCountFlags._1Bit | VkSampleCountFlags._2Bit | VkSampleCountFlags._4Bit | VkSampleCountFlags._8Bit | VkSampleCountFlags._16Bit,
    };

    /// <summary>Gets the scripted result of <c>vmaCreateImage</c>.</summary>
    public VkResult CreateResult { get; init; } = VkResult.Success;

    public int QueryCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public VkPhysicalDeviceImageFormatInfo2 LastQuery { get; private set; }

    public VkImageCreateInfo LastInfo { get; private set; }

    public VmaAllocationCreateInfo LastPlacement { get; private set; }

    /// <summary>Gets the last created image and allocation.</summary>
    public (VkImage Image, VmaAllocation Allocation) LastCreated { get; private set; }

    public List<(VkImage Image, VmaAllocation Allocation)> Destroyed { get; } = [];

    public List<(VmaAllocation Allocation, string Name)> AllocationNames { get; } = [];

    public List<(VkObjectType Type, ulong Handle, string Name)> ObjectNames { get; } = [];

    /// <summary>Gets the category each budget log was asked to write to.</summary>
    public List<LogCategory> BudgetLogs { get; } = [];

    public VkResult GetImageFormatProperties(VkPhysicalDeviceImageFormatInfo2* info, VkImageFormatProperties2* properties)
    {
        QueryCalls++;
        LastQuery = *info;
        if (Inner is not null)
        {
            return Inner.GetImageFormatProperties(info, properties);
        }

        properties->ImageFormatProperties = Limits;
        return QueryResult;
    }

    public VkResult CreateImage(VkImageCreateInfo* info, VmaAllocationCreateInfo* placement, VkImage* image, VmaAllocation* allocation)
    {
        CreateCalls++;
        LastInfo = *info;
        LastPlacement = *placement;
        VkResult result;
        if (Inner is not null)
        {
            result = Inner.CreateImage(info, placement, image, allocation);
        }
        else
        {
            result = CreateResult;
            if (result == VkResult.Success)
            {
                *image = new VkImage(_nextHandle++);
                *allocation = new VmaAllocation(_nextHandle++);
            }
        }

        LastCreated = (*image, *allocation);
        return result;
    }

    public void DestroyImage(VkImage image, VmaAllocation allocation)
    {
        Destroyed.Add((image, allocation));
        Inner?.DestroyImage(image, allocation);
    }

    public void SetAllocationName(VmaAllocation allocation, byte* name)
    {
        AllocationNames.Add((allocation, RecordingBuffers.Read(name)));
        Inner?.SetAllocationName(allocation, name);
    }

    public void WriteBudgets(LogCategory log)
    {
        BudgetLogs.Add(log);
        Inner?.WriteBudgets(log);
    }

    public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info)
    {
        ObjectNames.Add((info->ObjectType, (ulong)info->ObjectHandle, RecordingBuffers.Read(info->ObjectName)));
        return Inner?.SetName(info) ?? VkResult.Success;
    }
}
