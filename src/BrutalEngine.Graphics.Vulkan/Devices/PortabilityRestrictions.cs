using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// The portability-subset restrictions the render interface can meet (docs/design/graphics-vulkan.md section 3, "The
/// portability subset"), read once from <c>VkPhysicalDevicePortabilitySubsetFeaturesKHR</c> and
/// <c>VkPhysicalDevicePortabilitySubsetPropertiesKHR</c> when <c>VK_KHR_portability_subset</c> is enabled. Each
/// <see langword="true"/> field allows what its Vulkan field allows; the creation calls that apply them turn a
/// restricted request into <c>Unsupported</c>.
/// </summary>
/// <param name="MutableComparisonSamplers">Comparison samplers can be created (<c>mutableComparisonSamplers</c>).</param>
/// <param name="VertexAttributeAccessBeyondStride">A vertex attribute may reach past its binding's stride (<c>vertexAttributeAccessBeyondStride</c>).</param>
/// <param name="MinVertexInputBindingStrideAlignment">Every vertex binding stride must be a multiple of this (<c>minVertexInputBindingStrideAlignment</c>), as given.</param>
/// <param name="MultisampleArrayImage">A multisampled texture may have several layers (<c>multisampleArrayImage</c>).</param>
/// <param name="ConstantAlphaColorBlendFactors">Constant-alpha colour blend factors may be used (<c>constantAlphaColorBlendFactors</c>).</param>
/// <remarks>
/// Use <see cref="None"/> for a device without the extension: <c>default</c> restricts everything and has an alignment
/// of 0. Unverified on macOS, the only platform that needs the extension: tested with hand-built structures only.
/// Immutable; safe to read from any thread; no member allocates.
/// </remarks>
internal readonly record struct PortabilityRestrictions(
    bool MutableComparisonSamplers,
    bool VertexAttributeAccessBeyondStride,
    int MinVertexInputBindingStrideAlignment,
    bool MultisampleArrayImage,
    bool ConstantAlphaColorBlendFactors)
{
    /// <summary>Gets the value of a device without <c>VK_KHR_portability_subset</c>: nothing is restricted, and the alignment is 1.</summary>
    public static PortabilityRestrictions None => new(true, true, 1, true, true);

    /// <summary>Reads the restrictions from the extension's structures.</summary>
    /// <param name="features">The features, as <c>vkGetPhysicalDeviceFeatures2</c> filled them.</param>
    /// <param name="properties">The properties, as <c>vkGetPhysicalDeviceProperties2</c> filled them.</param>
    /// <returns>Each field from its Vulkan field.</returns>
    public static PortabilityRestrictions Read(in VkPhysicalDevicePortabilitySubsetFeaturesKHR features, in VkPhysicalDevicePortabilitySubsetPropertiesKHR properties) => new(
        features.MutableComparisonSamplers,
        features.VertexAttributeAccessBeyondStride,
        properties.MinVertexInputBindingStrideAlignment,
        features.MultisampleArrayImage,
        features.ConstantAlphaColorBlendFactors);
}
