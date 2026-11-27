using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Devices;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

/// <summary>Test double: an adapter that answers the format query from scripted values and records what it was asked.</summary>
internal sealed unsafe class ScriptedAdapter : FormatTable.ISource
{
    public VkPhysicalDeviceLimits Limits;

    public VkSampleCountFlags IntegerColorSampleCounts { get; set; } = VkSampleCountFlags._1Bit;

    /// <summary>Each format's optimal-tiling features; unlisted formats have none.</summary>
    public Dictionary<VkFormat, VkFormatFeatureFlags> Features { get; } = [];

    public List<VkFormat> FormatQueries { get; } = [];

    public void GetFormatProperties2(VkFormat format, VkFormatProperties2* properties)
    {
        FormatQueries.Add(format);
        properties->FormatProperties = new() { OptimalTilingFeatures = Features.GetValueOrDefault(format) };
    }
}
