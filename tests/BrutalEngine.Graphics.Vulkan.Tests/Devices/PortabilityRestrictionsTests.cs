using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed class PortabilityRestrictionsTests
{
    // Which Vulkan feature is false (the others are true) and the alignment, then the expected fields in order:
    // MutableComparisonSamplers, VertexAttributeAccessBeyondStride, MultisampleArrayImage, ConstantAlphaColorBlendFactors.
    public static TheoryData<string, int, bool, bool, bool, bool> Fields => new()
    {
        { "mutableComparisonSamplers", 1, false, true, true, true },
        { "vertexAttributeAccessBeyondStride", 1, true, false, true, true },
        { "none", 4, true, true, true, true },
        { "multisampleArrayImage", 1, true, true, false, true },
        { "constantAlphaColorBlendFactors", 1, true, true, true, false },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void Read_EachField(string restricted, int alignment, bool comparison, bool beyondStride, bool arrays, bool constantAlpha)
    {
        PortabilityRestrictions expected = new(comparison, beyondStride, alignment, arrays, constantAlpha);
        VkPhysicalDevicePortabilitySubsetFeaturesKHR features = AllAllowed();
        features.MutableComparisonSamplers = restricted != "mutableComparisonSamplers";
        features.VertexAttributeAccessBeyondStride = restricted != "vertexAttributeAccessBeyondStride";
        features.MultisampleArrayImage = restricted != "multisampleArrayImage";
        features.ConstantAlphaColorBlendFactors = restricted != "constantAlphaColorBlendFactors";
        VkPhysicalDevicePortabilitySubsetPropertiesKHR properties = new() { MinVertexInputBindingStrideAlignment = alignment };

        Assert.Equal(expected, PortabilityRestrictions.Read(features, properties));
    }

    private static VkPhysicalDevicePortabilitySubsetFeaturesKHR AllAllowed() => new()
    {
        ConstantAlphaColorBlendFactors = true,
        Events = true,
        ImageViewFormatReinterpretation = true,
        ImageViewFormatSwizzle = true,
        ImageView2DOn3DImage = true,
        MultisampleArrayImage = true,
        MutableComparisonSamplers = true,
        PointPolygons = true,
        SamplerMipLodBias = true,
        SeparateStencilMaskRef = true,
        ShaderSampleRateInterpolationFunctions = true,
        TessellationIsolines = true,
        TessellationPointMode = true,
        TriangleFans = true,
        VertexAttributeAccessBeyondStride = true,
    };
}
