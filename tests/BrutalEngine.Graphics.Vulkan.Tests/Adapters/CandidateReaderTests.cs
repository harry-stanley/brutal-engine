using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

public sealed class CandidateReaderTests
{
    private const string ExtensionsFailed = "vkEnumerateDeviceExtensionProperties failed";
    private const string SurfaceFailed = "vkGetPhysicalDeviceSurfaceSupportKHR failed";

    [Fact]
    public void Read_ApiBelow13_ChainsNoVulkan13Features()
    {
        // A 1.2 driver that would report both 1.3 features if a 1.3 structure were chained.
        FakeDevice device = new() { Minor = 2, Patch = 198, DynamicRendering = true, Synchronization2 = true };

        AdapterCandidate candidate = CandidateReader.Read(device, new VulkanCallGate()).Value;

        Assert.Equal([0], device.FeaturesChains);
        Assert.False(candidate.DynamicRendering);
        Assert.False(candidate.Synchronization2);
        Assert.Equal((1, 2, 198), (candidate.ApiVersionMajor, candidate.ApiVersionMinor, candidate.ApiVersionPatch));
    }

    // Driver properties are core from 1.2 and the 1.3 features from 1.3: each is chained from its version on.
    [Theory]
    [InlineData(0, 3, false, false)]
    [InlineData(1, 1, false, false)]
    [InlineData(1, 2, true, false)]
    [InlineData(1, 3, true, true)]
    [InlineData(1, 4, true, true)]
    [InlineData(2, 0, true, true)]
    public void Read_ApiVersion_ChainsOnlyStructuresTheVersionHas(int major, int minor, bool driver, bool vulkan13)
    {
        FakeDevice device = new() { Major = major, Minor = minor, Driver = VkDriverId.NvidiaProprietary, DynamicRendering = true, Synchronization2 = true };

        AdapterCandidate candidate = CandidateReader.Read(device, new VulkanCallGate()).Value;

        Assert.Equal(driver ? [0, FakeDevice.DriverPropertiesType] : [0], device.PropertiesChains);
        Assert.Equal(driver ? VkDriverId.NvidiaProprietary : 0, candidate.Driver);
        Assert.Equal([vulkan13 ? FakeDevice.Vulkan13FeaturesType : 0], device.FeaturesChains);
        Assert.Equal((vulkan13, vulkan13), (candidate.DynamicRendering, candidate.Synchronization2));
    }

    [Fact]
    public void Read_Vulkan13Device_ReadsPropertiesExtensionsAndFamilies()
    {
        FakeDevice device = new()
        {
            Name = "NVIDIA GeForce RTX 2070 SUPER",
            TimestampPeriod = 0.75f,
            Extensions = ["VK_KHR_maintenance5", "VK_KHR_swapchain"],
            Families =
            [
                (VkQueueFlags.TransferBit, 0, true),
                (VkQueueFlags.GraphicsBit | VkQueueFlags.ComputeBit, 64, true),
                (VkQueueFlags.ComputeBit, 36, true),
            ],
        };

        AdapterCandidate candidate = CandidateReader.Read(device, new VulkanCallGate()).Value;

        Assert.Equal("NVIDIA GeForce RTX 2070 SUPER", candidate.Name);
        Assert.Equal(AdapterType.Discrete, candidate.Type);
        Assert.Equal(VkDriverId.MESALlvmpipe, candidate.Driver);
        Assert.Equal((1, 3, 280), (candidate.ApiVersionMajor, candidate.ApiVersionMinor, candidate.ApiVersionPatch));
        Assert.Equal(0.75f, candidate.TimestampPeriod);
        Assert.Equal(["VK_KHR_maintenance5", "VK_KHR_swapchain"], candidate.DeviceExtensions.ToArray());

        // Without a surface, present support is false and never asked for, although every family would present.
        Assert.Equal(
            [new(0, false, false, false, 0), new(1, true, true, false, 64), new(2, false, true, false, 36)],
            candidate.QueueFamilies.ToArray());
        Assert.Empty(device.SurfaceQueries);
    }

    // One feature on at a time, so a feature read into the wrong field shows.
    [Theory]
    [InlineData("samplerAnisotropy")]
    [InlineData("textureCompressionBC")]
    [InlineData("textureCompressionASTC_LDR")]
    [InlineData("dynamicRendering")]
    [InlineData("synchronization2")]
    public void Read_OneFeature_SetsOnlyThatFlag(string feature)
    {
        FakeDevice device = new()
        {
            SamplerAnisotropy = feature == "samplerAnisotropy",
            TextureCompressionBC = feature == "textureCompressionBC",
            TextureCompressionAstcLdr = feature == "textureCompressionASTC_LDR",
            DynamicRendering = feature == "dynamicRendering",
            Synchronization2 = feature == "synchronization2",
        };

        AdapterCandidate c = CandidateReader.Read(device, new VulkanCallGate()).Value;

        Assert.Equal(
            (device.SamplerAnisotropy, device.TextureCompressionBC, device.TextureCompressionAstcLdr, device.DynamicRendering, device.Synchronization2),
            (c.SamplerAnisotropy, c.TextureCompressionBC, c.TextureCompressionAstcLdr, c.DynamicRendering, c.Synchronization2));
    }

    // By name: Vulkan's integrated (1) and discrete (2) values are swapped relative to AdapterType's.
    [Theory]
    [InlineData(VkPhysicalDeviceType.Other, AdapterType.Other)]
    [InlineData(VkPhysicalDeviceType.IntegratedGpu, AdapterType.Integrated)]
    [InlineData(VkPhysicalDeviceType.DiscreteGpu, AdapterType.Discrete)]
    [InlineData(VkPhysicalDeviceType.VirtualGpu, AdapterType.Virtual)]
    [InlineData(VkPhysicalDeviceType.Cpu, AdapterType.Cpu)]
    [InlineData((VkPhysicalDeviceType)5, AdapterType.Other)]
    [InlineData(VkPhysicalDeviceType.MaxEnum, AdapterType.Other)]
    public void Read_DeviceType_MapsByName(VkPhysicalDeviceType type, AdapterType expected)
    {
        Assert.Equal(expected, CandidateReader.Read(new FakeDevice { Type = type }, new VulkanCallGate()).Value.Type);
    }

    // A name that fills the 256-byte buffer without a terminator is read up to the buffer's end, never past it.
    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void Read_LongName_ReadsWithinTheBuffer(int length)
    {
        string name = new('n', length);

        Assert.Equal(name, CandidateReader.Read(new FakeDevice { Name = name }, new VulkanCallGate()).Value.Name);
    }

    [Fact]
    public void Read_WithSurface_AsksEveryFamilyForPresent()
    {
        FakeDevice device = new()
        {
            HasSurface = true,
            Families = [(VkQueueFlags.GraphicsBit, 64, false), (VkQueueFlags.GraphicsBit, 64, true), (VkQueueFlags.TransferBit, 0, false)],
        };

        AdapterCandidate candidate = CandidateReader.Read(device, new VulkanCallGate()).Value;

        Assert.Equal([0, 1, 2], device.SurfaceQueries);
        Assert.Equal([false, true, false], Array.ConvertAll(candidate.QueueFamilies.ToArray(), f => f.Present));
    }

    [Fact]
    public void Read_FewerFamiliesWritten_KeepsOnlyTheWrittenOnes()
    {
        FakeDevice device = new() { Families = [(VkQueueFlags.GraphicsBit, 64, false), (VkQueueFlags.ComputeBit, 64, false)], FamiliesWritten = 1 };

        Assert.Single(CandidateReader.Read(device, new VulkanCallGate()).Value.QueueFamilies.ToArray());
    }

    [Fact]
    public void Read_SurfaceSupportFails_ReturnsTheError()
    {
        FakeDevice device = new() { HasSurface = true, SurfaceResult = VkResult.ErrorSurfaceLostKHR };

        Result<AdapterCandidate> result = CandidateReader.Read(device, new VulkanCallGate());

        Assert.Equal(SurfaceFailed, result.Error.Message);
        Assert.Equal((int)VkResult.ErrorSurfaceLostKHR, result.Error.NativeCode);
    }

    [Fact]
    public void Read_InjectedSurfaceFault_SkipsTheCallAndStops()
    {
        FakeDevice device = new() { HasSurface = true, Families = [(VkQueueFlags.GraphicsBit, 64, true), (VkQueueFlags.GraphicsBit, 64, true), (VkQueueFlags.GraphicsBit, 64, true)] };

        Result<AdapterCandidate> result = CandidateReader.Read(device, Faulted(VulkanCall.GetSurfaceSupport, 2));

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, SurfaceFailed, (int)VkResult.ErrorOutOfHostMemory), result.Error);
        Assert.Equal([0], device.SurfaceQueries);
    }

    [Fact]
    public void Read_ExtensionsFail_ReturnsTheError()
    {
        Result<AdapterCandidate> result = CandidateReader.Read(new FakeDevice { CountResult = VkResult.ErrorOutOfHostMemory }, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, ExtensionsFailed, (int)VkResult.ErrorOutOfHostMemory), result.Error);
    }

    [Fact]
    public void Read_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("device", () => CandidateReader.Read(null!, default, new VulkanCallGate()));
        Assert.Throws<ArgumentNullException>("gate", () => CandidateReader.Read(new FakeDevice(), null!));
    }

    [Fact]
    public void ReadExtensions_PersistentIncomplete_FailsAfterLimit()
    {
        // Incomplete on every allowed attempt and complete on the one after, so a missing or loosened bound returns the
        // list instead of failing, and can't hang the run.
        FakeDevice device = new() { IncompleteFills = InstanceExtensions.MaxEnumerationAttempts };

        Result<string[]> result = CandidateReader.ReadExtensions(device, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, ExtensionsFailed, (int)VkResult.Incomplete), result.Error);

        // A count and a fill per attempt, and no attempt past the limit.
        Assert.Equal(2 * InstanceExtensions.MaxEnumerationAttempts, device.ExtensionCalls);
    }

    // Up to the last allowed attempt, a list that stops growing is read.
    [Theory]
    [InlineData(1)]
    [InlineData(InstanceExtensions.MaxEnumerationAttempts - 1)]
    public void ReadExtensions_IncompleteThenComplete_Retries(int incompleteFills)
    {
        FakeDevice device = new() { IncompleteFills = incompleteFills, Extensions = ["VK_KHR_swapchain", "VK_EXT_memory_budget"] };

        Result<string[]> result = CandidateReader.ReadExtensions(device, new VulkanCallGate());

        Assert.Equal(["VK_KHR_swapchain", "VK_EXT_memory_budget"], result.Value);
        Assert.Equal(2 * (incompleteFills + 1), device.ExtensionCalls);
    }

    [Fact]
    public void ReadExtensions_NoExtensions_ReturnsEmptyAfterTheCount()
    {
        FakeDevice device = new() { Extensions = [] };

        Assert.Empty(CandidateReader.ReadExtensions(device, new VulkanCallGate()).Value);
        Assert.Equal(1, device.ExtensionCalls);
    }

    private static VulkanCallGate Faulted(VulkanCall call, int occurrence)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(call, occurrence, VkResult.ErrorOutOfHostMemory);
        return new VulkanCallGate { Faults = faults };
    }
}
