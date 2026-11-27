using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// Maps an adapter's Vulkan properties to the render interface's capabilities (docs/design/graphics-vulkan.md section 3,
/// "Capabilities").
/// </summary>
/// <remarks>
/// <see cref="ToLimits"/> takes each <see cref="GraphicsLimits"/> property from the <c>VkPhysicalDeviceLimits</c> field
/// the graphics note lists (docs/design/graphics.md section 2). Stateless and thread-safe. No member allocates.
/// </remarks>
internal static class CapabilityMapping
{
    // Every count SampleCounts has: 1 to 16, whose values are Vulkan's bits.
    private const VkSampleCountFlags Mappable = VkSampleCountFlags._1Bit | VkSampleCountFlags._2Bit | VkSampleCountFlags._4Bit
        | VkSampleCountFlags._8Bit | VkSampleCountFlags._16Bit;

    /// <summary>Maps the limits.</summary>
    /// <param name="limits">The adapter's limits.</param>
    /// <param name="timestamps">Whether <see cref="GraphicsFeatures.TimestampQueries"/> is enabled.</param>
    /// <returns>
    /// The limits: <c>maxImageDimension2D</c>, <c>maxImageArrayLayers</c>, <c>maxBoundDescriptorSets</c>,
    /// <c>maxPushConstantsSize</c>, <c>maxColorAttachments</c>, the two offset alignments, <c>maxSamplerAnisotropy</c>,
    /// <c>framebufferColorSampleCounts</c> (<see cref="ToSampleCounts"/>), and <c>timestampPeriod</c> only with
    /// <paramref name="timestamps"/>, else 0.
    /// </returns>
    public static GraphicsLimits ToLimits(in VkPhysicalDeviceLimits limits, bool timestamps) => new()
    {
        MaxTextureDimension2D = limits.MaxImageDimension2D,
        MaxTextureArrayLayers = limits.MaxImageArrayLayers,
        MaxBindGroups = limits.MaxBoundDescriptorSets,
        MaxPushConstantBytes = limits.MaxPushConstantsSize,
        MaxColorTargets = limits.MaxColorAttachments,
        MinUniformBufferOffsetAlignment = (long)limits.MinUniformBufferOffsetAlignment.Value,
        MinStorageBufferOffsetAlignment = (long)limits.MinStorageBufferOffsetAlignment.Value,
        MaxSamplerAnisotropy = limits.MaxSamplerAnisotropy,
        ColorSampleCounts = ToSampleCounts(limits.FramebufferColorSampleCounts),
        TimestampPeriodNanoseconds = timestamps ? limits.TimestampPeriod : 0,
    };

    /// <summary>Maps the negotiated features.</summary>
    /// <param name="features">The negotiated features: the required set plus the optional ones the adapter has.</param>
    /// <returns>The enabled features.</returns>
    public static GraphicsFeatures ToFeatures(in FeatureChain.NegotiatedFeatures features) => features.Enabled;

    /// <summary>Maps Vulkan sample counts; the 32 and 64 counts, which <see cref="SampleCounts"/> doesn't have, are dropped.</summary>
    /// <param name="counts">The Vulkan counts.</param>
    /// <returns>The counts from 1 to 16 that <paramref name="counts"/> has.</returns>
    public static SampleCounts ToSampleCounts(VkSampleCountFlags counts) => (SampleCounts)(int)(counts & Mappable);
}
