using System;
using System.Numerics;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed unsafe class CapabilityTests
{
    // Every flag the specification requires of R8G8B8A8_UNORM and D16_UNORM with optimal tiling (the "Required Format
    // Support" tables), and the sample counts framebufferColorSampleCounts and framebufferDepthSampleCounts must have.
    private const FormatSupport Rgba8Required = FormatSupport.Sampled | FormatSupport.Filterable | FormatSupport.ColorTarget
        | FormatSupport.Blendable | FormatSupport.Storage | FormatSupport.CopySource | FormatSupport.CopyDestination | FormatSupport.Multisample;

    private const FormatSupport Depth16Required = FormatSupport.Sampled | FormatSupport.DepthStencilTarget
        | FormatSupport.CopySource | FormatSupport.CopyDestination | FormatSupport.Multisample;

    [Fact]
    [Trait("Category", "Gpu")]
    public void RealDevice_LimitsArePlausibleAndNamingIsClean()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        int devices = 0;
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            foreach (GpuDevices.Adapter adapter in GpuDevices.Suitable(instance))
            {
                using VulkanDeviceCore core = GpuDevices.Create(instance, adapter, new VulkanCallGate(), null).Value;
                VkPhysicalDeviceProperties raw = default;
                adapter.Device.PInvoke.vkGetPhysicalDeviceProperties.Invoke(adapter.Device.Handle, &raw);
                AssertLimits(raw.Limits, core);
                AssertFormats(adapter.Device, core);

                devices++;
            }
        }

        Assert.True(devices > 0);
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void RealDevice_ReadsAndNamesAllocateNothing()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        using VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        using VulkanDeviceCore core = GpuDevices.Create(instance, GpuDevices.Suitable(instance)[0], new VulkanCallGate(), null).Value;
        long checksum = Read(core);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            checksum += Read(core);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(0, checksum);

        // Every format's support, the capabilities and the restrictions, folded into one number.
        static long Read(VulkanDeviceCore core)
        {
            long sum = 0;
            for (int format = 1; format <= FormatTable.Count; format++)
            {
                sum += (long)core.GetFormatSupport((TextureFormat)format);
            }

            GraphicsCapabilities capabilities = core.Capabilities;
            sum += capabilities.Limits.MaxTextureDimension2D + (long)capabilities.Features + core.Portability.MinVertexInputBindingStrideAlignment;
            return sum;
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void GetFormatSupport_NotATableFormat_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        using VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        using VulkanDeviceCore core = GpuDevices.Create(instance, GpuDevices.Suitable(instance)[0], new VulkanCallGate(), null).Value;

        Assert.Throws<ArgumentOutOfRangeException>("format", () => core.GetFormatSupport(TextureFormat.Undefined));
        Assert.Throws<ArgumentOutOfRangeException>("format", () => core.GetFormatSupport((TextureFormat)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>("format", () => core.GetFormatSupport((TextureFormat)17));
        Assert.NotEqual(FormatSupport.None, core.GetFormatSupport(TextureFormat.R8Unorm));
        Assert.NotEqual(FormatSupport.None, core.GetFormatSupport(TextureFormat.Depth32Float));
    }

    // Each limit is the driver's own field (read through the plain query, not the code under test's chain), and each is
    // at least the specification's minimum, which only a real driver's values reach.
    private static void AssertLimits(in VkPhysicalDeviceLimits raw, VulkanDeviceCore core)
    {
        GraphicsLimits limits = core.Capabilities.Limits;
        bool timestamps = core.Features.Has(GraphicsFeatures.TimestampQueries);
        Assert.Equal(core.Features.Enabled, core.Capabilities.Features);
        Assert.Equal(
            (raw.MaxImageDimension2D, raw.MaxImageArrayLayers, raw.MaxBoundDescriptorSets, raw.MaxPushConstantsSize, raw.MaxColorAttachments),
            (limits.MaxTextureDimension2D, limits.MaxTextureArrayLayers, limits.MaxBindGroups, limits.MaxPushConstantBytes, limits.MaxColorTargets));
        Assert.Equal(((long)raw.MinUniformBufferOffsetAlignment.Value, (long)raw.MinStorageBufferOffsetAlignment.Value), (limits.MinUniformBufferOffsetAlignment, limits.MinStorageBufferOffsetAlignment));
        Assert.Equal(raw.MaxSamplerAnisotropy, limits.MaxSamplerAnisotropy);
        Assert.Equal(timestamps ? raw.TimestampPeriod : 0, limits.TimestampPeriodNanoseconds);
        Assert.InRange(limits.MaxTextureDimension2D, 4096, int.MaxValue);
        Assert.InRange(limits.MaxTextureArrayLayers, 256, int.MaxValue);
        Assert.InRange(limits.MaxBindGroups, 4, int.MaxValue);
        Assert.InRange(limits.MaxPushConstantBytes, 128, int.MaxValue);
        Assert.InRange(limits.MaxColorTargets, 4, int.MaxValue);
        Assert.True(BitOperations.IsPow2(limits.MinUniformBufferOffsetAlignment) && limits.MinUniformBufferOffsetAlignment <= 256);
        Assert.True(BitOperations.IsPow2(limits.MinStorageBufferOffsetAlignment) && limits.MinStorageBufferOffsetAlignment <= 256);
        Assert.Equal(SampleCounts.X1 | SampleCounts.X4, limits.ColorSampleCounts & (SampleCounts.X1 | SampleCounts.X4));
        Assert.Equal(core.Extensions.PortabilitySubset ? core.Portability : PortabilityRestrictions.None, core.Portability);
        TestContext.Current.TestOutputHelper?.WriteLine(limits.ToString());
    }

    // Each format's flags are the driver's optimal-tiling bits (read through the plain query) mapped with the test's own
    // literal table, and the formats every driver must support have every required flag.
    private static void AssertFormats(PhysicalDevice adapter, VulkanDeviceCore core)
    {
        foreach ((TextureFormat format, VkFormat vk) in FormatTableTests.Pairs)
        {
            VkFormatProperties raw = default;
            adapter.PInvoke.vkGetPhysicalDeviceFormatProperties.Invoke(adapter.Handle, vk, &raw);
            FormatSupport expected = FormatSupport.None;
            foreach ((VkFormatFeatureFlags bit, FormatSupport flag) in FormatTableTests.BitPairs)
            {
                expected |= (raw.OptimalTilingFeatures & bit) != 0 ? flag : FormatSupport.None;
            }

            FormatSupport support = core.GetFormatSupport(format);
            Assert.Equal(expected, support & ~FormatSupport.Multisample);
            TestContext.Current.TestOutputHelper?.WriteLine($"{format}: {support}");
        }

        Assert.Equal(Rgba8Required, core.GetFormatSupport(TextureFormat.Rgba8Unorm) & Rgba8Required);
        Assert.Equal(Depth16Required, core.GetFormatSupport(TextureFormat.Depth16Unorm) & Depth16Required);
    }
}
