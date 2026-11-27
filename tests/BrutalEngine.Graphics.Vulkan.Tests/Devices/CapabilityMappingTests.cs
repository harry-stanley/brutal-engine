using System;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed class CapabilityMappingTests
{
    // The ten mapped fields, distinct from each other and from every other field of the structure.
    private static GraphicsLimits Mapped => new()
    {
        MaxTextureDimension2D = 8193,
        MaxTextureArrayLayers = 2049,
        MaxBindGroups = 7,
        MaxPushConstantBytes = 129,
        MaxColorTargets = 6,
        MinUniformBufferOffsetAlignment = 256,
        MinStorageBufferOffsetAlignment = 32,
        MaxSamplerAnisotropy = 13.5f,
        ColorSampleCounts = SampleCounts.X1 | SampleCounts.X4 | SampleCounts.X16,
        TimestampPeriodNanoseconds = 1.25,
    };

    /// <summary>
    /// Limits whose every 4-byte word holds its own value (1000 + its index), then the ten mapped fields, so a mapping
    /// that reads a neighbouring field gets a value no expected property has.
    /// </summary>
    internal static VkPhysicalDeviceLimits HandBuilt()
    {
        VkPhysicalDeviceLimits limits = default;
        Span<int> words = MemoryMarshal.Cast<byte, int>(MemoryMarshal.AsBytes(new Span<VkPhysicalDeviceLimits>(ref limits)));
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = 1000 + i;
        }

        limits.MaxImageDimension2D = 8193;
        limits.MaxImageArrayLayers = 2049;
        limits.MaxBoundDescriptorSets = 7;
        limits.MaxPushConstantsSize = 129;
        limits.MaxColorAttachments = 6;
        limits.MinUniformBufferOffsetAlignment = new ByteSize64(256);
        limits.MinStorageBufferOffsetAlignment = new ByteSize64(32);
        limits.MaxSamplerAnisotropy = 13.5f;
        limits.FramebufferColorSampleCounts = VkSampleCountFlags._1Bit | VkSampleCountFlags._4Bit | VkSampleCountFlags._16Bit | VkSampleCountFlags._32Bit | VkSampleCountFlags._64Bit;
        limits.TimestampPeriod = 1.25f;
        return limits;
    }

    [Fact]
    public void ToLimits_MapsEachField()
    {
        VkPhysicalDeviceLimits limits = HandBuilt();

        Assert.Equal(Mapped, CapabilityMapping.ToLimits(limits, timestamps: true));
    }

    [Fact]
    public void ToLimits_NoTimestamps_PeriodIsZero()
    {
        VkPhysicalDeviceLimits limits = HandBuilt();

        Assert.Equal(Mapped with { TimestampPeriodNanoseconds = 0 }, CapabilityMapping.ToLimits(limits, timestamps: false));
    }

    [Theory]
    [InlineData(VkSampleCountFlags.None, SampleCounts.None)]
    [InlineData(VkSampleCountFlags._1Bit, SampleCounts.X1)]
    [InlineData(VkSampleCountFlags._2Bit, SampleCounts.X2)]
    [InlineData(VkSampleCountFlags._4Bit, SampleCounts.X4)]
    [InlineData(VkSampleCountFlags._8Bit, SampleCounts.X8)]
    [InlineData(VkSampleCountFlags._16Bit, SampleCounts.X16)]
    [InlineData(VkSampleCountFlags._32Bit, SampleCounts.None)]
    [InlineData(VkSampleCountFlags._64Bit, SampleCounts.None)]
    public void ToSampleCounts_EachCount_MapsOrDrops(VkSampleCountFlags counts, SampleCounts expected) =>
        Assert.Equal(expected, CapabilityMapping.ToSampleCounts(counts));

    [Theory]
    [InlineData(GraphicsFeatures.None)]
    [InlineData(GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TextureCompressionAstc)]
    [InlineData(GraphicsFeatures.TimestampQueries | GraphicsFeatures.TextureCompressionBC)]
    public void Read_ScriptedAdapter_ReportsFeaturesAndLimits(GraphicsFeatures enabled)
    {
        ScriptedAdapter adapter = new() { Limits = HandBuilt() };

        CapabilityMapping.Report report = CapabilityMapping.Read(adapter, new FeatureChain.NegotiatedFeatures(enabled), portabilitySubset: false);

        // The period follows the enabled features, not the adapter.
        double period = (enabled & GraphicsFeatures.TimestampQueries) != 0 ? 1.25 : 0;
        Assert.Equal(enabled, report.Capabilities.Features);
        Assert.Equal(Mapped with { TimestampPeriodNanoseconds = period }, report.Capabilities.Limits);
        Assert.Equal([VkStructureType.PhysicalDeviceVulkan12Properties], adapter.PropertyChain);
    }
}
