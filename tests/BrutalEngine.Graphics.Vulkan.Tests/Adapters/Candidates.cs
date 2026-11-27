using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

/// <summary>Hand-built adapter candidates and queue families for the rules' tests.</summary>
internal static class Candidates
{
    /// <summary>Every <see cref="GraphicsFeatures"/> flag, written from the graphics note, not from the enum.</summary>
    public const GraphicsFeatures AllFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries
        | GraphicsFeatures.TextureCompressionBC | GraphicsFeatures.TextureCompressionAstc;

    /// <summary>
    /// A candidate that passes every rule, with or without a surface and with every feature required. The swapchain
    /// extension isn't the first extension, and the family that passes isn't the first family: the first is
    /// transfer-only, so a rule that looked only at position 0 would fail it. Families' indices differ from their
    /// positions.
    /// </summary>
    public static AdapterCandidate Suitable() => new()
    {
        Name = "Test GPU",
        Type = AdapterType.Discrete,
        Driver = VkDriverId.NvidiaProprietary,
        ApiVersionMajor = 1,
        ApiVersionMinor = 3,
        ApiVersionPatch = 280,
        DynamicRendering = true,
        Synchronization2 = true,
        SamplerAnisotropy = true,
        TextureCompressionBC = true,
        TextureCompressionAstcLdr = true,
        TimestampPeriod = 1.0f,
        DeviceExtensions = new[] { "VK_KHR_maintenance5", "VK_KHR_swapchain", "VK_EXT_memory_budget" },
        QueueFamilies = new[]
        {
            Family(10, graphics: false, compute: false, present: false, timestampBits: 0),
            Family(11, graphics: true, compute: true, present: true, timestampBits: 64),
        },
    };

    /// <summary>Builds a queue family.</summary>
    public static AdapterCandidate.QueueFamily Family(int index, bool graphics, bool compute, bool present, int timestampBits = 64) =>
        new(index, graphics, compute, present, timestampBits);
}
