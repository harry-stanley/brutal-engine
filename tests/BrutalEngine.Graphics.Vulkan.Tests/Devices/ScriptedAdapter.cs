using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Devices;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

/// <summary>
/// Test double: an adapter that answers the capability queries from scripted values, filling each structure of a chain
/// it recognises by its <c>sType</c> (read at offset 0, <c>pNext</c> at offset 8, as Vulkan lays out every chained
/// structure), and records what it was asked.
/// </summary>
internal sealed unsafe class ScriptedAdapter : CapabilityMapping.ISource
{
    public VkPhysicalDeviceLimits Limits;

    public VkSampleCountFlags IntegerColorSampleCounts { get; set; } = VkSampleCountFlags._1Bit;

    public VkPhysicalDevicePortabilitySubsetFeaturesKHR PortabilityFeatures;

    public int PortabilityAlignment { get; set; } = 1;

    /// <summary>Each format's optimal-tiling features; unlisted formats have none.</summary>
    public Dictionary<VkFormat, VkFormatFeatureFlags> Features { get; } = [];

    /// <summary>Every structure type seen in the property chains, in order.</summary>
    public List<VkStructureType> PropertyChain { get; } = [];

    public List<VkFormat> FormatQueries { get; } = [];

    public int FeatureQueries { get; private set; }

    public void GetProperties2(VkPhysicalDeviceProperties2* properties)
    {
        properties->Properties.Limits = Limits;
        for (void* next = properties->Next; next is not null; next = *(void**)((byte*)next + 8))
        {
            VkStructureType type = *(VkStructureType*)next;
            PropertyChain.Add(type);
            if (type == VkStructureType.PhysicalDeviceVulkan12Properties)
            {
                ((VkPhysicalDeviceVulkan12Properties*)next)->FramebufferIntegerColorSampleCounts = IntegerColorSampleCounts;
            }
            else if (type == VkStructureType.PhysicalDevicePortabilitySubsetPropertiesKHR)
            {
                ((VkPhysicalDevicePortabilitySubsetPropertiesKHR*)next)->MinVertexInputBindingStrideAlignment = PortabilityAlignment;
            }
        }
    }

    public void GetFeatures2(VkPhysicalDeviceFeatures2* features)
    {
        FeatureQueries++;
        for (void* next = features->Next; next is not null; next = *(void**)((byte*)next + 8))
        {
            if (*(VkStructureType*)next == VkStructureType.PhysicalDevicePortabilitySubsetFeaturesKHR)
            {
                // Every field after sType and pNext (offsets 16-79), so a field read from the wrong place shows.
                VkPhysicalDevicePortabilitySubsetFeaturesKHR source = PortabilityFeatures;
                System.Buffer.MemoryCopy((byte*)&source + 16, (byte*)next + 16, 64, 64);
            }
        }
    }

    public void GetFormatProperties2(VkFormat format, VkFormatProperties2* properties)
    {
        FormatQueries.Add(format);
        properties->FormatProperties = new() { OptimalTilingFeatures = Features.GetValueOrDefault(format) };
    }
}
