using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VulkanCallTests
{
    [Fact]
    public void Members_MatchNote()
    {
        // docs/design/graphics-vulkan.md section 12, in the note's order. A call added to the backend adds its member
        // to the note and here in the same change.
        string[] expected =
        [
            "EnumerateInstanceExtensionProperties", "EnumerateInstanceLayerProperties", "CreateInstance", "CreateDebugUtilsMessenger",
            "EnumerateDeviceExtensionProperties", "GetImageFormatProperties2", "CreateDevice",
            "GetSurfaceSupport", "GetSurfaceCapabilities", "GetSurfaceFormats", "GetSurfacePresentModes", "CreateSwapchain",
            "GetSwapchainImages", "AcquireNextImage", "QueuePresent", "WaitForPresentFence",
            "CreateCommandPool", "AllocateCommandBuffers", "CreateFence", "CreateSemaphore", "WaitForFences", "GetFenceStatus", "ResetFences",
            "ResetCommandPool", "BeginCommandBuffer", "EndCommandBuffer", "QueueSubmit2", "DeviceWaitIdle",
            "CreateVmaAllocator", "CreateBuffer", "CreateImage", "CreateImageView", "CreateSampler",
            "CreateDescriptorSetLayout", "CreateDescriptorPool", "ResetDescriptorPool", "AllocateDescriptorSets", "FreeDescriptorSets",
            "CreateShaderModule", "CreatePipelineLayout", "CreatePipelineCache", "CreateGraphicsPipelines", "CreateComputePipelines",
            "CreateQueryPool", "GetQueryPoolResults", "SetDebugUtilsObjectName",
        ];

        Assert.Equal(expected, Enum.GetNames<VulkanCall>());
    }

    [Fact]
    public void Members_AreContiguousFromZero()
    {
        // The fault injector indexes arrays by member.
        int[] values = [.. Enum.GetValues<VulkanCall>().Select(c => (int)c)];

        Assert.Equal(Enumerable.Range(0, values.Length), values);
    }

    [Fact]
    public void InternalsVisibleTo_IsExactlyTheTestProject()
    {
        // The fault seam is reachable only from this project (docs/design/graphics-vulkan.md section 12).
        string[] friends =
        [
            .. typeof(VulkanCall).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName),
        ];

        Assert.Equal(["BrutalEngine.Graphics.Vulkan.Tests"], friends);
    }
}
