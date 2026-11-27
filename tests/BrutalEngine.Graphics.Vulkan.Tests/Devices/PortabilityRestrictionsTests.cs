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

    [Theory]
    [MemberData(nameof(Fields))]
    public void Read_ThroughTheDeviceChains_EachField(string restricted, int alignment, bool comparison, bool beyondStride, bool arrays, bool constantAlpha)
    {
        PortabilityRestrictions expected = new(comparison, beyondStride, alignment, arrays, constantAlpha);
        ScriptedAdapter adapter = new() { PortabilityFeatures = AllAllowed(), PortabilityAlignment = alignment };
        adapter.PortabilityFeatures.MutableComparisonSamplers = restricted != "mutableComparisonSamplers";
        adapter.PortabilityFeatures.VertexAttributeAccessBeyondStride = restricted != "vertexAttributeAccessBeyondStride";
        adapter.PortabilityFeatures.MultisampleArrayImage = restricted != "multisampleArrayImage";
        adapter.PortabilityFeatures.ConstantAlphaColorBlendFactors = restricted != "constantAlphaColorBlendFactors";

        CapabilityMapping.Report report = CapabilityMapping.Read(adapter, default, portabilitySubset: true);

        Assert.Equal(expected, report.Portability);
        Assert.Equal(1, adapter.FeatureQueries);
        Assert.Contains(VkStructureType.PhysicalDevicePortabilitySubsetPropertiesKHR, adapter.PropertyChain);
    }

    [Fact]
    public void Absent_IsNone()
    {
        // Restricting features on the adapter, which a device without the extension must never read.
        ScriptedAdapter adapter = new() { PortabilityAlignment = 8 };

        CapabilityMapping.Report report = CapabilityMapping.Read(adapter, default, portabilitySubset: false);

        Assert.Equal(new PortabilityRestrictions(true, true, 1, true, true), report.Portability);
        Assert.Equal(PortabilityRestrictions.None, report.Portability);
        Assert.Equal(0, adapter.FeatureQueries);
        Assert.DoesNotContain(VkStructureType.PhysicalDevicePortabilitySubsetPropertiesKHR, adapter.PropertyChain);
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
